namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.Placement;

  /// <summary>
  /// Applies the normal part-to-part clearance around an intentional common-line graph. The clearance
  /// exception is geometric, not pair-wide: only the recorded pair of full outside edges, plus the short
  /// endpoint caps needed for their adjacent faces, may be closer than the normal gap.
  /// </summary>
  internal static class CommonEdgeClearance
  {
    internal readonly struct EdgeReference
    {
      internal EdgeReference(IPartPlacement partA, int edgeAIndex, IPartPlacement partB, int edgeBIndex)
      {
        this.PartA = partA;
        this.EdgeAIndex = edgeAIndex;
        this.PartB = partB;
        this.EdgeBIndex = edgeBIndex;
      }

      internal IPartPlacement PartA { get; }

      internal int EdgeAIndex { get; }

      internal IPartPlacement PartB { get; }

      internal int EdgeBIndex { get; }
    }

    /// <summary>
    /// The cut-safety verdict for one pair. Compatibility never enters this method: callers supply only
    /// concrete edge records selected by the planner and re-hydrated from the saved sheet.
    /// </summary>
    internal static bool TooClose(
      IPartPlacement a,
      IPartPlacement b,
      double normalClearance,
      double sliver,
      IReadOnlyList<EdgeReference> commonEdges,
      double commonLineGap,
      double geometryTolerance)
    {
      if (a?.PlacedPart == null || b?.PlacedPart == null)
      {
        return false;
      }

      double clearance = Math.Max(0, normalClearance);
      double geometryTol = Math.Max(0, geometryTolerance);
      double numericalTol = Math.Max(PlacementCollision.PlacementNoise, geometryTol);
      if (clearance <= 0 || commonEdges == null || commonEdges.Count == 0)
      {
        return PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, clearance, sliver);
      }

      List<EdgeReference> valid = ValidEdges(commonEdges, commonLineGap, geometryTol);
      return TooCloseWithValidEdges(
        a,
        b,
        clearance,
        sliver,
        valid,
        commonLineGap,
        geometryTol,
        numericalTol);
    }

    private static bool TooCloseWithValidEdges(
      IPartPlacement a,
      IPartPlacement b,
      double clearance,
      double sliver,
      IReadOnlyList<EdgeReference> valid,
      double commonLineGap,
      double geometryTol,
      double numericalTol)
    {
      if (!AreConnected(a, b, valid))
      {
        return PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, clearance, sliver);
      }

      // A common-line record only relaxes CLEARANCE. It never authorises material overlap. This also
      // catches containment, which a boundary-distance-only test cannot see.
      if (PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, 0, numericalTol))
      {
        return true;
      }

      // Keep the cheap and battle-tested Clipper result as the broad phase. Segment work is only needed
      // for a pair whose ordinary halos really meet.
      if (!PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, clearance, sliver))
      {
        return false;
      }

      List<BoundarySegment> aSegments = BoundarySegments(a, numericalTol);
      List<BoundarySegment> bSegments = BoundarySegments(b, numericalTol);
      double required = Math.Max(0, clearance - Math.Max(Math.Max(0, sliver), numericalTol));
      double requiredSquared = required * required;
      var closePairs = new HashSet<SegmentPair>();
      foreach (BoundarySegment edgeA in aSegments)
      {
        foreach (BoundarySegment edgeB in bSegments)
        {
          if (SegmentDistanceSquared(edgeA.Start, edgeA.End, edgeB.Start, edgeB.End, numericalTol) < requiredSquared)
          {
            closePairs.Add(new SegmentPair(edgeA.Index, edgeB.Index));
          }
        }
      }

      // A rounded corner can be tessellated into an arbitrary number of short chords. Treat the
      // below-clearance cells that are topologically connected to a recorded seam endpoint as one
      // local cap, rather than assuming that one chord (or one fixed travel distance) is enough.
      // Once both branches recover the normal gap there is no product-grid connection, so a later
      // concave lobe remains an ordinary collision.
      HashSet<SegmentPair> endpointCaps = EndpointConnectedPairs(
        a,
        b,
        valid,
        aSegments,
        bSegments,
        closePairs,
        commonLineGap,
        geometryTol,
        requiredSquared);
      foreach (SegmentPair pair in closePairs)
      {
        if (!IsRecordedPair(a, pair.EdgeAIndex, b, pair.EdgeBIndex, valid)
          && !endpointCaps.Contains(pair))
        {
          return true;
        }
      }

      // Connectivity alone must not turn a long, nearly-parallel branch into a common cut. Keep the
      // connected exception physically local to each junction. Segment clipping matters here: a long
      // straight branch may begin inside the cap and continue far outside it.
      HashSet<int> aCapIndexes = SharedJunctionVertices(a, b, valid, commonLineGap, geometryTol);
      HashSet<int> bCapIndexes = SharedJunctionVertices(b, a, valid, commonLineGap, geometryTol);
      List<Point> aCapPoints = JunctionPoints(a, aCapIndexes, numericalTol);
      List<Point> bCapPoints = JunctionPoints(b, bCapIndexes, numericalTol);
      double capRadius = (2 * clearance) + (2 * Math.Max(0, commonLineGap)) + numericalTol;
      if (HasCloseGeometryOutsideCaps(
        a,
        b,
        valid,
        aSegments,
        bSegments,
        aCapPoints,
        bCapPoints,
        capRadius,
        numericalTol,
        requiredSquared))
      {
        return true;
      }

      return false;
    }

    /// <summary>Sheet-wide form shared by final engine acceptance and the manual editor/export gate.</summary>
    internal static PlacementAcceptance.Unfit FindUnfit(
      IReadOnlyList<IPartPlacement> placements,
      double sheetWidth,
      double sheetHeight,
      double sheetEdgeMargin,
      Func<IPartPlacement, IPartPlacement, double> normalClearanceBetween,
      Func<IPartPlacement, IPartPlacement, double> sliverBetween,
      IReadOnlyList<EdgeReference> commonEdges,
      double commonLineGap,
      double geometryTolerance)
    {
      var unfit = new PlacementAcceptance.Unfit();
      sliverBetween ??= (a, b) => PlacementCollision.PlacementNoise;
      double geometryTol = Math.Max(0, geometryTolerance);
      double numericalTol = Math.Max(PlacementCollision.PlacementNoise, geometryTol);
      IReadOnlyList<EdgeReference> valid = commonEdges == null || commonEdges.Count == 0
        ? Array.Empty<EdgeReference>()
        : ValidEdges(commonEdges, commonLineGap, geometryTol);
      for (int i = 0; i < placements.Count; i++)
      {
        IPartPlacement a = placements[i];
        if (a?.PlacedPart == null)
        {
          continue;
        }

        if (PlacementAcceptance.IsOutsideUsableArea(a, sheetWidth, sheetHeight, sheetEdgeMargin))
        {
          unfit.OutsideMargin.Add(a);
          unfit.All.Add(a);
        }

        for (int j = i + 1; j < placements.Count; j++)
        {
          IPartPlacement b = placements[j];
          double clearance = b?.PlacedPart == null ? 0 : Math.Max(0, normalClearanceBetween(a, b));
          double sliver = b?.PlacedPart == null ? 0 : sliverBetween(a, b);
          if (b?.PlacedPart != null
            && (clearance <= 0 || valid.Count == 0
              ? PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, clearance, sliver)
              : TooCloseWithValidEdges(
                a,
                b,
                clearance,
                sliver,
                valid,
                commonLineGap,
                geometryTol,
                numericalTol)))
          {
            unfit.Overlapping.Add(a);
            unfit.Overlapping.Add(b);
            unfit.All.Add(a);
            unfit.All.Add(b);
          }
        }
      }

      return unfit;
    }

    private static List<EdgeReference> ValidEdges(
      IReadOnlyList<EdgeReference> commonEdges,
      double commonLineGap,
      double tolerance)
    {
      var result = new List<EdgeReference>(commonEdges.Count);
      foreach (EdgeReference edge in commonEdges)
      {
        if (edge.PartA != null
          && edge.PartB != null
          && CommonLineGeometryResolver.TryCreateCommonEdge(
            edge.PartA,
            edge.EdgeAIndex,
            edge.PartB,
            edge.EdgeBIndex,
            commonLineGap,
            tolerance,
            out _))
        {
          result.Add(edge);
        }
      }

      return result;
    }

    private static bool AreConnected(IPartPlacement a, IPartPlacement b, IReadOnlyList<EdgeReference> edges)
    {
      var reached = new HashSet<IPartPlacement> { a };
      bool grew;
      do
      {
        grew = false;
        foreach (EdgeReference edge in edges)
        {
          if (reached.Contains(edge.PartA))
          {
            grew |= reached.Add(edge.PartB);
          }

          if (reached.Contains(edge.PartB))
          {
            grew |= reached.Add(edge.PartA);
          }
        }
      }
      while (grew);

      return reached.Contains(b);
    }

    private static bool IsRecordedPair(
      IPartPlacement a,
      int edgeAIndex,
      IPartPlacement b,
      int edgeBIndex,
      IReadOnlyList<EdgeReference> edges)
      => edges.Any(edge =>
        (ReferenceEquals(edge.PartA, a)
          && edge.EdgeAIndex == edgeAIndex
          && ReferenceEquals(edge.PartB, b)
          && edge.EdgeBIndex == edgeBIndex)
        || (ReferenceEquals(edge.PartA, b)
          && edge.EdgeAIndex == edgeBIndex
          && ReferenceEquals(edge.PartB, a)
          && edge.EdgeBIndex == edgeAIndex));

    private static List<BoundarySegment> BoundarySegments(IPartPlacement part, double tolerance)
    {
      SvgPoint[] points = part.PlacedPart.Points;
      int count = RingCount(points, tolerance);
      var result = new List<BoundarySegment>(count);
      if (count < 2)
      {
        return result;
      }

      for (int i = 0; i < count; i++)
      {
        Point start = new Point(points[i].X, points[i].Y);
        Point end = new Point(points[(i + 1) % count].X, points[(i + 1) % count].Y);
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= tolerance)
        {
          continue;
        }

        result.Add(new BoundarySegment(i, start, end));
      }

      return result;
    }

    private static HashSet<SegmentPair> EndpointConnectedPairs(
      IPartPlacement a,
      IPartPlacement b,
      IReadOnlyList<EdgeReference> edges,
      IReadOnlyList<BoundarySegment> aSegments,
      IReadOnlyList<BoundarySegment> bSegments,
      IReadOnlySet<SegmentPair> closePairs,
      double commonLineGap,
      double tolerance,
      double requiredSquared)
    {
      var result = new HashSet<SegmentPair>();
      if (closePairs.Count == 0 || aSegments.Count == 0 || bSegments.Count == 0)
      {
        return result;
      }

      var pending = new Queue<SegmentPair>();
      void Seed(SegmentPair pair)
      {
        if (closePairs.Contains(pair) && result.Add(pair))
        {
          pending.Enqueue(pair);
        }
      }

      // A directly recorded seam is itself the bridge between its two endpoint caps.
      foreach (EdgeReference edge in edges)
      {
        if (ReferenceEquals(edge.PartA, a) && ReferenceEquals(edge.PartB, b))
        {
          Seed(new SegmentPair(edge.EdgeAIndex, edge.EdgeBIndex));
        }
        else if (ReferenceEquals(edge.PartA, b) && ReferenceEquals(edge.PartB, a))
        {
          Seed(new SegmentPair(edge.EdgeBIndex, edge.EdgeAIndex));
        }
      }

      // Diagonal members of a T/+ cluster have no direct seam pair, but their recorded seam vertices
      // still meet at the same intentional junction. Seed only incident cells at that physical node.
      List<(int Index, Point Point)> aVertices = CommonVertices(a, edges, tolerance);
      List<(int Index, Point Point)> bVertices = CommonVertices(b, edges, tolerance);
      double reach = Math.Max(0, 2 * commonLineGap) + (2 * tolerance);
      double reachSquared = reach * reach;
      var aPositions = aSegments
        .Select((segment, position) => (segment.Index, position))
        .ToDictionary(item => item.Index, item => item.position);
      var bPositions = bSegments
        .Select((segment, position) => (segment.Index, position))
        .ToDictionary(item => item.Index, item => item.position);
      foreach ((int _, Point aPoint) in aVertices)
      {
        foreach ((int _, Point bPoint) in bVertices)
        {
          if (DistanceSquared(aPoint, bPoint) > reachSquared)
          {
            continue;
          }

          foreach (BoundarySegment aIncident in IncidentSegments(aPoint, aSegments, tolerance))
          {
            foreach (BoundarySegment bIncident in IncidentSegments(bPoint, bSegments, tolerance))
            {
              Seed(new SegmentPair(aIncident.Index, bIncident.Index));
            }
          }
        }
      }

      while (pending.Count > 0)
      {
        SegmentPair pair = pending.Dequeue();
        int aPosition = aPositions[pair.EdgeAIndex];
        int bPosition = bPositions[pair.EdgeBIndex];
        BoundarySegment edgeA = aSegments[aPosition];
        BoundarySegment edgeB = bSegments[bPosition];

        TryCrossA(-1, edgeA.Start);
        TryCrossA(1, edgeA.End);
        TryCrossB(-1, edgeB.Start);
        TryCrossB(1, edgeB.End);

        void TryCrossA(int direction, Point sharedVertex)
        {
          if (PointSegmentDistanceSquared(sharedVertex, edgeB.Start, edgeB.End) >= requiredSquared)
          {
            return;
          }

          int nextPosition = Mod(aPosition + direction, aSegments.Count);
          Seed(new SegmentPair(aSegments[nextPosition].Index, pair.EdgeBIndex));
        }

        void TryCrossB(int direction, Point sharedVertex)
        {
          if (PointSegmentDistanceSquared(sharedVertex, edgeA.Start, edgeA.End) >= requiredSquared)
          {
            return;
          }

          int nextPosition = Mod(bPosition + direction, bSegments.Count);
          Seed(new SegmentPair(pair.EdgeAIndex, bSegments[nextPosition].Index));
        }
      }

      return result;
    }

    private static IEnumerable<BoundarySegment> IncidentSegments(
      Point vertex,
      IReadOnlyList<BoundarySegment> segments,
      double tolerance)
    {
      double toleranceSquared = tolerance * tolerance;
      foreach (BoundarySegment segment in segments)
      {
        if (DistanceSquared(vertex, segment.Start) <= toleranceSquared
          || DistanceSquared(vertex, segment.End) <= toleranceSquared)
        {
          yield return segment;
        }
      }
    }

    private static int Mod(int value, int modulus)
    {
      int result = value % modulus;
      return result < 0 ? result + modulus : result;
    }

    private static List<Point> JunctionPoints(
      IPartPlacement part,
      IReadOnlySet<int> indexes,
      double tolerance)
    {
      SvgPoint[] points = part.PlacedPart.Points;
      int count = RingCount(points, tolerance);
      return indexes
        .Where(index => index >= 0 && index < count)
        .Select(index => new Point(points[index].X, points[index].Y))
        .ToList();
    }

    private static bool HasCloseGeometryOutsideCaps(
      IPartPlacement a,
      IPartPlacement b,
      IReadOnlyList<EdgeReference> edges,
      IReadOnlyList<BoundarySegment> aSegments,
      IReadOnlyList<BoundarySegment> bSegments,
      IReadOnlyList<Point> aCaps,
      IReadOnlyList<Point> bCaps,
      double capRadius,
      double tolerance,
      double requiredSquared)
    {
      List<BoundarySegment> outsideASegments = OutsideCaps(aSegments, aCaps, capRadius, tolerance).ToList();
      List<BoundarySegment> outsideBSegments = OutsideCaps(bSegments, bCaps, capRadius, tolerance).ToList();
      foreach (BoundarySegment outsideA in outsideASegments)
      {
        foreach (BoundarySegment edgeB in bSegments)
        {
          if (!IsRecordedPair(a, outsideA.Index, b, edgeB.Index, edges)
            && SegmentDistanceSquared(
              outsideA.Start,
              outsideA.End,
              edgeB.Start,
              edgeB.End,
              tolerance) < requiredSquared)
          {
            return true;
          }
        }
      }

      foreach (BoundarySegment edgeA in aSegments)
      {
        foreach (BoundarySegment outsideB in outsideBSegments)
        {
          if (!IsRecordedPair(a, edgeA.Index, b, outsideB.Index, edges)
            && SegmentDistanceSquared(
              edgeA.Start,
              edgeA.End,
              outsideB.Start,
              outsideB.End,
              tolerance) < requiredSquared)
          {
            return true;
          }
        }
      }

      return false;
    }

    private static IEnumerable<BoundarySegment> OutsideCaps(
      IReadOnlyList<BoundarySegment> segments,
      IReadOnlyList<Point> caps,
      double radius,
      double tolerance)
    {
      foreach (BoundarySegment segment in segments)
      {
        foreach (BoundarySegment piece in OutsideCaps(segment, caps, radius, tolerance))
        {
          yield return piece;
        }
      }
    }

    private static IEnumerable<BoundarySegment> OutsideCaps(
      BoundarySegment segment,
      IReadOnlyList<Point> caps,
      double radius,
      double tolerance)
    {
      if (caps.Count == 0 || radius <= 0)
      {
        yield return segment;
        yield break;
      }

      double dx = segment.End.X - segment.Start.X;
      double dy = segment.End.Y - segment.Start.Y;
      double lengthSquared = (dx * dx) + (dy * dy);
      if (lengthSquared <= tolerance * tolerance)
      {
        yield break;
      }

      var covered = new List<(double Start, double End)>();
      double radiusSquared = radius * radius;
      foreach (Point cap in caps)
      {
        double fromCapX = segment.Start.X - cap.X;
        double fromCapY = segment.Start.Y - cap.Y;
        double projection = -((fromCapX * dx) + (fromCapY * dy)) / lengthSquared;
        double closestX = fromCapX + (projection * dx);
        double closestY = fromCapY + (projection * dy);
        double perpendicularSquared = (closestX * closestX) + (closestY * closestY);
        if (perpendicularSquared >= radiusSquared)
        {
          continue;
        }

        double halfSpan = Math.Sqrt((radiusSquared - perpendicularSquared) / lengthSquared);
        double start = Math.Max(0, projection - halfSpan);
        double end = Math.Min(1, projection + halfSpan);
        if (end > start)
        {
          covered.Add((start, end));
        }
      }

      if (covered.Count == 0)
      {
        yield return segment;
        yield break;
      }

      covered.Sort((left, right) => left.Start.CompareTo(right.Start));
      double cursor = 0;
      foreach ((double start, double end) in covered)
      {
        if (start > cursor)
        {
          BoundarySegment piece = SegmentPortion(segment, cursor, start);
          if (Distance(piece.Start, piece.End) > tolerance)
          {
            yield return piece;
          }
        }

        cursor = Math.Max(cursor, end);
        if (cursor >= 1)
        {
          yield break;
        }
      }

      if (cursor < 1)
      {
        BoundarySegment piece = SegmentPortion(segment, cursor, 1);
        if (Distance(piece.Start, piece.End) > tolerance)
        {
          yield return piece;
        }
      }
    }

    private static BoundarySegment SegmentPortion(BoundarySegment segment, double start, double end)
    {
      double dx = segment.End.X - segment.Start.X;
      double dy = segment.End.Y - segment.Start.Y;
      return new BoundarySegment(
        segment.Index,
        new Point(segment.Start.X + (start * dx), segment.Start.Y + (start * dy)),
        new Point(segment.Start.X + (end * dx), segment.Start.Y + (end * dy)));
    }

    /// <summary>
    /// Endpoint caps are pair-local. Two non-adjacent members of a long component do not earn a blanket
    /// exception merely because a graph path connects them; only vertices that meet at the same recorded
    /// T/+ junction (within the configured slot width) may be trimmed.
    /// </summary>
    private static HashSet<int> SharedJunctionVertices(
      IPartPlacement part,
      IPartPlacement other,
      IReadOnlyList<EdgeReference> edges,
      double commonLineGap,
      double tolerance)
    {
      List<(int Index, Point Point)> mine = CommonVertices(part, edges, tolerance);
      List<(int Index, Point Point)> theirs = CommonVertices(other, edges, tolerance);
      double reach = Math.Max(0, 2 * commonLineGap) + (2 * tolerance);
      double reachSquared = reach * reach;
      return new HashSet<int>(mine
        .Where(vertex => theirs.Any(candidate => DistanceSquared(vertex.Point, candidate.Point) <= reachSquared))
        .Select(vertex => vertex.Index));
    }

    private static List<(int Index, Point Point)> CommonVertices(
      IPartPlacement part,
      IReadOnlyList<EdgeReference> edges,
      double tolerance)
    {
      SvgPoint[] points = part.PlacedPart?.Points;
      int count = RingCount(points, tolerance);
      var indexes = new HashSet<int>();
      foreach (EdgeReference edge in edges)
      {
        int index = ReferenceEquals(edge.PartA, part)
          ? edge.EdgeAIndex
          : ReferenceEquals(edge.PartB, part)
            ? edge.EdgeBIndex
            : -1;
        if (index >= 0 && index < count)
        {
          indexes.Add(index);
          indexes.Add((index + 1) % count);
        }
      }

      return indexes
        .Select(index => (index, new Point(points[index].X, points[index].Y)))
        .ToList();
    }

    private static int RingCount(SvgPoint[] points, double tolerance)
    {
      int count = points?.Length ?? 0;
      if (count > 1
        && Math.Abs(points[0].X - points[count - 1].X) <= tolerance
        && Math.Abs(points[0].Y - points[count - 1].Y) <= tolerance)
      {
        count--;
      }

      return count;
    }

    private static double SegmentDistanceSquared(Point a, Point b, Point c, Point d, double tolerance)
    {
      if (SegmentsIntersect(a, b, c, d, tolerance))
      {
        return 0;
      }

      return Math.Min(
        Math.Min(PointSegmentDistanceSquared(a, c, d), PointSegmentDistanceSquared(b, c, d)),
        Math.Min(PointSegmentDistanceSquared(c, a, b), PointSegmentDistanceSquared(d, a, b)));
    }

    private static bool SegmentsIntersect(Point a, Point b, Point c, Point d, double tolerance)
    {
      double abC = Cross(a, b, c);
      double abD = Cross(a, b, d);
      double cdA = Cross(c, d, a);
      double cdB = Cross(c, d, b);
      double scale = Math.Max(1, Math.Max(Distance(a, b), Distance(c, d)));
      double epsilon = tolerance * scale;
      if (((abC > epsilon && abD < -epsilon) || (abC < -epsilon && abD > epsilon))
        && ((cdA > epsilon && cdB < -epsilon) || (cdA < -epsilon && cdB > epsilon)))
      {
        return true;
      }

      return Math.Abs(abC) <= epsilon && OnSegment(c, a, b, tolerance)
        || Math.Abs(abD) <= epsilon && OnSegment(d, a, b, tolerance)
        || Math.Abs(cdA) <= epsilon && OnSegment(a, c, d, tolerance)
        || Math.Abs(cdB) <= epsilon && OnSegment(b, c, d, tolerance);
    }

    private static bool OnSegment(Point p, Point a, Point b, double tolerance)
      => p.X >= Math.Min(a.X, b.X) - tolerance
        && p.X <= Math.Max(a.X, b.X) + tolerance
        && p.Y >= Math.Min(a.Y, b.Y) - tolerance
        && p.Y <= Math.Max(a.Y, b.Y) + tolerance;

    private static double PointSegmentDistanceSquared(Point p, Point a, Point b)
    {
      double dx = b.X - a.X;
      double dy = b.Y - a.Y;
      double lengthSquared = (dx * dx) + (dy * dy);
      if (lengthSquared <= 0)
      {
        return DistanceSquared(p, a);
      }

      double t = (((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared;
      t = Math.Max(0, Math.Min(1, t));
      return DistanceSquared(p, new Point(a.X + (t * dx), a.Y + (t * dy)));
    }

    private static double Cross(Point a, Point b, Point p)
      => ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

    private static double Distance(Point a, Point b) => Math.Sqrt(DistanceSquared(a, b));

    private static double DistanceSquared(Point a, Point b)
    {
      double dx = b.X - a.X;
      double dy = b.Y - a.Y;
      return (dx * dx) + (dy * dy);
    }

    private readonly struct BoundarySegment
    {
      internal BoundarySegment(int index, Point start, Point end)
      {
        this.Index = index;
        this.Start = start;
        this.End = end;
      }

      internal int Index { get; }

      internal Point Start { get; }

      internal Point End { get; }
    }

    private readonly struct SegmentPair : IEquatable<SegmentPair>
    {
      internal SegmentPair(int edgeAIndex, int edgeBIndex)
      {
        this.EdgeAIndex = edgeAIndex;
        this.EdgeBIndex = edgeBIndex;
      }

      internal int EdgeAIndex { get; }

      internal int EdgeBIndex { get; }

      public bool Equals(SegmentPair other)
        => this.EdgeAIndex == other.EdgeAIndex && this.EdgeBIndex == other.EdgeBIndex;

      public override bool Equals(object? obj) => obj is SegmentPair other && this.Equals(other);

      public override int GetHashCode() => HashCode.Combine(this.EdgeAIndex, this.EdgeBIndex);
    }

    private readonly struct Point
    {
      internal Point(double x, double y)
      {
        this.X = x;
        this.Y = y;
      }

      internal double X { get; }

      internal double Y { get; }
    }
  }
}
