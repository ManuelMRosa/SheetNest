namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using ClipperLib;
  using DeepNestLib;

  /// <summary>
  /// The clearance test between two placed parts: "does A, grown by the pair's clearance, overlap B?" —
  /// the same rule the nester enforces, used by manual edit mode to accept or refuse a position.
  /// It lives here rather than inside the viewer so it can be unit-tested, and so
  /// <see cref="DragCollisionCache"/> can share the geometry across the hundreds of queries one drag makes.
  /// </summary>
  internal static class PlacementCollision
  {
    internal const double Scale = 1e6;

    private const double EpsArea = 1e-6 * Scale * Scale; // ignore sub-1e-6 contact slivers as "touching"

    /// <summary>True when the two placed outlines are closer than <paramref name="clearance"/>.</summary>
    internal static bool TooClose(INfp placedA, INfp placedB, double clearance)
    {
      if (BoxesApart(placedA, placedB, clearance))
      {
        return false;
      }

      var pathsA = ToPaths(placedA);
      if (clearance > 0)
      {
        pathsA = InflateOuter(pathsA, clearance);
      }

      return Overlaps(pathsA, ToPaths(placedB));
    }

    /// <summary>Cheap reject: the two bounding boxes are already further apart than the clearance.</summary>
    internal static bool BoxesApart(INfp a, INfp b, double clearance)
    {
      return BoxesApart(a.MinX, a.MinY, a.MaxX, a.MaxY, b.MinX, b.MinY, b.MaxX, b.MaxY, clearance);
    }

    internal static bool BoxesApart(
      double aMinX, double aMinY, double aMaxX, double aMaxY,
      double bMinX, double bMinY, double bMaxX, double bMaxY,
      double clearance)
    {
      return aMaxX + clearance <= bMinX || bMaxX <= aMinX - clearance
        || aMaxY + clearance <= bMinY || bMaxY <= aMinY - clearance;
    }

    /// <summary>Outline + holes of one contour set, in Clipper integer coordinates.</summary>
    internal static List<List<IntPoint>> ToPaths(INfp nfp)
    {
      var paths = new List<List<IntPoint>>();
      void Add(INfp contour)
      {
        if (contour?.Points == null || contour.Points.Length < 3)
        {
          return;
        }

        var path = new List<IntPoint>(contour.Points.Length);
        foreach (var p in contour.Points)
        {
          path.Add(new IntPoint((long)Math.Round(p.X * Scale), (long)Math.Round(p.Y * Scale)));
        }

        paths.Add(path);
      }

      Add(nfp);
      if (nfp?.Children != null)
      {
        foreach (var child in nfp.Children)
        {
          Add(child);
        }
      }

      return paths;
    }

    /// <summary>Grow the OUTER contour by <paramref name="amount"/> drawing units (round join = uniform
    /// Euclidean clearance, matching the nesting engine's halo). Holes are kept as-is.</summary>
    internal static List<List<IntPoint>> InflateOuter(List<List<IntPoint>> paths, double amount)
    {
      if (paths.Count == 0 || amount <= 0)
      {
        return paths;
      }

      var outer = new List<IntPoint>(paths[0]);
      if (!Clipper.Orientation(outer))
      {
        outer.Reverse(); // a positive offset only expands positively-oriented rings
      }

      // The arc tolerance MUST be tied to the offset: Clipper's default is an ABSOLUTE 0.25 integer
      // units, so a round join's point count grows with the offset measured in integer units — on a
      // metric drawing that resampled this shell into ~10k vertices (see RasterCompact.Inflate).
      var offset = new ClipperOffset(2.0, Math.Abs(amount * Scale) * 0.0005);
      offset.AddPath(outer, JoinType.jtRound, EndType.etClosedPolygon);
      var grown = new List<List<IntPoint>>();
      offset.Execute(ref grown, amount * Scale);

      var result = new List<List<IntPoint>>(grown);
      for (int i = 1; i < paths.Count; i++)
      {
        result.Add(paths[i]);
      }

      return result.Count > 0 ? result : paths;
    }

    /// <summary>True when the two path sets share real area (holes respected via even-odd).</summary>
    internal static bool Overlaps(List<List<IntPoint>> a, List<List<IntPoint>> b)
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

    internal static List<List<IntPoint>> Translate(List<List<IntPoint>> paths, long dx, long dy)
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
  }

  /// <summary>
  /// Everything that does NOT change while one part is dragged, precomputed once: the neighbours' Clipper
  /// paths and bounds, and the dragged part's clearance shells (built at the part's own origin, then
  /// translated per query). Without it every mouse move rebuilt both parts' paths AND ran a full
  /// ClipperOffset per neighbour — and a single refused drop runs ~68 of those passes
  /// (<c>ResolveDropToContact</c>), i.e. ~1800 rebuilds on a 26-part sheet.
  /// </summary>
  internal sealed class DragCollisionCache
  {
    private readonly List<Neighbour> neighbours = new List<Neighbour>();
    private readonly Dictionary<long, List<List<IntPoint>>> shells = new Dictionary<long, List<List<IntPoint>>>();
    private readonly double minX;
    private readonly double minY;
    private readonly double maxX;
    private readonly double maxY;

    /// <param name="candidatePart">The dragged part's own geometry (NOT placed — the placement offset is
    /// supplied per query).</param>
    /// <param name="others">Each neighbour as already placed, with the clearance required against the
    /// dragged part.</param>
    internal DragCollisionCache(INfp candidatePart, IReadOnlyList<(INfp Placed, double Clearance)> others)
    {
      var raw = PlacementCollision.ToPaths(candidatePart);
      this.minX = candidatePart.MinX;
      this.minY = candidatePart.MinY;
      this.maxX = candidatePart.MaxX;
      this.maxY = candidatePart.MaxY;
      this.shells[0] = raw;

      foreach (var (placed, clearance) in others)
      {
        long key = ClearanceKey(clearance);
        if (!this.shells.ContainsKey(key))
        {
          this.shells[key] = PlacementCollision.InflateOuter(raw, clearance);
        }

        this.neighbours.Add(new Neighbour(
          PlacementCollision.ToPaths(placed), placed.MinX, placed.MinY, placed.MaxX, placed.MaxY, clearance));
      }
    }

    /// <summary>The dragged part's bounds when placed at (x, y).</summary>
    internal (double MinX, double MinY, double MaxX, double MaxY) BoundsAt(double x, double y)
      => (this.minX + x, this.minY + y, this.maxX + x, this.maxY + y);

    /// <summary>True when the dragged part, placed at (x, y), is closer than the required clearance to any
    /// neighbour. Same verdict as <see cref="PlacementCollision.TooClose"/> pair by pair.</summary>
    internal bool AnyTooClose(double x, double y)
    {
      long dx = (long)Math.Round(x * PlacementCollision.Scale);
      long dy = (long)Math.Round(y * PlacementCollision.Scale);
      var moved = new Dictionary<long, List<List<IntPoint>>>(); // one translation per distinct clearance

      foreach (var n in this.neighbours)
      {
        if (PlacementCollision.BoxesApart(
          this.minX + x, this.minY + y, this.maxX + x, this.maxY + y,
          n.MinX, n.MinY, n.MaxX, n.MaxY, n.Clearance))
        {
          continue;
        }

        long key = ClearanceKey(n.Clearance);
        if (!moved.TryGetValue(key, out var candidate))
        {
          moved[key] = candidate = PlacementCollision.Translate(this.shells[key], dx, dy);
        }

        if (PlacementCollision.Overlaps(candidate, n.Paths))
        {
          return true;
        }
      }

      return false;
    }

    private static long ClearanceKey(double clearance)
      => clearance <= 0 ? 0 : (long)Math.Round(clearance * PlacementCollision.Scale);

    private readonly struct Neighbour
    {
      internal Neighbour(List<List<IntPoint>> paths, double minX, double minY, double maxX, double maxY, double clearance)
      {
        this.Paths = paths;
        this.MinX = minX;
        this.MinY = minY;
        this.MaxX = maxX;
        this.MaxY = maxY;
        this.Clearance = clearance;
      }

      internal List<List<IntPoint>> Paths { get; }

      internal double MinX { get; }

      internal double MinY { get; }

      internal double MaxX { get; }

      internal double MaxY { get; }

      internal double Clearance { get; }
    }
  }
}
