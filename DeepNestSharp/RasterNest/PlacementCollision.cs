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

    /// <summary>
    /// The finest overlap that means anything at all: below this two placements are not distinguishable.
    /// <para>Placements are written to the project rounded to four decimals
    /// (<c>DoublePrecisionConverter</c>), so a difference under that cannot survive a save and reload, and
    /// welding two edges coincident always lands a hair to one side of zero. Unlike a kerf this is a
    /// property of how the number is stored rather than of how big the part is, so it is the same figure on
    /// an inch drawing and on a metric one, and there is nothing here to convert wrongly.</para>
    /// </summary>
    internal const double PlacementNoise = 1e-4;

    /// <summary>
    /// How deep this pair may bite into each other before it counts: the width of the cut that is going to
    /// run between them, when anybody knows it.
    /// <para>A kerf is known for a job that came in toolpathed from SheetCam, and that is also the job whose
    /// shared edge really is cut once. On a plain DXF nobody has said how wide the cut is, and standing a
    /// kerf in for one meant handing every pair five thou of forgiveness whether or not a cut was ever
    /// going to run there: two parts nested to touch could bite four thou into each other, pass in silence,
    /// and be exported with both outlines written in full. Without a figure, only the noise is forgiven.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Never below <see cref="PlacementNoise"/>. A cut can only ever forgive MORE than the precision the
    /// positions are stored at; it cannot make the check stricter than that, because below it two
    /// placements are not distinguishable in the first place. Reported from the shop: a per-part kerf of
    /// 1.39e-17 mm, floating point residue off a spinner, went in as a real cut width, and being above
    /// zero it REPLACED the floor instead of raising it. Tolerance fell by fifteen orders of magnitude
    /// and four parts the engine had deliberately placed touching came up red on a nest nobody had
    /// edited.
    /// </remarks>
    internal static double SliverFor(double kerfA, double kerfB)
    {
      double kerf = Math.Max(kerfA, kerfB);
      return Math.Max(kerf, PlacementNoise);
    }

    /// <summary>
    /// True when the two placed outlines are closer than <paramref name="clearance"/>.
    /// </summary>
    /// <param name="sliver">How deep an overlap the CUT itself will remove, normally the kerf. A common-line
    /// pair shares one cut, and that cut takes out a kerf of material, so parts overlapping by less than it
    /// come off the machine exactly as drawn. Where a clearance was asked for, what overlaps is the grown
    /// shells rather than the parts, so the same figure reads as clearance the pair is short of, which a
    /// cut's width of is nothing either.</param>
    internal static bool TooClose(INfp placedA, INfp placedB, double clearance, double sliver)
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

      return Overlaps(pathsA, ToPaths(placedB), sliver);
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

    /// <summary>
    /// True when the two path sets bite into each other by a real amount (holes respected via even-odd).
    /// <para>Judged by how DEEP the overlap goes, not by how much area it adds up to. Those are the same
    /// question only on a compact shape: along a shared edge they are nothing alike, and a fixed area
    /// threshold there means an absurd depth. At 1e-6 sq in over a 36in edge it meant 2.8e-8in, so a
    /// common-line pair, welded until its edges are coincident, was called overlapping for landing a hair
    /// on the wrong side of zero. Depth asks the question the shop floor asks: how far into my part.</para>
    /// </summary>
    /// <param name="sliver">Depth the cut itself removes; anything shallower is not there once it is cut.</param>
    internal static bool Overlaps(List<List<IntPoint>> a, List<List<IntPoint>> b, double sliver)
    {
      var clipper = new Clipper();
      clipper.AddPaths(a, PolyType.ptSubject, true);
      clipper.AddPaths(b, PolyType.ptClip, true);
      var solution = new List<List<IntPoint>>();
      clipper.Execute(ClipType.ctIntersection, solution, PolyFillType.pftEvenOdd, PolyFillType.pftEvenOdd);

      double limit = Math.Max(0, sliver) * Scale;
      foreach (var path in solution)
      {
        // Deepest piece wins rather than the total: a long sliver alongside a real bite must not average
        // the bite away.
        if (BitesDeeperThan(path, limit))
        {
          return true;
        }
      }

      return false;
    }

    /// <summary>
    /// Whether one piece of overlap bites deeper than <paramref name="limit"/>, in Clipper units.
    /// <para>Depth is the widest the piece gets, which is the diameter of the largest circle that fits
    /// inside it: a sliver of length L and depth d measures d, and a square bite of side s measures s.
    /// 2 x area / perimeter, which is what this used to ask, gives d for the sliver but only s/2 for the
    /// square, so a job with a six thou cut waved through a ten thou corner. That ratio is still the way in
    /// though, because for one piece it brackets the answer: the inscribed diameter is never below it and
    /// never above twice it. Almost every call is settled by one of those two bounds, and only the band
    /// between them pays for measuring properly.</para>
    /// </summary>
    private static bool BitesDeeperThan(List<IntPoint> path, double limit)
    {
      double area = Math.Abs(Clipper.Area(path));
      if (area <= 0 || path.Count < 3)
      {
        return false;
      }

      double perimeter = 0;
      for (int i = 0; i < path.Count; i++)
      {
        var p = path[i];
        var q = path[(i + 1) % path.Count];
        double dx = q.X - p.X;
        double dy = q.Y - p.Y;
        perimeter += Math.Sqrt((dx * dx) + (dy * dy));
      }

      if (perimeter <= 0)
      {
        return false;
      }

      double ratio = 2 * area / perimeter;
      if (ratio > limit)
      {
        return true; // deep enough on the lower bound alone
      }

      if (2 * ratio <= limit)
      {
        return false; // cannot reach the limit even at the upper bound
      }

      return InscribedDiameter(path, limit) > limit;
    }

    /// <summary>
    /// The diameter of the largest circle that fits inside the piece, found by shrinking it until nothing
    /// is left. Only ever called on the narrow band where the cheap bounds disagree, and it stops as soon
    /// as the answer is past <paramref name="limit"/>, so the count of offsets is small and fixed.
    /// </summary>
    private static double InscribedDiameter(List<IntPoint> path, double limit)
    {
      double lo = 0, hi = limit; // hi is a radius: a diameter past 2*hi was already settled above
      for (int i = 0; i < 8; i++)
      {
        double mid = (lo + hi) / 2;
        var offset = new ClipperOffset(2.0, Math.Max(1, mid * 0.0005));
        offset.AddPath(path, JoinType.jtMiter, EndType.etClosedPolygon);
        var shrunk = new List<List<IntPoint>>();
        offset.Execute(ref shrunk, -mid);
        if (shrunk.Count > 0)
        {
          lo = mid; // a circle of this radius still fits
        }
        else
        {
          hi = mid;
        }
      }

      return 2 * lo;
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
    /// dragged part and the depth its cut will remove (see <see cref="PlacementCollision.TooClose"/>).</param>
    internal DragCollisionCache(INfp candidatePart, IReadOnlyList<(INfp Placed, double Clearance, double Sliver)> others)
    {
      var raw = PlacementCollision.ToPaths(candidatePart);
      this.minX = candidatePart.MinX;
      this.minY = candidatePart.MinY;
      this.maxX = candidatePart.MaxX;
      this.maxY = candidatePart.MaxY;
      this.shells[0] = raw;

      foreach (var (placed, clearance, sliver) in others)
      {
        long key = ClearanceKey(clearance);
        if (!this.shells.ContainsKey(key))
        {
          this.shells[key] = PlacementCollision.InflateOuter(raw, clearance);
        }

        this.neighbours.Add(new Neighbour(
          PlacementCollision.ToPaths(placed), placed.MinX, placed.MinY, placed.MaxX, placed.MaxY, clearance, sliver));
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

        if (PlacementCollision.Overlaps(candidate, n.Paths, n.Sliver))
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
      internal Neighbour(List<List<IntPoint>> paths, double minX, double minY, double maxX, double maxY, double clearance, double sliver)
      {
        this.Paths = paths;
        this.MinX = minX;
        this.MinY = minY;
        this.MaxX = maxX;
        this.MaxY = maxY;
        this.Clearance = clearance;
        this.Sliver = sliver;
      }

      internal List<List<IntPoint>> Paths { get; }

      internal double MinX { get; }

      internal double MinY { get; }

      internal double MaxX { get; }

      internal double MaxY { get; }

      internal double Clearance { get; }

      internal double Sliver { get; }
    }
  }
}
