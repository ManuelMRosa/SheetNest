namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using ClipperLib;
  using DeepNestLib;
  using DeepNestLib.NestProject;

  /// <summary>A placed part being compacted: its rotated polygon and the placement offset (inches).</summary>
  internal sealed class CompactItem
  {
    public INfp Poly;       // rotated part geometry (absolute points = Poly points + (X, Y))
    public double X;
    public double Y;
    public double Spacing;  // this part's NOMINAL spacing (in) — what it keeps to anything it cannot share a cut with
    public CommonCuttingMode Cc;  // who it may share a cut edge with; None = nobody
    public int ShareKey;    // identity for SamePart: same drawing AND same hand. Only compared, never ordered
  }

  /// <summary>
  /// EXACT-geometry compaction pass. The raster engine can only place parts on its pixel grid, and its
  /// conservative masks keep every pair 1-2 pixels further apart than asked (spacing-0 parts that should
  /// share an edge sit ~1-2 mm apart; spaced parts carry the same phantom slack on top of their spacing).
  /// This pass slides each placed part down then left in EXACT polygon space (Clipper, the same library
  /// the NFP engine uses) until it reaches its true limit: contact for common-line parts, the exact
  /// requested clearance for spaced parts, or the sheet margin. That concentrates the sheet's slack into
  /// one free region at the pack's end (where the refill pass can use it) instead of leaving it spread
  /// as N useless slivers. A small safety backoff (0.001") is kept so numeric noise can never produce
  /// real material interference. Translation only â€” rotations are never changed.
  /// </summary>
  internal static class RasterCompact
  {
    private const double Scale = 1e6;              // Clipper integer units per inch (120" sheet â‰ˆ 1.2e8 Â« long range)
    private const double EpsArea = 1e-6 * Scale * Scale; // ignore sub-1e-6 inÂ² contact slivers as "touching"
    private const double Backoff = 0.001;          // floor: extra slack left after a slide (0.001" = 0.025 mm)

    /// <summary>
    /// How much of the clearance being enforced a slide leaves as slack, on top of <see cref="Backoff"/>.
    /// <para>The slide stops where its own acceptance test is still happy, and that test is not the one the
    /// finished sheet is judged by. The gap between the two is proportional to the clearance, because both
    /// are polygonal approximations of an offset BY that clearance. A fixed figure is therefore right for
    /// exactly one size of number: 0.001 is a thousandth of an inch on an imperial job and a thousandth of
    /// a MILLIMETRE on a metric one, twenty five times less slack for a clearance twenty five times bigger.
    /// </para>
    /// <para>Measured on the job from issue #2 (2650x2070 mm, 9 mm spacing): the slide applied thirty moves
    /// its own test allowed and the judge rejects, and a pair came to rest 0.01 mm inside the 9 mm asked
    /// for. That is 0.0011 of the clearance; this leaves roughly three times it. Below an inch-sized
    /// clearance the floor still wins, so imperial jobs slide exactly as before.</para>
    /// </summary>
    private const double BackoffPerClearance = 0.003;

    /// <summary>
    /// Gap between common-line parts: ZERO — true common-line cutting means the shared edge is
    /// COINCIDENT so the DXF export (MergeLines) emits it as ONE cut line. The old 0.003" "CAM-safe"
    /// mini-gap produced two cuts three thou apart (sliver + double heat) — the opposite of what the
    /// shop wants. Exact touch is safe engine-side: overlap tests are AREA-based (edge contact = 0).
    /// </summary>
    internal const double CommonLineGap = 0.0;

    /// <summary>
    /// Safety floor for pairs that involve a SPACED part when vetting tight (halo-0) layouts —
    /// a jammed separation can leave a CC part nearly touching a neighbour that asked for real
    /// spacing. (Kept at the historic 0.003/2 hazard threshold.)
    /// </summary>
    internal const double MixedPairFloor = 0.0015;

    /// <summary>
    /// True when these two placements are allowed to share a cut edge, so the pair closes to CONTACT
    /// instead of to a clearance. The stricter of the two modes wins: a Same-part item shares only with
    /// its own kind no matter who is looking at it, which is what "Same part" means everywhere else.
    /// </summary>
    /// <remarks>
    /// This replaced a `Spacing &lt;= 0` test on both sides. That test conflated two different things —
    /// "shares a cut" and "asks for no clearance" — and the price was that a common-line part offered
    /// NOTHING to a spaced neighbour, so the pair closed to sB/2 instead of (sA + sB)/2. Measured on
    /// three 20" squares: a neighbour that asked for 0.5" got 0.2536".
    /// </remarks>
    internal static bool MayShare(CompactItem a, CompactItem b)
    {
      if (a.Cc == CommonCuttingMode.None || b.Cc == CommonCuttingMode.None)
      {
        return false;
      }

      if (a.Cc == CommonCuttingMode.SamePart || b.Cc == CommonCuttingMode.SamePart)
      {
        return a.ShareKey == b.ShareKey;
      }

      return true;
    }

    /// <summary>
    /// Compact one sheet's placements in place (mutates item X/Y). Every part slides toward the pack
    /// corner; clearances are preserved exactly â€” each pair keeps (spacingA + spacingB)/2 via
    /// half-inflated shells, and common-line pairs close to EXACT contact (shared cut edge).
    /// </summary>
    public static void Compact(IList<CompactItem> items, double sheetW, double sheetH, double margin, int[] groups = null, System.Threading.CancellationToken cancel = default)
    {
      if (items == null || items.Count < 1)
      {
        return;
      }

      // PAIR MODULES ride through compaction RIGIDLY (groups[i] > 0 marks members of one module).
      // Sliding the two halves independently let each wedge into the previous column's diagonal at a
      // different depth — columns leaned over and the strip grew 4%. A module has flat outer faces,
      // so moving it as one body keeps columns straight; its internal seam is born exact and must not
      // change. memberSets[i] is the SAME array instance for all members (reference equality = same group).
      var memberSets = new int[items.Count][];
      if (groups != null)
      {
        var byGroup = new Dictionary<int, List<int>>();
        for (int i = 0; i < items.Count && i < groups.Length; i++)
        {
          if (groups[i] > 0)
          {
            if (!byGroup.TryGetValue(groups[i], out var list))
            {
              byGroup[groups[i]] = list = new List<int>();
            }

            list.Add(i);
          }
        }

        foreach (var g in byGroup.Values)
        {
          if (g.Count > 1)
          {
            var arr = g.ToArray();
            foreach (int i in arr)
            {
              memberSets[i] = arr;
            }
          }
        }
      }

      // Clearance is PER PAIR: spacing pairs need (sA+sB)/2 â€” expressed by half-inflated shells â€” and
      // pairs that may SHARE a cut close to EXACT contact (CommonLineGap = 0: the shared edge exports as
      // one cut). The CC shell must NOT leak into shared-vs-spaced pairs (the raster already placed those
      // at exactly s/2, and inflating would make every one look violated), hence TWO shells per item.
      // Which shell a pair is judged by is MayShare's call, not the spacing's: a common-cut part still
      // carries its full spacing for everyone it cannot share with.
      var pathsRaw = items.Select(ToPaths).ToArray();
      var pathsHalf = items.Select(it => it.Spacing > 0 ? Inflate(ToPaths(it), it.Spacing / 2.0) : ToPaths(it)).ToArray();
      var pathsCC = items.Select(it => it.Cc != CommonCuttingMode.None ? Inflate(ToPaths(it), CommonLineGap / 2.0) : null).ToArray();
      // Box pre-filter for every pair test below, so it has to be the OUTER of an item's two shells or a
      // pair gets skipped before it is ever measured. pathsHalf always contains pathsCC (raw, inflated by
      // nothing). This used to read `pathsCC[i] ?? pathsHalf[i]` and was harmless only because a
      // common-line item had spacing 0, making both shells the same outline. Now that such an item keeps
      // its real spacing, taking the CC box let a spaced neighbour slide to half its clearance unchecked.
      var bounds = items.Select((it, i) => BoundsOf(pathsHalf[i])).ToArray();

      // HARD INVARIANT: no move may ever create a REAL overlap. The engine hands us an overlap-free
      // layout; every push/slide below is additionally checked against the raw outlines of ALL parts.
      bool RawClearAll(int m, double ox, double oy)
      {
        long tx = (long)Math.Round(ox * Scale);
        long ty = (long)Math.Round(oy * Scale);
        var set = memberSets[m];
        int n = set?.Length ?? 1;
        for (int s = 0; s < n; s++)
        {
          int mm = set != null ? set[s] : m;
          var moved = tx == 0 && ty == 0 ? pathsRaw[mm] : Translate(pathsRaw[mm], tx, ty);
          var mb = BoundsOf(moved);
          for (int k = 0; k < items.Count; k++)
          {
            if (k == mm || (set != null && memberSets[k] == set) || !BoxesTouch(mb, bounds[k]))
            {
              continue;
            }

            if (Intersects(moved, pathsRaw[k]))
            {
              return false;
            }
          }
        }

        return true;
      }

      bool BothCC(int i, int j) => MayShare(items[i], items[j]);

      bool ValidOffset(int i, double ox, double oy)
      {
        long tx = (long)Math.Round(ox * Scale);
        long ty = (long)Math.Round(oy * Scale);
        var set = memberSets[i];
        int n = set?.Length ?? 1;
        for (int s = 0; s < n; s++)
        {
          int mm = set != null ? set[s] : i;
          var movedHalf = tx == 0 && ty == 0 ? pathsHalf[mm] : Translate(pathsHalf[mm], tx, ty);
          var movedCC = pathsCC[mm] == null ? null : tx == 0 && ty == 0 ? pathsCC[mm] : Translate(pathsCC[mm], tx, ty);
          // The OUTER shell, like the bounds array it is matched against. Taking the CC box here skipped
          // the pair before it was ever measured whenever the MOVER was common-cut and the neighbour was
          // not, and a common-cut part then slid to half its neighbour's clearance. That combination only
          // became reachable with Same part, where one part shares with its own kind and with nobody else.
          var mb = BoundsOf(movedHalf);
          for (int j = 0; j < items.Count; j++)
          {
            if (j == mm || (set != null && memberSets[j] == set) || !BoxesTouch(mb, bounds[j]))
            {
              continue;
            }

            bool hit = BothCC(mm, j)
              ? Intersects(movedCC, pathsCC[j])
              : Intersects(movedHalf, pathsHalf[j]);
            if (hit)
            {
              return false;
            }
          }
        }

        return true;
      }

      void ApplyMove(int i, double ox, double oy)
      {
        long tx = (long)Math.Round(ox * Scale);
        long ty = (long)Math.Round(oy * Scale);
        var set = memberSets[i];
        int n = set?.Length ?? 1;
        for (int s = 0; s < n; s++)
        {
          int mm = set != null ? set[s] : i;
          items[mm].X += ox;
          items[mm].Y += oy;
          pathsRaw[mm] = Translate(pathsRaw[mm], tx, ty);
          pathsHalf[mm] = Translate(pathsHalf[mm], tx, ty);
          if (pathsCC[mm] != null)
          {
            pathsCC[mm] = Translate(pathsCC[mm], tx, ty);
          }

          bounds[mm] = BoundsOf(pathsHalf[mm]); // the OUTER shell, same reason as where bounds is built
        }
      }

      // Compact toward the same corner the nester packs to: the pack grows along the sheet's LONGER
      // axis, so push primarily along that axis (keeps the remnant a clean short-dimension strip).
      bool growX = sheetW > sheetH;

      // Every part moves (spaced parts stop at their exact clearance, so sliding them is always safe).
      // Parts nearest the target corner settle first so the others can lean on their final positions.
      // The order runs along the SETTLE axis (the secondary axis, slid first below): for growX the
      // stack settles downward, so process bottom-up — ordering along X here split interlocked pair
      // mates apart (their X differs by ~2px), so a part's -Y slide landed on a mate that had not
      // settled yet and the whole column hung 0.07-0.32" high (the user's uneven columns).
      var movable = Enumerable.Range(0, items.Count);
      var order = (growX
          ? movable.OrderBy(i => items[i].Y + items[i].Poly.MinY).ThenBy(i => items[i].X + items[i].Poly.MinX)
          : movable.OrderBy(i => items[i].X + items[i].Poly.MinX).ThenBy(i => items[i].Y + items[i].Poly.MinY))
        .ToArray();

      // SEPARATION pre-pass: the raster can place CC parts literally touching (gap 0), and a touching
      // CC pair may be sandwiched against exactly-spaced neighbours — so violations are resolved as an
      // iterative PAIR relaxation: each violating pair pushes its outer member the minimum distance
      // along the dominant axis between their centres (sliding along a shared edge separates nothing),
      // and follow-up iterations cascade the shove through the chain. Spaced parts may shift outward a
      // few thousandths; their own clearances only grow.
      bool PairClear(int a, int b, double ox, double oy)
      {
        bool cc = BothCC(a, b);
        var pa = cc ? pathsCC[a] : pathsHalf[a];
        var pb = cc ? pathsCC[b] : pathsHalf[b];
        var moved = ox == 0 && oy == 0 ? pa : Translate(pa, (long)Math.Round(ox * Scale), (long)Math.Round(oy * Scale));
        return !BoxesTouch(BoundsOf(moved), BoundsOf(pb)) || !Intersects(moved, pb);
      }

      // A chain of touching parts frees ONE link per iteration (a part capped at raw contact must wait
      // for its blocker to move first), so the budget scales with the part count; the loop also stops
      // as soon as an iteration produces no movement (truly jammed).
      int maxIterations = System.Math.Min(128, items.Count + 8);
      for (int iter = 0; iter < maxIterations; iter++)
      {
        cancel.ThrowIfCancellationRequested();
        bool anyViolation = false;
        bool movedAny = false;
        for (int a = 0; a < items.Count; a++)
        {
          for (int b = a + 1; b < items.Count; b++)
          {
            if ((memberSets[a] != null && memberSets[a] == memberSets[b])
                || !BoxesTouch(bounds[a], bounds[b]) || PairClear(a, b, 0, 0))
            {
              continue; // module mates are born at their exact internal seam — never "violated"
            }

            anyViolation = true;

            // The push must act along the CONTACT NORMAL, which is NOT necessarily the axis the
            // centres differ most on (a staircase of wide flat parts touches top-to-bottom while the
            // centres are offset mostly in X — pushing X just slides along the shared edge forever).
            // Try BOTH axes and keep whichever separates the pair with the SHORTEST push.
            double cxa = items[a].X + ((items[a].Poly.MinX + items[a].Poly.MaxX) / 2.0);
            double cya = items[a].Y + ((items[a].Poly.MinY + items[a].Poly.MaxY) / 2.0);
            double cxb = items[b].X + ((items[b].Poly.MinX + items[b].Poly.MaxX) / 2.0);
            double cyb = items[b].Y + ((items[b].Poly.MinY + items[b].Poly.MaxY) / 2.0);

            int pushed = -1;
            double dx = 0, dy = 0, hi = double.MaxValue;

            // Candidate pushes: the pair's outer member outward (+X/+Y) and its INNER member the other
            // way (−X/−Y) — a chain jammed against the sheet's far edge can only open backwards into
            // free space. Keep whichever valid push is shortest.
            foreach (var (axisX, positive) in new[] { (true, true), (false, true), (true, false), (false, false) })
            {
              int mover = axisX
                ? (positive ? (cxb >= cxa ? b : a) : (cxb >= cxa ? a : b))
                : (positive ? (cyb >= cya ? b : a) : (cyb >= cya ? a : b));
              int anchor2 = mover == a ? b : a;
              double sign = positive ? 1 : -1;
              double ddx = axisX ? sign : 0;
              double ddy = axisX ? 0 : sign;
              var mi = items[mover];
              double roof = axisX
                ? (positive ? sheetW - margin - (mi.X + mi.Poly.MaxX) : (mi.X + mi.Poly.MinX) - margin)
                : (positive ? sheetH - margin - (mi.Y + mi.Poly.MaxY) : (mi.Y + mi.Poly.MinY) - margin);

              double axisHi = -1;
              foreach (double probe in new[] { 0.01, 0.05, 0.1, 0.25 })
              {
                if (probe > roof)
                {
                  break;
                }

                if (PairClear(mover, anchor2, ddx * probe, ddy * probe))
                {
                  axisHi = probe;
                  break;
                }
              }

              if (axisHi < 0)
              {
                continue;
              }

              double lo2 = 0;
              for (int k = 0; k < 18; k++)
              {
                double mid = (lo2 + axisHi) / 2.0;
                if (PairClear(mover, anchor2, ddx * mid, ddy * mid))
                {
                  axisHi = mid;
                }
                else
                {
                  lo2 = mid;
                }
              }

              if (axisHi < hi)
              {
                hi = axisHi;
                pushed = mover;
                dx = ddx;
                dy = ddy;
              }
            }

            if (pushed < 0)
            {
              continue; // no room to open this pair — leave it for the operator
            }

            // The push may not RAM a third part: cap it at raw contact (minus a hair). The pair then
            // stays partially violated and the NEXT iteration pushes the blocking part in turn.
            if (!RawClearAll(pushed, dx * hi, dy * hi))
            {
              double lo2 = 0, hi2 = hi;
              for (int k = 0; k < 18; k++)
              {
                double mid = (lo2 + hi2) / 2.0;
                if (RawClearAll(pushed, dx * mid, dy * mid))
                {
                  lo2 = mid;
                }
                else
                {
                  hi2 = mid;
                }
              }

              hi = lo2 - 0.0005;
            }

            if (hi <= 1e-6)
            {
              continue; // jammed solid — no overlap is created, the gap just stays sub-minimal here
            }

            ApplyMove(pushed, dx * hi, dy * hi);
            movedAny = true;
          }
        }

        if (!anyViolation || !movedAny)
        {
          break;
        }
      }

      // Slide item i as far as possible along (dirX, dirY); returns true if it moved.
      bool Slide(int i, int dirX, int dirY)
      {
        // Furthest the part could go: down to the sheet margin (sliding only ever shrinks the extent,
        // so the far edges can't be violated). For a rigid module: the closest member bounds it.
        double max = double.MaxValue;
        double clearance = 0;
        var slideSet = memberSets[i];
        int slideN = slideSet?.Length ?? 1;
        for (int s = 0; s < slideN; s++)
        {
          var it = items[slideSet != null ? slideSet[s] : i];
          max = Math.Min(max, dirX != 0 ? (it.X + it.Poly.MinX) - margin : (it.Y + it.Poly.MinY) - margin);
          clearance = Math.Max(clearance, it.Spacing);
        }

        double backoff = Math.Max(Backoff, clearance * BackoffPerClearance);

        if (max <= backoff)
        {
          return false;
        }

        // Pre-probe: a part that cannot move even 2× the backoff nets a no-op slide — skip the whole
        // binary search (most parts are already blocked in round 2, and each probe is a Clipper call).
        if (!ValidOffset(i, dirX * System.Math.Min(max, backoff * 2.0), dirY * System.Math.Min(max, backoff * 2.0)))
        {
          return false;
        }

        double slide;
        if (ValidOffset(i, dirX * max, dirY * max))
        {
          slide = max;
        }
        else
        {
          // Largest collision-free slide: adaptive binary search to half the backoff (finer precision
          // is invisible behind the 0.001" backoff, and every iteration is a Clipper intersection —
          // with ALL parts sliding, the old fixed 30 iterations dominated the nest's wall time).
          double lo = 0, hi = max;
          while (hi - lo > backoff / 2.0)
          {
            double mid = (lo + hi) / 2.0;
            if (ValidOffset(i, dirX * mid, dirY * mid))
            {
              lo = mid;
            }
            else
            {
              hi = mid;
            }
          }

          slide = lo;
        }

        slide -= backoff;
        if (slide <= 1e-9)
        {
          return false;
        }

        ApplyMove(i, dirX * slide, dirY * slide);
        return true;
      }

      // Rounds run until nothing moves (capped): a part freed by a neighbour's move takes the slack
      // next round, and stacked chains need as many rounds as their depth — the old fixed two left
      // parts hanging 0.07-0.32" above a late-settling neighbour, beyond the weld's reach.
      var processed = new bool[items.Count];
      for (int round = 0; round < 8; round++)
      {
        bool moved = false;
        Array.Clear(processed, 0, processed.Length);
        foreach (int i in order)
        {
          cancel.ThrowIfCancellationRequested();
          if (processed[i])
          {
            continue; // already moved as part of its rigid module this round
          }

          var iSet = memberSets[i];
          if (iSet != null)
          {
            foreach (int mm in iSet)
            {
              processed[mm] = true;
            }
          }

          // SECONDARY axis first. Sliding the growth axis first pins a part against its previous
          // column, and if that column's face is DIAGONAL (parallelogram pair modules — e.g. paired
          // scalene triangles) the pinned part can no longer settle down: measured 0.2-0.31" gaps
          // between stacked modules from the third one up, while the first (neighbour-less) column
          // stacked tight. Settling the secondary axis first stacks every column flat, then the
          // growth-axis slide wedges the whole stack as deep as the diagonal allows.
          if (growX)
          {
            moved |= Slide(i, 0, -1); // down (settle the stack)
            moved |= Slide(i, -1, 0); // left (growth axis)
          }
          else
          {
            moved |= Slide(i, -1, 0); // left (settle the stack)
            moved |= Slide(i, 0, -1); // down (growth axis)
          }
        }

        if (!moved)
        {
          break;
        }
      }

      // WELD PASS: the slide's binary search + backoff leaves every contact ~0.001-0.002" short.
      // For common-line parts that residue defeats the feature — the shared edge must be EXACTLY
      // coincident for the DXF export (MergeLines) to emit it as one cut — so close each CC part the
      // last fraction precisely, by geometry: the exact directional gap to the nearest constraint
      // (sheet margin, a CC neighbour's raw outline, or a spaced neighbour's clearance shell).
      // Residues CASCADE down a contact chain (each weld moves the wall the next part rests on —
      // measured 0.005" on the 5th part of a row), hence pack order, a cap generous enough for long
      // chains, and a second sweep. The cap cannot jump real open space: the slide above already
      // consumed all genuine free room, so any post-slide gap to a blocker is residue by construction.
      double weldMax = 0.05;
      long weldMaxInt = (long)Math.Round(weldMax * Scale);
      long marginInt = (long)Math.Round(margin * Scale);

      // The weld closes toward -X and -Y, so a part has to settle AFTER whatever it comes to rest
      // against. `order` above is deliberately built from the INPUT positions (see the comment there:
      // ordering the SLIDE along X split interlocked module mates and left whole columns hanging), but
      // by the time the weld runs every part has moved, and inheriting that order welds parts before
      // their own anchors. Measured on the six-bar row: bar 5 closed onto bar 1, then bar 1 moved
      // 0.002217 further left and reopened the seam from behind. Re-sorting on the SETTLED positions
      // is local to the weld, so the slide's ordering - and the fix it carries - is untouched.
      var weldOrder = (growX
          ? order.OrderBy(i => items[i].X + items[i].Poly.MinX).ThenBy(i => items[i].Y + items[i].Poly.MinY)
          : order.OrderBy(i => items[i].Y + items[i].Poly.MinY).ThenBy(i => items[i].X + items[i].Poly.MinX))
        .ToArray();

      // Sweeps run until nothing moves. Two was not enough once a cascade could still shift a part
      // that a later part had already closed onto; the loop breaks the moment a sweep welds nothing,
      // so a converged layout still costs exactly one extra measuring sweep.
      for (int sweep = 0; sweep < 8; sweep++)
      {
        bool welded = false;
        Array.Clear(processed, 0, processed.Length);
        foreach (int i in weldOrder)
        {
          cancel.ThrowIfCancellationRequested();
          if (items[i].Cc == CommonCuttingMode.None || processed[i])
          {
            continue;
          }

          var iSet = memberSets[i];
          if (iSet != null)
          {
            foreach (int mm in iSet)
            {
              processed[mm] = true;
            }
          }

          for (int pass = 0; pass < 2; pass++)
          {
            bool alongX = growX == (pass == 0);
            long gap = DirectionalGapExact(i, alongX, weldMaxInt, marginInt, items, pathsRaw, pathsHalf, memberSets);
            if (gap > 0 && gap <= weldMaxInt)
            {
              double g = gap / Scale;
              double ox = alongX ? -g : 0;
              double oy = alongX ? 0 : -g;
              // ValidOffset as well as RawClearAll. RawClearAll only forbids REAL overlap, which was the
              // whole rule back when the weld ran solely on jobs where every part could touch every
              // other. It cannot see a clearance, so on its own it lets the weld close onto a neighbour
              // this part may not share a cut with. ValidOffset is the pair rule; keep both, since the
              // cheap one still guards the hard invariant.
              if (RawClearAll(i, ox, oy) && ValidOffset(i, ox, oy))
              {
                ApplyMove(i, ox, oy);
                welded = true;
              }
            }
          }
        }

        if (!welded)
        {
          break;
        }
      }
    }

    /// <summary>
    /// Exact directional clearance (integer Clipper units) from item <paramref name="i"/>'s raw
    /// outline to the nearest constraint when sliding toward the pack corner along one axis:
    /// the sheet margin, a common-line neighbour's RAW outline (contact allowed), or a spaced
    /// neighbour's half-inflated clearance shell. Vertex-vs-edge ray casting both ways — exact,
    /// no search. Returns long.MaxValue when nothing constrains within range.
    /// </summary>
    private static long DirectionalGapExact(
      int i,
      bool alongX,
      long capInt,
      long marginInt,
      IList<CompactItem> items,
      List<List<IntPoint>>[] pathsRaw,
      List<List<IntPoint>>[] pathsHalf,
      int[][] memberSets = null)
    {
      // Work in a rotated frame where the slide is always -X: U = slide axis, V = the other.
      long U(IntPoint p) => alongX ? p.X : p.Y;
      long V(IntPoint p) => alongX ? p.Y : p.X;

      // A rigid module measures from ALL its members' outlines and ignores its own mates.
      var iSet = memberSets?[i];
      List<List<IntPoint>> Mine(List<List<IntPoint>>[] source)
      {
        if (iSet == null)
        {
          return source[i];
        }

        var all = new List<List<IntPoint>>();
        foreach (int mm in iSet)
        {
          all.AddRange(source[mm]);
        }

        return all;
      }

      // TWO shells for the MOVER, the same way every pair test upstream keeps two: raw where the pair may
      // share a cut, half-inflated where it may not. Measuring the mover raw against a spaced neighbour's
      // shell asks for sB/2 instead of (sA+sB)/2, and the weld then closes onto it — the very halving
      // this file already fixed for the slide, which survived here because the weld only ever ran on jobs
      // where EVERY part could touch every other. Reported as parts turning red on a Same part nest.
      var mineRaw = Mine(pathsRaw);
      var mineHalf = Mine(pathsHalf);

      (long MinU, long MaxU, long MinV, long MaxV) FrameBounds(List<List<IntPoint>> paths)
      {
        long minU = long.MaxValue, maxU = long.MinValue, minV = long.MaxValue, maxV = long.MinValue;
        foreach (var path in paths)
        {
          foreach (var p in path)
          {
            long u = U(p);
            long v = V(p);
            if (u < minU) { minU = u; }
            if (u > maxU) { maxU = u; }
            if (v < minV) { minV = v; }
            if (v > maxV) { maxV = v; }
          }
        }

        return (minU, maxU, minV, maxV);
      }

      var rawBox = FrameBounds(mineRaw);
      var halfBox = FrameBounds(mineHalf);

      long best = long.MaxValue;

      // The sheet margin applies to the PART, not to its clearance shell, so it is measured raw.
      best = Math.Min(best, rawBox.MinU - marginInt);

      // Ray from vertex v toward -U against edge (a,b): hits where the edge spans v's V coordinate.
      long RayGap(IntPoint v, IntPoint a, IntPoint b)
      {
        long va = V(a);
        long vb = V(b);
        if (va == vb)
        {
          // Edge parallel to the ray: only exact V alignment can constrain; endpoints handle it.
          if (V(v) != va)
          {
            return long.MaxValue;
          }

          long nearest = Math.Max(U(a), U(b)) <= U(v) ? Math.Max(U(a), U(b)) : long.MaxValue;
          return nearest == long.MaxValue ? long.MaxValue : U(v) - nearest;
        }

        if (V(v) < Math.Min(va, vb) || V(v) > Math.Max(va, vb))
        {
          return long.MaxValue;
        }

        // U on the edge at the ray's V, exact rational rounded to nearest integer unit.
        double t = (double)(V(v) - va) / (vb - va);
        long uEdge = (long)Math.Round(U(a) + (t * (U(b) - U(a))));
        return uEdge <= U(v) ? U(v) - uEdge : long.MaxValue;
      }

      for (int j = 0; j < items.Count; j++)
      {
        if (j == i || (iSet != null && memberSets[j] == iSet))
        {
          continue;
        }

        // Both sides of the pair, or neither: shell against shell for a pair that must keep a clearance,
        // outline against outline for one that may share the cut.
        bool share = MayShare(items[i], items[j]);
        var theirs = share ? pathsRaw[j] : pathsHalf[j];
        var mine = share ? mineRaw : mineHalf;
        var (myMinU, myMaxU, myMinV, myMaxV) = share ? rawBox : halfBox;

        // BBox pre-filter (essential: without it an 800-part common-line job timed out — this scan
        // is O(vertsA x vertsB) per pair). The constraint can only bite if it overlaps my V-range
        // and sits within [myMinU - cap, myMaxU] along the slide axis.
        long thMinU = long.MaxValue, thMaxU = long.MinValue, thMinV = long.MaxValue, thMaxV = long.MinValue;
        foreach (var path in theirs)
        {
          foreach (var p in path)
          {
            long u = U(p);
            long v = V(p);
            if (u < thMinU) { thMinU = u; }
            if (u > thMaxU) { thMaxU = u; }
            if (v < thMinV) { thMinV = v; }
            if (v > thMaxV) { thMaxV = v; }
          }
        }

        // Three of these four bounds are CLOSED (>=, <=), and that is load-bearing rather than
        // cosmetic: a neighbour that only touches cannot block, but measured as if it could it
        // returns a gap of exactly 0, and the `best == 0` short-circuit below then freezes this part
        // along this axis for the rest of the pass. Both halves of that were measured on a six-bar
        // common-line row, and between them they accounted for EVERY seam this pass failed to close.
        //
        //   thMinU >= myMaxU  - they sit entirely at or beyond my leading edge, so sliding -U only
        //                       opens the pair. Touching one used to read 0 and pin the part: in the
        //                       row, bar 5 welded first, landed flush against bar 1's trailing edge,
        //                       and bar 1 could then never close its own seam (left open 0.003353).
        //   V ranges touching at a single coordinate - contact along one line has NO AREA, and the
        //                       predicate that vets the move (Intersects, area-based with EpsArea)
        //                       agrees it is not a collision. Measuring it as one contradicted the
        //                       vetting rule: four of six bars were held 0.001 off the sheet margin
        //                       by a side-by-side neighbour whose corner grazed the ray's origin.
        //
        // Both rules are conservative by construction - they only ever DROP a pair that cannot
        // produce an area overlap - so the hard no-interference invariant is untouched.
        if (thMinV >= myMaxV || thMaxV <= myMinV || thMinU >= myMaxU || thMaxU < myMinU - capInt)
        {
          continue;
        }

        // My vertices raying -U onto their edges.
        foreach (var pathA in mine)
        {
          foreach (var v in pathA)
          {
            if (V(v) < thMinV || V(v) > thMaxV || U(v) < thMinU || U(v) - thMaxU > capInt)
            {
              continue; // this vertex can't reach them within the cap
            }

            foreach (var pathB in theirs)
            {
              for (int n = 0; n < pathB.Count; n++)
              {
                long g1 = RayGap(v, pathB[n], pathB[(n + 1) % pathB.Count]);
                if (g1 < best)
                {
                  best = g1;
                }
              }
            }
          }
        }

        // Their vertices raying +U onto my edges (equivalent to me sliding -U onto them).
        foreach (var pathB in theirs)
        {
          foreach (var w in pathB)
          {
            if (V(w) < myMinV || V(w) > myMaxV || U(w) > myMaxU || myMinU - U(w) > capInt)
            {
              continue;
            }

            foreach (var pathA in mine)
            {
              for (int m = 0; m < pathA.Count; m++)
              {
                long g2 = RayGapReverse(w, pathA[m], pathA[(m + 1) % pathA.Count]);
                if (g2 < best)
                {
                  best = g2;
                }
              }
            }
          }
        }

        if (best == 0)
        {
          return 0;
        }
      }

      return best;

      long RayGapReverse(IntPoint w, IntPoint a, IntPoint b)
      {
        long va = V(a);
        long vb = V(b);
        if (va == vb)
        {
          if (V(w) != va)
          {
            return long.MaxValue;
          }

          long nearest = Math.Min(U(a), U(b)) >= U(w) ? Math.Min(U(a), U(b)) : long.MaxValue;
          return nearest == long.MaxValue ? long.MaxValue : nearest - U(w);
        }

        if (V(w) < Math.Min(va, vb) || V(w) > Math.Max(va, vb))
        {
          return long.MaxValue;
        }

        double t = (double)(V(w) - va) / (vb - va);
        long uEdge = (long)Math.Round(U(a) + (t * (U(b) - U(a))));
        return uEdge >= U(w) ? uEdge - U(w) : long.MaxValue;
      }
    }

    /// <summary>
    /// True when every pair that involves a SPACED part keeps at least <paramref name="floor"/>
    /// inches of clearance. Used to vet tight-packed (halo-0) layouts after compaction: a chain born
    /// touching can jam mid-separation, and the separation pre-pass caps pushes at RAW contact — so
    /// a common-line part can end up ~0.0005" from a SPACED neighbour that asked for real spacing.
    /// CC-CC pairs are exempt: exact contact is the GOAL there (the shared edge exports as one cut).
    /// </summary>
    internal static bool CommonLineGapsOk(IList<CompactItem> items, double floor)
    {
      var raw = items.Select(ToPaths).ToArray();
      var grown = items.Select((it, i) => Inflate(raw[i], floor)).ToArray();
      var rawBounds = raw.Select(BoundsOf).ToArray();
      long pad = (long)Math.Round(floor * Scale) + 1;
      for (int i = 0; i < items.Count; i++)
      {
        var bi = rawBounds[i];
        var padded = new IntRect(bi.left - pad, bi.top - pad, bi.right + pad, bi.bottom + pad);
        for (int j = i + 1; j < items.Count; j++)
        {
          if (MayShare(items[i], items[j]))
          {
            continue; // shared-cut pair — exact contact is intended
          }

          if (!BoxesTouch(padded, rawBounds[j]))
          {
            continue;
          }

          if (Intersects(grown[i], raw[j]))
          {
            return false;
          }
        }
      }

      return true;
    }

    /// <summary>
    /// Exact-geometry acceptance gate for REFILL insertions: the candidate must keep every pair's
    /// required clearance against ALL placed parts â€” (spacingA + spacingB)/2, or exact contact for a
    /// common-line pair â€” and its real contour must stay inside the sheet margin. The raster scan
    /// that proposed the position is conservative, so this should always pass; it is the safety net
    /// that guarantees a refill insertion can never violate what the compacted layout promised.
    /// </summary>
    internal static bool CandidateClear(IList<CompactItem> items, CompactItem cand, double sheetW, double sheetH, double margin)
    {
      const double Eps = 0.0005; // do not reject the raster's own integer-pixel clearance over noise

      if (cand.X + cand.Poly.MinX < margin - Eps || cand.Y + cand.Poly.MinY < margin - Eps
        || cand.X + cand.Poly.MaxX > sheetW - margin + Eps || cand.Y + cand.Poly.MaxY > sheetH - margin + Eps)
      {
        return false;
      }

      var candRaw = ToPaths(cand);
      var grownByFloor = new Dictionary<double, List<List<IntPoint>>>();
      foreach (var it in items)
      {
        double req = MayShare(cand, it)
          ? CommonLineGap
          : (Math.Max(0, cand.Spacing) + Math.Max(0, it.Spacing)) / 2.0;
        double floor = Math.Max(0, req - Eps);

        var itRaw = ToPaths(it);
        long pad = (long)Math.Round(floor * Scale) + 1;
        var cb = BoundsOf(candRaw);
        var padded = new IntRect(cb.left - pad, cb.top - pad, cb.right + pad, cb.bottom + pad);
        if (!BoxesTouch(padded, BoundsOf(itRaw)))
        {
          continue;
        }

        if (!grownByFloor.TryGetValue(floor, out var grown))
        {
          grown = Inflate(candRaw, floor);
          grownByFloor[floor] = grown;
        }

        if (Intersects(grown, itRaw))
        {
          return false;
        }
      }

      return true;
    }

    /// <summary>
    /// Grow the OUTER contour by <paramref name="inches"/> (round join = uniform Euclidean clearance,
    /// matching the raster halo). Holes are kept as-is â€” conservative for spacing purposes.
    /// </summary>
    private static List<List<IntPoint>> Inflate(List<List<IntPoint>> paths, double inches)
    {
      if (inches <= 0 || paths.Count == 0)
      {
        return paths;
      }

      var outer = new List<IntPoint>(paths[0]);
      if (!Clipper.Orientation(outer))
      {
        outer.Reverse(); // positive offset expands only positively-oriented paths
      }

      // ClipperOffset's default ArcTolerance is ABSOLUTE (0.25 integer units), while the point count a
      // round join is resampled into is pi/acos(1 - tolerance/delta) — so it grows with the offset
      // measured in INTEGER units, not with the part. A metric job offsets ~25x more units than the same
      // job in inches (9 mm spacing at 1e6 = 4.5e6 vs 0.354" = 1.77e5), which blew every clearance shell
      // up to ~10k vertices and made each collision test ~13x dearer — a 14-part metric sheet spent
      // minutes here. Tying the tolerance to the offset keeps the shell's accuracy relative (0.05% of the
      // offset, i.e. ~100 points per full circle) and identical in every unit system.
      var offset = new ClipperOffset(2.0, Math.Abs(inches * Scale) * 0.0005);
      offset.AddPath(outer, JoinType.jtRound, EndType.etClosedPolygon);
      var grown = new List<List<IntPoint>>();
      offset.Execute(ref grown, inches * Scale);

      var result = new List<List<IntPoint>>(grown);
      for (int i = 1; i < paths.Count; i++)
      {
        result.Add(paths[i]);
      }

      return result.Count > 0 ? result : paths;
    }

    private static List<List<IntPoint>> ToPaths(CompactItem it)
    {
      var paths = new List<List<IntPoint>> { ContourToPath(it.Poly, it.X, it.Y) };
      if (it.Poly.Children != null)
      {
        foreach (var hole in it.Poly.Children)
        {
          paths.Add(ContourToPath(hole, it.X, it.Y));
        }
      }

      return paths;
    }

    private static List<IntPoint> ContourToPath(INfp contour, double dx, double dy)
    {
      var path = new List<IntPoint>(contour.Points.Length);
      foreach (var p in contour.Points)
      {
        path.Add(new IntPoint((long)Math.Round((p.X + dx) * Scale), (long)Math.Round((p.Y + dy) * Scale)));
      }

      return path;
    }

    private static List<List<IntPoint>> Translate(List<List<IntPoint>> paths, long dx, long dy)
    {
      var moved = new List<List<IntPoint>>(paths.Count);
      foreach (var path in paths)
      {
        var np = new List<IntPoint>(path.Count);
        foreach (var p in path)
        {
          np.Add(new IntPoint(p.X + dx, p.Y + dy));
        }

        moved.Add(np);
      }

      return moved;
    }

    /// <summary>True when the two polygons overlap in real area (holes respected via even-odd).</summary>
    private static bool Intersects(List<List<IntPoint>> a, List<List<IntPoint>> b)
    {
      var clipper = new Clipper();
      clipper.AddPaths(a, PolyType.ptSubject, true);
      clipper.AddPaths(b, PolyType.ptClip, true);
      var solution = new List<List<IntPoint>>();
      clipper.Execute(ClipType.ctIntersection, solution, PolyFillType.pftEvenOdd, PolyFillType.pftEvenOdd);
      double area = 0;
      foreach (var path in solution)
      {
        area += Math.Abs(Clipper.Area(path));
      }

      return area > EpsArea;
    }

    private static IntRect BoundsOf(List<List<IntPoint>> paths)
    {
      long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
      foreach (var path in paths)
      {
        foreach (var p in path)
        {
          minX = Math.Min(minX, p.X);
          maxX = Math.Max(maxX, p.X);
          minY = Math.Min(minY, p.Y);
          maxY = Math.Max(maxY, p.Y);
        }
      }

      return new IntRect(minX, minY, maxX, maxY);
    }

    private static bool BoxesTouch(IntRect a, IntRect b)
    {
      return a.left <= b.right && b.left <= a.right && a.top <= b.bottom && b.top <= a.bottom;
    }
  }
}
