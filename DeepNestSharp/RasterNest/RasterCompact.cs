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
    public INfp Poly;   // rotated part geometry (absolute points = Poly points + (X, Y))
    public double X;
    public double Y;
  }

  /// <summary>
  /// EXACT-geometry compaction pass for spacing-0 nests. The raster engine can only place parts on its
  /// pixel grid, and its conservative masks keep two parts that share an edge (e.g. interlocking
  /// triangles) 1-2 pixels apart — a visible ~1-2 mm gap that no affordable resolution removes. This pass
  /// slides each placed part down then left in EXACT polygon space (Clipper, the same library the NFP
  /// engine uses) until it touches its neighbours or the sheet margin, eliminating the raster
  /// quantization entirely. A small safety backoff (0.001") is kept so numeric noise can never produce
  /// real material interference. Translation only — rotations are never changed.
  /// </summary>
  internal static class RasterCompact
  {
    private const double Scale = 1e6;              // Clipper integer units per inch (120" sheet ≈ 1.2e8 « long range)
    private const double EpsArea = 1e-6 * Scale * Scale; // ignore sub-1e-6 in² contact slivers as "touching"
    private const double Backoff = 0.001;          // safety gap left after contact (0.025 mm — far below kerf)

    /// <summary>Compact one sheet's placements in place (mutates item X/Y).</summary>
    public static void Compact(IList<CompactItem> items, double sheetW, double sheetH, double margin)
    {
      if (items == null || items.Count < 1)
      {
        return;
      }

      var paths = items.Select(ToPaths).ToArray();
      var bounds = paths.Select(BoundsOf).ToArray();

      // Bottom rows settle first so upper parts can lean on their final positions; two rounds lets a part
      // freed by a neighbour's move take the extra slack.
      var order = Enumerable.Range(0, items.Count)
        .OrderBy(i => items[i].Y + items[i].Poly.MinY)
        .ThenBy(i => items[i].X + items[i].Poly.MinX)
        .ToArray();

      for (int round = 0; round < 2; round++)
      {
        bool moved = false;
        foreach (int i in order)
        {
          moved |= Slide(items, paths, bounds, i, 0, -1, margin); // down
          moved |= Slide(items, paths, bounds, i, -1, 0, margin); // left
        }

        if (!moved)
        {
          break;
        }
      }
    }

    /// <summary>Slide item i as far as possible along (dirX, dirY); returns true if it moved.</summary>
    private static bool Slide(IList<CompactItem> items, List<List<IntPoint>>[] paths, IntRect[] bounds, int i, int dirX, int dirY, double margin)
    {
      var it = items[i];

      // Furthest the part could go: down to the sheet margin (sliding only ever shrinks the extent, so
      // the far edges can't be violated).
      double max = dirX != 0 ? (it.X + it.Poly.MinX) - margin : (it.Y + it.Poly.MinY) - margin;
      if (max <= Backoff)
      {
        return false;
      }

      bool Valid(double s)
      {
        var moved = Translate(paths[i], (long)Math.Round(dirX * s * Scale), (long)Math.Round(dirY * s * Scale));
        var mb = BoundsOf(moved);
        for (int j = 0; j < paths.Length; j++)
        {
          if (j == i || !BoxesTouch(mb, bounds[j]))
          {
            continue;
          }

          if (Intersects(moved, paths[j]))
          {
            return false;
          }
        }

        return true;
      }

      double slide;
      if (Valid(max))
      {
        slide = max;
      }
      else
      {
        // Largest collision-free slide: binary search (30 iterations ≈ 1e-7" over a 120" sheet).
        double lo = 0, hi = max;
        for (int k = 0; k < 30; k++)
        {
          double mid = (lo + hi) / 2;
          if (Valid(mid))
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

      it.X += dirX * slide;
      it.Y += dirY * slide;
      paths[i] = Translate(paths[i], (long)Math.Round(dirX * slide * Scale), (long)Math.Round(dirY * slide * Scale));
      bounds[i] = BoundsOf(paths[i]);
      return true;
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
