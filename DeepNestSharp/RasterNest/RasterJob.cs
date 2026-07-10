namespace DeepNestSharp.RasterNest
{
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;

  internal sealed class PartType
  {
    public int Source;
    public INfp Poly;
    public int Quantity;
    public int[] RotationsDeg;
    public RasterMask[] Masks;
    public PackedMask[] Packed; // 64-bit packed rows of Masks, built by the nester
    public int Priority = 5;    // 0-10, higher nests first
    public int HaloPx = 0;      // PER-PART spacing halo (px): masks dilate by this, so two parts end up (haloA + haloB) apart
    public bool CommonLine;     // spacing<=0 part: its pair module is born with the internal seam at EXACT contact (shared cut)

    // PAIR CLUSTERING (industry "pairwise nesting"): parts that interlock with their own 180°
    // rotation — a triangle plus its flip is a parallelogram — get extra PAIR MODULE entries
    // appended to Masks/Packed/RotationsDeg. The scanner places a module like any rotation (so it
    // competes on position with singles) and the placement loop then emits TWO placements.
    // Dropping the buddy AFTER seating the seed was tried first and failed: the buddy's slot is
    // usually already occupied (measured 23 fits out of 77 tries on 100 triangles).
    public int FirstPairEntry = -1; // index into Masks where pair modules start; -1 = none
    public int[] PairSubA;          // per pair entry: the two REAL rotation indices…
    public int[] PairSubB;
    public int[] PairAx;            // …and each sub-part's offset inside the module frame
    public int[] PairAy;
    public int[] PairBx;
    public int[] PairBy;

    // Common-line pairs only: EXACT sub-part offsets (inches, doubles) inside the module frame.
    // The integer-pixel pair offset leaves the internal seam ~1px (0.04") open — a double cut on
    // the shared edge. These exact offsets, computed by polygon ray-cast, close it to true contact;
    // the placement carries them past the pixel grid and compaction keeps the module rigid.
    public double[] PairExactAx;
    public double[] PairExactAy;
    public double[] PairExactBx;
    public double[] PairExactBy;
  }

  /// <summary>
  /// A mask packed 64 columns per word (bit j of word w = column w*64+j), so the collision test ANDs
  /// whole words instead of walking cells one by one. Purely a faster representation — the fit/collide
  /// answer is bit-for-bit the same as the byte scan it replaces (which took minutes on real parts:
  /// mixed rotations leave a jagged frontier where near-fit positions scanned thousands of cells).
  /// </summary>
  internal sealed class PackedMask
  {
    public int W;
    public int H;
    public int WordsPerRow;
    public ulong[] Words; // H * WordsPerRow

    public static PackedMask From(RasterMask m)
    {
      int wpr = (m.W + 63) / 64;
      var words = new ulong[m.H * wpr];
      for (int y = 0; y < m.H; y++)
      {
        int rowBase = y * m.W;
        int wordBase = y * wpr;
        for (int x = 0; x < m.W; x++)
        {
          if (m.Bits[rowBase + x])
          {
            words[wordBase + (x >> 6)] |= 1UL << (x & 63);
          }
        }
      }

      return new PackedMask { W = m.W, H = m.H, WordsPerRow = wpr, Words = words };
    }
  }

  internal struct JobPlacement
  {
    public int Source;
    public int Sheet;
    public int Xpx;
    public int Ypx;
    public int RotationDeg;
    public int PairGroup;   // >0: this part is half of a rigid pair module (compaction moves the pair as one body)
    public bool HasExact;   // common-line pair member: use the double position (internal seam at true contact)
    public double ExactXin; // exact part-bbox-min position, TRUE-sheet inches (the int px grid can't express it)
    public double ExactYin;
  }

  internal sealed class JobResult
  {
    public List<JobPlacement> Placements;
    public int Sheets;
    public int NotPlaced;
    public bool NoOverlap;
  }

  /// <summary>
  /// Generalized raster nester: multiple part geometries (each with a quantity and allowed rotations),
  /// packed bottom-left across as many sheets as needed, biggest-first, best rotation per part. The
  /// collision search is the same bitmap test proven on the GPU (GpuNestLab); here on CPU.
  /// </summary>
  internal static class RasterJobNester
  {
    public static JobResult Nest(List<PartType> types, int sheetW, int sheetH, double pxPerInch, int marginPx, int maxSheets = int.MaxValue, System.Threading.CancellationToken cancel = default)
    {
      var profSw = NestProfile.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;

      // Dilate each part by ITS OWN spacing halo (PartType.HaloPx) so two parts end up exactly
      // (haloA + haloB) apart — per-part spacing / common-line (halo 0 = copies pack touching).
      // The real part sits inset by (halo, halo) inside its dilated mask.
      foreach (var t in types)
      {
        var raws = t.RotationsDeg
          .Select(r => RasterUtil.Rasterize(r == 0 ? t.Poly : t.Poly.Rotate(r), pxPerInch))
          .ToArray();
        t.Masks = raws.Select(m => RasterUtil.Dilate(m, t.HaloPx)).ToArray();
        t.Packed = t.Masks.Select(PackedMask.From).ToArray();

        // Rotation-symmetric parts (circles, squares…) yield IDENTICAL masks for several rotations —
        // scanning the duplicates is pure waste (4× for a circle). Keep one rotation per distinct mask.
        var keep = new List<int>();
        for (int i = 0; i < t.Packed.Length; i++)
        {
          bool dup = false;
          for (int j = 0; j < keep.Count && !dup; j++)
          {
            dup = MasksEqual(t.Packed[i], t.Packed[keep[j]]);
          }

          if (!dup)
          {
            keep.Add(i);
          }
        }

        if (keep.Count < t.Packed.Length)
        {
          t.RotationsDeg = keep.Select(i => t.RotationsDeg[i]).ToArray();
          t.Masks = keep.Select(i => t.Masks[i]).ToArray();
          t.Packed = keep.Select(i => t.Packed[i]).ToArray();
          raws = keep.Select(i => raws[i]).ToArray();
        }

        ComputePairing(t, raws, pxPerInch);
      }

      long profMaskMs = profSw?.ElapsedMilliseconds ?? 0;

      // Pad the grid by the LARGEST halo on every side so any part's spacing-halo can extend PAST the
      // sheet edge into a virtual border (no neighbour to keep clear of out there). The real part then
      // reaches the true edge at margin 0. Per part: true-sheet corner = grid corner + halo − pad, and
      // the scan inset keeps the REAL part (not the halo) at the margin.
      int pad = types.Count == 0 ? 0 : types.Max(t => t.HaloPx);
      int gw = sheetW + (2 * pad);
      int gh = sheetH + (2 * pad);
      int marginInset = marginPx;

      // Grow the pack along the sheet's LONGER axis and fill across the shorter one, so the leftover
      // remnant is a clean full-width strip of the SHORT dimension (e.g. on a 120x60 sheet the offcut is
      // 60 x whatever-is-left, not a skinny 120-long sliver — what the shop actually wants to keep).
      bool growX = sheetW > sheetH;

      int wpr = (gw + 63) / 64; // occupancy words per grid row (64 columns per word)

      var instances = new List<int>();
      for (int ti = 0; ti < types.Count; ti++)
      {
        for (int q = 0; q < types[ti].Quantity; q++)
        {
          instances.Add(ti);
        }
      }

      // Higher-priority part types nest first (they get the best sheet positions and are never the ones
      // left unplaced); within a priority tier, biggest-first as before.
      instances.Sort((a, b) =>
      {
        int byPriority = types[b].Priority.CompareTo(types[a].Priority);
        return byPriority != 0 ? byPriority : types[b].Masks[0].SolidCount.CompareTo(types[a].Masks[0].SolidCount);
      });

      var sheets = new List<ulong[]>();
      var sheetFree = new List<int>();
      var closed = new List<bool[]>(); // closed[sheet][type] — this type already failed to fit here

      // rowHints[sheet][type][rotation] = lowest grid row where this mask could still fit. A sheet only
      // ever FILLS UP, so once a mask has no fit with its corner below row Y, it never will — the next
      // identical part resumes scanning at Y instead of re-scanning the whole sheet from row 0. Exact
      // same placements (rows below the hint are proven impossible), massively fewer scans: the dominant
      // cost was every part re-walking the full occupied region (minutes for real production parts).
      var rowHints = new List<int[][]>();
      var placements = new List<JobPlacement>();
      int notPlaced = 0;
      long solidPlaced = 0;

      var used = new bool[instances.Count];
      int pairGroupSeq = 0;
      for (int idx = 0; idx < instances.Count; idx++)
      {
        // Cheap volatile read per part: a Cancel press aborts within one placement (~ms), not at
        // the next PHASE boundary (measured 31 s on a 2000-part job before this check existed).
        cancel.ThrowIfCancellationRequested();
        if (used[idx])
        {
          continue; // consumed as a pair buddy
        }

        int ti = instances[idx];
        var t = types[ti];
        int partSolid = t.Masks[0].SolidCount;
        bool done = false;

        for (int si = 0; si <= sheets.Count && !done; si++)
        {
          ulong[] occ;
          if (si < sheets.Count)
          {
            // Once a part type fails on a sheet it never fits there again (the sheet only fills up),
            // so skip it without re-scanning. Same result, no wasted full scans (the big speedup).
            if (closed[si][ti] || sheetFree[si] < partSolid)
            {
              closed[si][ti] = true;
              continue;
            }

            occ = sheets[si];
          }
          else
          {
            occ = new ulong[wpr * gh];
          }

          // The scan inset keeps the REAL part at the margin: real corner = grid + halo − pad, so the
          // mask corner must stay ≥ margin + pad − halo from the grid edge (per type).
          int insetPx = marginInset + pad - t.HaloPx;

          // A single part can scan dozens of full sheets before placing; without this the per-part
          // check alone left ~11 s cancel latency on a 2000-part / 60-sheet job.
          cancel.ThrowIfCancellationRequested();

          if (si == sheets.Count && sheets.Count >= maxSheets)
          {
            break; // no more sheets available — this part stays unplaced
          }

          if (si == sheets.Count && rowHints.Count == si)
          {
            // Prospective new sheet: hints start at the margin row for every type/rotation. Kept even if
            // this attempt fails (a part that doesn't fit an EMPTY sheet never fits one).
            rowHints.Add(types.Select(tt => Enumerable.Repeat(marginInset + pad - tt.HaloPx, tt.Masks.Length).ToArray()).ToArray());
          }

          // A pair module may only be chosen when a SECOND unused instance of this type exists.
          int pairMate = -1;
          if (t.FirstPairEntry >= 0)
          {
            for (int k = idx + 1; k < instances.Count; k++)
            {
              if (!used[k] && instances[k] == ti)
              {
                pairMate = k;
                break;
              }
            }
          }

          if (TryPlaceBest(occ, wpr, gw, gh, insetPx, t, rowHints[si][ti], growX, pairMate >= 0, out int x, out int y, out int rotIdx))
          {
            if (si == sheets.Count)
            {
              sheets.Add(occ);
              sheetFree.Add(gw * gh);
              closed.Add(new bool[types.Count]);
              si = sheets.Count - 1;
            }

            var m = t.Masks[rotIdx];
            Stamp(occ, wpr, t.Packed[rotIdx], x, y);
            sheetFree[si] -= m.SolidCount;
            solidPlaced += m.SolidCount;
            done = true;

            if (t.FirstPairEntry >= 0 && rotIdx >= t.FirstPairEntry)
            {
              // PAIR MODULE placed (e.g. two triangles as a parallelogram): one stamped union mask,
              // TWO real placements, and the mate instance is consumed from the queue. Common-line
              // modules also carry the EXACT double offsets (internal seam at true contact — the int
              // grid can't express it) and a group id so compaction moves the module as one body.
              int pi = rotIdx - t.FirstPairEntry;
              bool exactPair = t.CommonLine && t.PairExactAx != null && !double.IsNaN(t.PairExactAx[pi]);
              int grp = 0;
              if (exactPair)
              {
                pairGroupSeq++;
                grp = pairGroupSeq;
              }

              double frameX = (x - pad) / pxPerInch;
              double frameY = (y - pad) / pxPerInch;
              placements.Add(new JobPlacement
              {
                Source = t.Source, Sheet = si, RotationDeg = t.RotationsDeg[t.PairSubA[pi]], PairGroup = grp,
                Xpx = x + t.PairAx[pi] + t.HaloPx - pad, Ypx = y + t.PairAy[pi] + t.HaloPx - pad,
                HasExact = exactPair, ExactXin = exactPair ? frameX + t.PairExactAx[pi] : 0, ExactYin = exactPair ? frameY + t.PairExactAy[pi] : 0,
              });
              placements.Add(new JobPlacement
              {
                Source = t.Source, Sheet = si, RotationDeg = t.RotationsDeg[t.PairSubB[pi]], PairGroup = grp,
                Xpx = x + t.PairBx[pi] + t.HaloPx - pad, Ypx = y + t.PairBy[pi] + t.HaloPx - pad,
                HasExact = exactPair, ExactXin = exactPair ? frameX + t.PairExactBx[pi] : 0, ExactYin = exactPair ? frameY + t.PairExactBy[pi] : 0,
              });
              used[pairMate] = true;
            }
            else
            {
              // Real part corner in TRUE-sheet coords = mask corner + halo − pad (the real part sits
              // +halo inside its dilated mask; the grid is padded by the largest halo).
              placements.Add(new JobPlacement { Source = t.Source, Sheet = si, Xpx = x + t.HaloPx - pad, Ypx = y + t.HaloPx - pad, RotationDeg = t.RotationsDeg[rotIdx] });
            }
          }
          else if (si < sheets.Count)
          {
            closed[si][ti] = true; // failed here — don't try this type on this sheet again
          }
        }

        if (!done)
        {
          notPlaced++;
        }
      }

      long occSum = 0;
      foreach (var s in sheets)
      {
        foreach (var w in s)
        {
          occSum += System.Numerics.BitOperations.PopCount(w);
        }
      }

      if (profSw != null)
      {
        long probes = System.Threading.Interlocked.Exchange(ref ProfFitsCalls, 0);
        NestProfile.Log($"RasterJob px={pxPerInch} types={types.Count} inst={instances.Count} rots={string.Join("/", types.Select(t => t.RotationsDeg.Length))} maskMs={profMaskMs} placeMs={profSw.ElapsedMilliseconds - profMaskMs} sheets={sheets.Count} fitsProbes={probes}");
      }

      return new JobResult
      {
        Placements = placements,
        Sheets = sheets.Count,
        NotPlaced = notPlaced,
        NoOverlap = occSum == solidPlaced,
      };
    }

    /// <summary>
    /// One-shot placement for the service's post-compaction REFILL: find the bottom-left position for
    /// one copy of <paramref name="t"/> (its Masks/Packed already built) on an existing occupancy grid.
    /// Exactly the nester's scan and collision test; row hints start at the inset because the occupancy
    /// was rebuilt fresh from exact positions.
    /// </summary>
    internal static bool TryPlaceOne(ulong[] occ, int wpr, int gw, int gh, int insetPx, PartType t, bool growX, out int x, out int y, out int rotIdx)
    {
      var hints = new int[t.Packed.Length];
      for (int i = 0; i < hints.Length; i++)
      {
        hints[i] = insetPx;
      }

      return TryPlaceBest(occ, wpr, gw, gh, insetPx, t, hints, growX, false, out x, out y, out rotIdx);
    }

    private static bool MasksEqual(PackedMask a, PackedMask b)
    {
      return a.W == b.W && a.H == b.H && System.MemoryExtensions.SequenceEqual<ulong>(a.Words, b.Words);
    }

    private static bool TryPlaceBest(ulong[] occ, int wpr, int sheetW, int sheetH, int insetPx, PartType t, int[] rowHints, bool growX, bool allowPair, out int bx, out int by, out int bRot)
    {
      // MODULE-FIRST (pairwise nesting): when a pair module fits it wins outright — a single always
      // fits at a position <= the module's (it is a subset), so scoring them together means the
      // module NEVER gets picked and the interlock is lost. Singles remain the fallback (end of
      // sheet, odd part out).
      if (allowPair && t.FirstPairEntry >= 0
          && TryPlaceRange(occ, wpr, sheetW, sheetH, insetPx, t, rowHints, growX, t.FirstPairEntry, t.Packed.Length, out bx, out by, out bRot))
      {
        return true;
      }

      int singleEnd = t.FirstPairEntry >= 0 ? t.FirstPairEntry : t.Packed.Length;
      return TryPlaceRange(occ, wpr, sheetW, sheetH, insetPx, t, rowHints, growX, 0, singleEnd, out bx, out by, out bRot);
    }

    private static bool TryPlaceRange(ulong[] occ, int wpr, int sheetW, int sheetH, int insetPx, PartType t, int[] rowHints, bool growX, int riFrom, int riTo, out int bx, out int by, out int bRot)
    {
      bx = -1;
      by = -1;
      bRot = -1;
      long bestIndex = long.MaxValue;

      for (int ri = riFrom; ri < riTo; ri++)
      {
        var m = t.Packed[ri];
        if (m.W + (2 * insetPx) > sheetW || m.H + (2 * insetPx) > sheetH)
        {
          continue;
        }

        // Resume where the previous identical mask left off — positions below the hint (along the
        // growth axis) are proven full.
        int start = rowHints[ri];
        if (start > (growX ? sheetW - m.W - insetPx : sheetH - m.H - insetPx))
        {
          continue; // proven: no fit anywhere on this sheet for this rotation, ever again
        }

        if (FindBottomLeft(occ, wpr, sheetW, sheetH, insetPx, m, start, growX, out int x, out int y))
        {
          rowHints[ri] = growX ? x : y; // the same row/column can still hold more copies
          long idx = growX ? ((long)x * sheetH) + y : ((long)y * sheetW) + x;

          // Tie on position: prefer the rotation that is NARROWER along the growth axis, so the part
          // tucks into the pack instead of sticking out sideways (e.g. a slim bracket placed on top of
          // a tall part goes vertical, keeping the used strip — and the remnant — at the pack's width).
          int growthDim = growX ? m.W : m.H;
          int bestGrowthDim = bRot >= 0 ? (growX ? t.Packed[bRot].W : t.Packed[bRot].H) : int.MaxValue;
          if (idx < bestIndex || (idx == bestIndex && growthDim < bestGrowthDim))
          {
            bestIndex = idx;
            bx = x;
            by = y;
            bRot = ri;
          }
        }
        else
        {
          rowHints[ri] = int.MaxValue; // the sheet only fills up — this rotation is done here
        }
      }

      return bestIndex != long.MaxValue;
    }

    /// <summary>
    /// PAIR CLUSTERING setup: for every rotation that has its 180° complement available, find the
    /// buddy offset that packs part + flipped copy into the smallest union bounding box (a triangle
    /// pair = a parallelogram). Column min/max profiles make the search O(W²) — exact for convex
    /// parts, conservative (never overlapping) for concave ones. When the pair is >= 15% denser
    /// than the single part, a PAIR MODULE mask (the union) is appended as an extra scan entry —
    /// rectangles and other non-interlocking shapes are untouched. Masks are the DILATED ones, so
    /// the spacing/halo clearance is built in.
    /// </summary>
    internal static void ComputePairing(PartType t, RasterMask[] raws = null, double pxPerInch = 0)
    {
      int n = t.RotationsDeg.Length;
      if (t.Quantity < 2)
      {
        return;
      }

      // Common-line pairs are matched on the UNDILATED masks so the module is born with its internal
      // seam at EXACT contact — that seam IS the shared cut, and nothing downstream may move it
      // (the module rides rigidly through compaction). Spaced parts keep the dilated matching: their
      // internal seam is born at exactly the requested clearance instead.
      bool exact = t.CommonLine && raws != null;

      var pairMasks = new List<RasterMask>();
      var pairRotLabel = new List<int>();
      var subA = new List<int>();
      var subB = new List<int>();
      var aOffX = new List<int>();
      var aOffY = new List<int>();
      var bOffX = new List<int>();
      var bOffY = new List<int>();
      var exactAx = new List<double>();
      var exactAy = new List<double>();
      var exactBx = new List<double>();
      var exactBy = new List<double>();

      (int[] Top, int[] Bot) Profiles(RasterMask m)
      {
        var top = new int[m.W];
        var bot = new int[m.W];
        for (int x = 0; x < m.W; x++)
        {
          top[x] = -1;
          bot[x] = int.MaxValue;
          for (int y = 0; y < m.H; y++)
          {
            if (m.Bits[(y * m.W) + x])
            {
              if (y < bot[x])
              {
                bot[x] = y;
              }

              if (y > top[x])
              {
                top[x] = y;
              }
            }
          }
        }

        return (top, bot);
      }

      for (int ri = 0; ri < n; ri++)
      {
        int want = (t.RotationsDeg[ri] + 180) % 360;
        int bi = System.Array.IndexOf(t.RotationsDeg, want);
        if (bi < 0)
        {
          continue; // complement not allowed (or deduped away for a symmetric part — pairing is pointless there)
        }

        var a = exact ? raws[ri] : t.Masks[ri];
        var b = exact ? raws[bi] : t.Masks[bi];
        var (topA, botA) = Profiles(a);
        var (topB, botB) = Profiles(b);

        long bestArea = long.MaxValue;
        int bestDx = 0, bestDy = 0;
        for (int dx = -b.W + 1; dx < a.W; dx++)
        {
          // Contact positions: B dropped from above until its columns rest on A, and pushed up from
          // below until its columns press under A (interval profiles => guaranteed no overlap).
          int dyAbove = int.MinValue;
          int dyBelow = int.MaxValue;
          bool anyColumn = false;
          int lo = System.Math.Max(0, dx);
          int hi = System.Math.Min(a.W, dx + b.W);
          for (int x = lo; x < hi; x++)
          {
            int xb = x - dx;
            if (topA[x] < 0 || topB[xb] < 0)
            {
              continue; // an empty column constrains nothing
            }

            anyColumn = true;
            dyAbove = System.Math.Max(dyAbove, topA[x] - botB[xb] + 1);
            dyBelow = System.Math.Min(dyBelow, botA[x] - topB[xb] - 1);
          }

          foreach (int dy in anyColumn ? new[] { dyAbove, dyBelow } : new[] { 0 })
          {
            long w = System.Math.Max(a.W, dx + b.W) - System.Math.Min(0, dx);
            long h = System.Math.Max(a.H, dy + b.H) - System.Math.Min(0, dy);
            long area = w * h;
            if (area < bestArea)
            {
              bestArea = area;
              bestDx = dx;
              bestDy = dy;
            }
          }
        }

        if (bestArea == long.MaxValue)
        {
          continue;
        }

        double singleDensity = (double)a.SolidCount / ((long)a.W * a.H);
        double pairDensity = (double)(a.SolidCount + b.SolidCount) / bestArea;
        NestProfile.Log($"pairing src={t.Source} rot={t.RotationsDeg[ri]} best=({bestDx},{bestDy}) single={singleDensity:0.000} pair={pairDensity:0.000} gate={(pairDensity >= singleDensity * 1.15 ? "ON" : "off")}");
        if (pairDensity < singleDensity * 1.15)
        {
          continue;
        }

        // Module mask = the FILLED bounding box, not the union outline. The pair already fills
        // ~all of its bbox (the >=1.15 density gate saw to that), and a solid rectangle gives the
        // module FLAT faces: with the true union, the bottom-left scan tucked a module's corner
        // 1-2px into the previous column's diagonal pocket — winning 0.01" of X at the cost of a
        // 0.32" stacking gap per module (measured), which is exactly the stair-stepped, uneven
        // columns the user reported. Flat faces make columns stack flush by construction.
        // Exact (common-line) modules add the halo back as a border ring, so the module keeps the
        // usual anti-jam clearance to OTHERS while its inside stays at true contact.
        int border = exact ? t.HaloPx : 0;
        int ax = System.Math.Max(0, -bestDx);
        int ay = System.Math.Max(0, -bestDy);
        int bx = System.Math.Max(0, bestDx);
        int by = System.Math.Max(0, bestDy);
        int pw = System.Math.Max(ax + a.W, bx + b.W) + (2 * border);
        int ph = System.Math.Max(ay + a.H, by + b.H) + (2 * border);
        var bits = new bool[pw * ph];
        for (int k = 0; k < bits.Length; k++)
        {
          bits[k] = true;
        }

        pairMasks.Add(new RasterMask { Bits = bits, W = pw, H = ph, SolidCount = pw * ph });
        pairRotLabel.Add(t.RotationsDeg[ri]);
        subA.Add(ri);
        subB.Add(bi);
        aOffX.Add(ax);
        aOffY.Add(ay);
        bOffX.Add(bx);
        bOffY.Add(by);

        // EXACT internal seam for common-line pairs: the pixel offset leaves the mates ~1px apart
        // (a 0.04" double cut on the shared edge). Candidate exact configurations by double ray-cast,
        // keeping the one with the SMALLEST union bbox: closing only the nearest axis reached contact
        // but left the mate slid ~1px ALONG the seam — the pair's corners poked out 0.04" and module
        // junctions looked shifted at high zoom. The bbox-aligned candidates seat the mate flush
        // (a right-triangle pair becomes a true rectangle).
        if (exact && pxPerInch > 0)
        {
          var polyA = t.RotationsDeg[ri] == 0 ? t.Poly : t.Poly.Rotate(t.RotationsDeg[ri]);
          var polyB = t.RotationsDeg[bi] == 0 ? t.Poly : t.Poly.Rotate(t.RotationsDeg[bi]);
          double offXin = bestDx / pxPerInch;
          double offYin = bestDy / pxPerInch;
          double tax = -polyA.Points.Min(p => p.X);
          double tay = -polyA.Points.Min(p => p.Y);
          double wA = polyA.Points.Max(p => p.X) - polyA.Points.Min(p => p.X);
          double hA = polyA.Points.Max(p => p.Y) - polyA.Points.Min(p => p.Y);
          double wB = polyB.Points.Max(p => p.X) - polyB.Points.Min(p => p.X);
          double hB = polyB.Points.Max(p => p.Y) - polyB.Points.Min(p => p.Y);
          double bMinX = polyB.Points.Min(p => p.X);
          double bMinY = polyB.Points.Min(p => p.Y);

          double closable = 3.0 / pxPerInch; // birth residue is ~1px; anything larger means no real seam on that axis
          double far = wA + wB + hA + hB + 1.0;
          double bestSnapX = offXin, bestSnapY = offYin, bestSnapArea = double.MaxValue;

          // Containment: the module's stamped mask frame was sized for the pixel offset — a candidate
          // whose exact union outgrows it would poke past the reserved clearance ring.
          double frameWin = (pw - (2 * border)) / pxPerInch;
          double frameHin = (ph - (2 * border)) / pxPerInch;

          void Consider(double startBx, double startBy, bool alongXAxis, double maxTravel)
          {
            double g = AxisGapPoly(polyA, tax, tay, polyB, startBx - bMinX, startBy - bMinY, alongXAxis);
            if (g <= 0 || g > maxTravel)
            {
              return;
            }

            double px2 = alongXAxis ? startBx - g : startBx;
            double py2 = alongXAxis ? startBy : startBy - g;
            double uw = System.Math.Max(wA, px2 + wB) - System.Math.Min(0, px2);
            double uh = System.Math.Max(hA, py2 + hB) - System.Math.Min(0, py2);
            if (uw > frameWin + 1e-9 || uh > frameHin + 1e-9)
            {
              return;
            }

            if (uw * uh < bestSnapArea)
            {
              bestSnapArea = uw * uh;
              bestSnapX = px2;
              bestSnapY = py2;
            }
          }

          Consider(offXin, offYin, true, closable);   // close X from the pixel birth position
          Consider(offXin, offYin, false, closable);  // close Y from the pixel birth position
          Consider(far, 0, true, double.MaxValue);    // bbox-aligned in Y, slide B in from the right
          Consider(0, far, false, double.MaxValue);   // bbox-aligned in X, slide B in from the top

          // Re-anchor so the raw union starts at the frame origin; the border ring absorbs the shift.
          double unionMinX = System.Math.Min(0, bestSnapX);
          double unionMinY = System.Math.Min(0, bestSnapY);
          double borderIn = border / pxPerInch;
          exactAx.Add(borderIn - unionMinX);
          exactAy.Add(borderIn - unionMinY);
          exactBx.Add(borderIn + bestSnapX - unionMinX);
          exactBy.Add(borderIn + bestSnapY - unionMinY);
        }
        else
        {
          // Not exact-capable: mirror the integer offsets so the arrays stay aligned (unused).
          exactAx.Add(double.NaN);
          exactAy.Add(double.NaN);
          exactBx.Add(double.NaN);
          exactBy.Add(double.NaN);
        }
      }

      if (pairMasks.Count == 0)
      {
        return;
      }

      t.FirstPairEntry = n;
      t.RotationsDeg = t.RotationsDeg.Concat(pairRotLabel).ToArray();
      t.Masks = t.Masks.Concat(pairMasks).ToArray();
      t.Packed = t.Packed.Concat(pairMasks.Select(PackedMask.From)).ToArray();
      t.PairSubA = subA.ToArray();
      t.PairSubB = subB.ToArray();
      t.PairAx = aOffX.ToArray();
      t.PairAy = aOffY.ToArray();
      t.PairBx = bOffX.ToArray();
      t.PairBy = bOffY.ToArray();
      t.PairExactAx = exactAx.ToArray();
      t.PairExactAy = exactAy.ToArray();
      t.PairExactBx = exactBx.ToArray();
      t.PairExactBy = exactBy.ToArray();
    }

    /// <summary>
    /// Exact directional clearance (inches) between two placed polygons: how far B can slide toward
    /// -X (or -Y) before its raw outline meets A's. Double-precision twin of the weld's integer
    /// ray-cast — vertex-vs-edge both ways. Returns MaxValue when the slide never meets A.
    /// </summary>
    internal static double AxisGapPoly(INfp a, double tax, double tay, INfp b, double tbx, double tby, bool alongX)
    {
      double U(double x, double y) => alongX ? x : y;
      double V(double x, double y) => alongX ? y : x;
      double best = double.MaxValue;

      // Ray from vertex v toward -U against edge (p,q): hits where the edge spans v's V ordinate.
      double RayGap(double vu, double vv, double pu, double pv, double qu, double qv)
      {
        if (pv == qv)
        {
          return double.MaxValue; // parallel to the ray — the edge's endpoints constrain instead
        }

        if (vv < System.Math.Min(pv, qv) || vv > System.Math.Max(pv, qv))
        {
          return double.MaxValue;
        }

        double tt = (vv - pv) / (qv - pv);
        double uEdge = pu + (tt * (qu - pu));
        return uEdge <= vu ? vu - uEdge : double.MaxValue;
      }

      var pa = a.Points;
      var pb = b.Points;

      // B's vertices raying -U onto A's edges…
      foreach (var v in pb)
      {
        double vu = U(v.X + tbx, v.Y + tby);
        double vv = V(v.X + tbx, v.Y + tby);
        for (int e = 0; e < pa.Length; e++)
        {
          var p = pa[e];
          var q = pa[(e + 1) % pa.Length];
          double g = RayGap(vu, vv, U(p.X + tax, p.Y + tay), V(p.X + tax, p.Y + tay), U(q.X + tax, q.Y + tay), V(q.X + tax, q.Y + tay));
          if (g < best)
          {
            best = g;
          }
        }
      }

      // …and A's vertices raying +U onto B's edges (equivalent to B sliding -U onto them).
      foreach (var v in pa)
      {
        double vu = U(v.X + tax, v.Y + tay);
        double vv = V(v.X + tax, v.Y + tay);
        for (int e = 0; e < pb.Length; e++)
        {
          var p = pb[e];
          var q = pb[(e + 1) % pb.Length];
          double pu = U(p.X + tbx, p.Y + tby);
          double pv = V(p.X + tbx, p.Y + tby);
          double qu = U(q.X + tbx, q.Y + tby);
          double qv = V(q.X + tbx, q.Y + tby);
          if (pv == qv || vv < System.Math.Min(pv, qv) || vv > System.Math.Max(pv, qv))
          {
            continue;
          }

          double tt = (vv - pv) / (qv - pv);
          double uEdge = pu + (tt * (qu - pu));
          double g = uEdge >= vu ? uEdge - vu : double.MaxValue;
          if (g < best)
          {
            best = g;
          }
        }
      }

      return best;
    }

    /// <summary>Profiling only (SHEETNEST_NEST_PROFILE=1): total Fits probes across a job.</summary>
    internal static long ProfFitsCalls;

    private static bool FindBottomLeft(ulong[] occ, int wpr, int sheetW, int sheetH, int insetPx, PackedMask m, int start, bool growX, out int ox, out int oy)
    {
      int hiY = sheetH - m.H - insetPx;
      int hiX = sheetW - m.W - insetPx;
      long probes = 0;

      try
      {
      if (growX)
      {
        // Fill full columns across the short (Y) dimension, growing along X — the remnant stays a
        // full-height strip at the right end of the sheet.
        for (int x = System.Math.Max(insetPx, start); x <= hiX; x++)
        {
          for (int y = insetPx; y <= hiY; y++)
          {
            probes++;
            if (Fits(occ, wpr, m, x, y))
            {
              ox = x;
              oy = y;
              return true;
            }
          }
        }
      }
      else
      {
        for (int y = System.Math.Max(insetPx, start); y <= hiY; y++)
        {
          for (int x = insetPx; x <= hiX; x++)
          {
            probes++;
            if (Fits(occ, wpr, m, x, y))
            {
              ox = x;
              oy = y;
              return true;
            }
          }
        }
      }

      ox = -1;
      oy = -1;
      return false;
      }
      finally
      {
        if (NestProfile.Enabled)
        {
          System.Threading.Interlocked.Add(ref ProfFitsCalls, probes);
        }
      }
    }

    // Word-packed collision test: AND 64 columns at a time (the mask row is shifted to the candidate
    // x on the fly, carrying bits across word boundaries). Same answer as the cell-by-cell scan.
    private static bool Fits(ulong[] occ, int wpr, PackedMask m, int ox, int oy)
    {
      int wordX = ox >> 6;
      int s = ox & 63;

      // DEEP-FAILURE GUARD: probes over mostly-empty space that collide only hundreds of rows up
      // used to walk every row below the obstruction first — for large (3D-unfolded) parts that made
      // one 4-rotation candidate take ~18 s while the 2-rotation ones took ~0.2 s (measured: same
      // probe count, ~100x the cost per probe). Sample a few spread rows first: any colliding sample
      // is an EXACT reject; obstructions are whole parts (hundreds of px tall), so a band taller
      // than H/4 can never hide between the samples. All samples clean -> full definitive scan.
      if (m.H >= 32)
      {
        if (RowCollides(occ, wpr, m, wordX, s, oy, 0)
            || RowCollides(occ, wpr, m, wordX, s, oy, m.H - 1)
            || RowCollides(occ, wpr, m, wordX, s, oy, m.H >> 1)
            || RowCollides(occ, wpr, m, wordX, s, oy, m.H >> 2)
            || RowCollides(occ, wpr, m, wordX, s, oy, (3 * m.H) >> 2))
        {
          return false;
        }
      }

      for (int py = 0; py < m.H; py++)
      {
        int mBase = py * m.WordsPerRow;
        int oBase = ((oy + py) * wpr) + wordX;
        ulong carry = 0;
        for (int wi = 0; wi < m.WordsPerRow; wi++)
        {
          ulong mw = m.Words[mBase + wi];
          ulong shifted = (mw << s) | carry;
          carry = s == 0 ? 0UL : mw >> (64 - s);
          if ((shifted & occ[oBase + wi]) != 0)
          {
            return false;
          }
        }

        if (carry != 0 && (carry & occ[oBase + m.WordsPerRow]) != 0)
        {
          return false;
        }
      }

      return true;
    }

    /// <summary>Collision test for a single mask row (same shifting as the full scan).</summary>
    private static bool RowCollides(ulong[] occ, int wpr, PackedMask m, int wordX, int s, int oy, int py)
    {
      int mBase = py * m.WordsPerRow;
      int oBase = ((oy + py) * wpr) + wordX;
      ulong carry = 0;
      for (int wi = 0; wi < m.WordsPerRow; wi++)
      {
        ulong mw = m.Words[mBase + wi];
        ulong shifted = (mw << s) | carry;
        carry = s == 0 ? 0UL : mw >> (64 - s);
        if ((shifted & occ[oBase + wi]) != 0)
        {
          return true;
        }
      }

      return carry != 0 && (carry & occ[oBase + m.WordsPerRow]) != 0;
    }

    internal static void Stamp(ulong[] occ, int wpr, PackedMask m, int ox, int oy)
    {
      int wordX = ox >> 6;
      int s = ox & 63;

      for (int py = 0; py < m.H; py++)
      {
        int mBase = py * m.WordsPerRow;
        int oBase = ((oy + py) * wpr) + wordX;
        ulong carry = 0;
        for (int wi = 0; wi < m.WordsPerRow; wi++)
        {
          ulong mw = m.Words[mBase + wi];
          occ[oBase + wi] |= (mw << s) | carry;
          carry = s == 0 ? 0UL : mw >> (64 - s);
        }

        if (carry != 0)
        {
          occ[oBase + m.WordsPerRow] |= carry;
        }
      }
    }
  }
}
