namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using ClipperLib;
  using DeepNestLib;

  /// <summary>A placed part being compacted: its rotated polygon and the placement offset (inches).</summary>
  internal sealed class CompactItem
  {
    public INfp Poly;       // rotated part geometry (absolute points = Poly points + (X, Y))
    public double X;
    public double Y;
    public double Spacing;  // this part's spacing (in); 0 = common-line part (slides to true contact)
  }

  /// <summary>
  /// EXACT-geometry compaction pass for spacing-0 nests. The raster engine can only place parts on its
  /// pixel grid, and its conservative masks keep two parts that share an edge (e.g. interlocking
  /// triangles) 1-2 pixels apart â€” a visible ~1-2 mm gap that no affordable resolution removes. This pass
  /// slides each placed part down then left in EXACT polygon space (Clipper, the same library the NFP
  /// engine uses) until it touches its neighbours or the sheet margin, eliminating the raster
  /// quantization entirely. A small safety backoff (0.001") is kept so numeric noise can never produce
  /// real material interference. Translation only â€” rotations are never changed.
  /// </summary>
  internal static class RasterCompact
  {
    private const double Scale = 1e6;              // Clipper integer units per inch (120" sheet â‰ˆ 1.2e8 Â« long range)
    private const double EpsArea = 1e-6 * Scale * Scale; // ignore sub-1e-6 inÂ² contact slivers as "touching"
    private const double Backoff = 0.001;          // extra slack left after a slide (0.025 mm)

    /// <summary>
    /// Minimum gap between common-line parts: coincident (0-gap) edges get MERGED/DELETED by the CAM,
    /// so CC parts keep this tiny separation instead â€” â‰ˆ0.076 mm, far below the laser kerf
    /// (~0.15-0.2 mm) yet clearly above any CAM coincidence tolerance.
    /// </summary>
    internal const double CommonLineGap = 0.003;

    /// <summary>
    /// Compact one sheet's placements in place (mutates item X/Y). Only spacing-0 (common-line) parts
    /// slide; parts with a spacing keep their position AND their clearance â€” the sliding parts test
    /// against their neighbours' outlines inflated by the neighbour's half-spacing.
    /// </summary>
    public static void Compact(IList<CompactItem> items, double sheetW, double sheetH, double margin)
    {
      if (items == null || items.Count < 1 || !items.Any(it => it.Spacing <= 0))
      {
        return;
      }

      // Clearance is PER PAIR: spacing pairs need (sA+sB)/2 â€” expressed by half-inflated shells â€” and
      // CC-CC pairs need the CAM-safe mini-gap (coincident edges get merged/deleted by the CAM). The
      // mini-gap must NOT leak into CC-vs-spaced pairs (the raster already placed those at exactly
      // s/2, and inflating would make every one look violated), hence TWO shells per item.
      var pathsRaw = items.Select(ToPaths).ToArray();
      var pathsHalf = items.Select(it => it.Spacing > 0 ? Inflate(ToPaths(it), it.Spacing / 2.0) : ToPaths(it)).ToArray();
      var pathsCC = items.Select(it => it.Spacing <= 0 ? Inflate(ToPaths(it), CommonLineGap / 2.0) : null).ToArray();
      var bounds = items.Select((it, i) => BoundsOf(pathsCC[i] ?? pathsHalf[i])).ToArray();

      // HARD INVARIANT: no move may ever create a REAL overlap. The engine hands us an overlap-free
      // layout; every push/slide below is additionally checked against the raw outlines of ALL parts.
      bool RawClearAll(int m, double ox, double oy)
      {
        long tx = (long)Math.Round(ox * Scale);
        long ty = (long)Math.Round(oy * Scale);
        var moved = ox == 0 && oy == 0 ? pathsRaw[m] : Translate(pathsRaw[m], tx, ty);
        var mb = BoundsOf(moved);
        for (int k = 0; k < items.Count; k++)
        {
          if (k == m || !BoxesTouch(mb, bounds[k]))
          {
            continue;
          }

          if (Intersects(moved, pathsRaw[k]))
          {
            return false;
          }
        }

        return true;
      }

      bool BothCC(int i, int j) => items[i].Spacing <= 0 && items[j].Spacing <= 0;

      bool ValidOffset(int i, double ox, double oy)
      {
        long tx = (long)Math.Round(ox * Scale);
        long ty = (long)Math.Round(oy * Scale);
        var movedHalf = ox == 0 && oy == 0 ? pathsHalf[i] : Translate(pathsHalf[i], tx, ty);
        var movedCC = pathsCC[i] == null ? null : ox == 0 && oy == 0 ? pathsCC[i] : Translate(pathsCC[i], tx, ty);
        var mb = BoundsOf(movedCC ?? movedHalf);
        for (int j = 0; j < items.Count; j++)
        {
          if (j == i || !BoxesTouch(mb, bounds[j]))
          {
            continue;
          }

          bool hit = BothCC(i, j)
            ? Intersects(movedCC, pathsCC[j])
            : Intersects(movedHalf, pathsHalf[j]);
          if (hit)
          {
            return false;
          }
        }

        return true;
      }

      void ApplyMove(int i, double ox, double oy)
      {
        long tx = (long)Math.Round(ox * Scale);
        long ty = (long)Math.Round(oy * Scale);
        items[i].X += ox;
        items[i].Y += oy;
        pathsRaw[i] = Translate(pathsRaw[i], tx, ty);
        pathsHalf[i] = Translate(pathsHalf[i], tx, ty);
        if (pathsCC[i] != null)
        {
          pathsCC[i] = Translate(pathsCC[i], tx, ty);
        }

        bounds[i] = BoundsOf(pathsCC[i] ?? pathsHalf[i]);
      }

      // Compact toward the same corner the nester packs to: the pack grows along the sheet's LONGER
      // axis, so push primarily along that axis (keeps the remnant a clean short-dimension strip).
      bool growX = sheetW > sheetH;

      // Only common-line (spacing-0) parts move. Parts nearest the target corner settle first so the
      // others can lean on their final positions; two rounds lets a part freed by a neighbour's move
      // take the extra slack.
      var movable = Enumerable.Range(0, items.Count).Where(i => items[i].Spacing <= 0);
      var order = (growX
          ? movable.OrderBy(i => items[i].X + items[i].Poly.MinX).ThenBy(i => items[i].Y + items[i].Poly.MinY)
          : movable.OrderBy(i => items[i].Y + items[i].Poly.MinY).ThenBy(i => items[i].X + items[i].Poly.MinX))
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
        bool anyViolation = false;
        bool movedAny = false;
        for (int a = 0; a < items.Count; a++)
        {
          for (int b = a + 1; b < items.Count; b++)
          {
            if (!BoxesTouch(bounds[a], bounds[b]) || PairClear(a, b, 0, 0))
            {
              continue;
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
        var it = items[i];

        // Furthest the part could go: down to the sheet margin (sliding only ever shrinks the extent,
        // so the far edges can't be violated).
        double max = dirX != 0 ? (it.X + it.Poly.MinX) - margin : (it.Y + it.Poly.MinY) - margin;
        if (max <= Backoff)
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
          // Largest collision-free slide: binary search (30 iterations â‰ˆ 1e-7" over a 120" sheet).
          double lo = 0, hi = max;
          for (int k = 0; k < 30; k++)
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

        slide -= Backoff;
        if (slide <= 1e-9)
        {
          return false;
        }

        ApplyMove(i, dirX * slide, dirY * slide);
        return true;
      }

      for (int round = 0; round < 2; round++)
      {
        bool moved = false;
        foreach (int i in order)
        {
          if (growX)
          {
            moved |= Slide(i, -1, 0); // left (growth axis)
            moved |= Slide(i, 0, -1); // down
          }
          else
          {
            moved |= Slide(i, 0, -1); // down (growth axis)
            moved |= Slide(i, -1, 0); // left
          }
        }

        if (!moved)
        {
          break;
        }
      }
    }

    /// <summary>
    /// True when EVERY pair of parts keeps at least <paramref name="floor"/> inches of clearance.
    /// Used to vet tight-packed (halo-0) layouts after compaction: a chain born touching can jam
    /// mid-separation, and the separation pre-pass caps pushes at RAW contact — so a common-line
    /// part can end up ~0.0005" from a SPACED neighbour, not just from another CC part. Any pair
    /// under the floor is the coincident-line CAM merge/delete hazard, regardless of what spacing
    /// the parts asked for, so all pairs are vetted.
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

      var offset = new ClipperOffset();
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
