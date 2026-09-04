namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Concurrent;
  using System.Collections.Generic;
  using System.Drawing;
  using System.IO;
  using System.Linq;
  using DeepNestLib.IO;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// Revalidates intentional common edges and turns their connected components into closed external
  /// contours plus a deterministic, deduplicated internal cut graph.
  /// </summary>
  public static class CommonLineGeometryResolver
  {
    private static readonly ConcurrentDictionary<SourceCacheKey, Lazy<SourceOuterDefinition>> SourceOuterCache
      = new ConcurrentDictionary<SourceCacheKey, Lazy<SourceOuterDefinition>>();

    public static CommonEdge CreateCommonEdge(
      IPartPlacement partA,
      int edgeAIndex,
      IPartPlacement partB,
      int edgeBIndex,
      double commonLineGap,
      double geometryTolerance)
    {
      ValidateSettings(commonLineGap, geometryTolerance);
      ResolvedEdge resolved = CalculateEdge(
        partA,
        edgeAIndex,
        partB,
        edgeBIndex,
        commonLineGap,
        geometryTolerance);

      return new CommonEdge(
        partA.Id,
        partB.Id,
        edgeAIndex,
        edgeBIndex,
        resolved.MidStart,
        resolved.MidEnd,
        commonLineGap);
    }

    public static bool TryCreateCommonEdge(
      IPartPlacement partA,
      int edgeAIndex,
      IPartPlacement partB,
      int edgeBIndex,
      double commonLineGap,
      double geometryTolerance,
      out CommonEdge commonEdge)
    {
      try
      {
        commonEdge = CreateCommonEdge(
          partA,
          edgeAIndex,
          partB,
          edgeBIndex,
          commonLineGap,
          geometryTolerance);
        return true;
      }
      catch (CommonLineValidationException)
      {
        commonEdge = null;
        return false;
      }
    }

    /// <summary>
    /// Resolves all recorded relationships. No recovery or legacy duplicate-line merging is attempted:
    /// an invalid relationship throws so an unsafe nest cannot be exported ambiguously.
    /// </summary>
    public static CommonLineGeometryResult Resolve(ISheetPlacement placement)
    {
      if (placement == null)
      {
        throw new ArgumentNullException(nameof(placement));
      }

      IReadOnlyList<CommonEdge> records = placement.CommonEdges;
      if (records == null || records.Count == 0)
      {
        return new CommonLineGeometryResult(Array.Empty<CommonLineCluster>());
      }

      ValidateSettings(placement.CommonLineGap, placement.GeometryTolerance);

      var placementsById = new Dictionary<int, IPartPlacement>();
      foreach (IPartPlacement part in placement.PartPlacements)
      {
        if (placementsById.ContainsKey(part.Id))
        {
          throw Invalid($"Part placement id {part.Id} is not unique on the sheet.");
        }

        placementsById.Add(part.Id, part);
      }

      var consumedEdges = new HashSet<PartEdgeKey>();
      var resolvedEdges = new List<ResolvedEdge>(records.Count);
      var union = new UnionFind();
      foreach (CommonEdge record in records)
      {
        if (record == null)
        {
          throw Invalid("A common-edge record is null.");
        }

        if (record.PartAId == record.PartBId)
        {
          throw Invalid($"Part {record.PartAId} cannot share an outside edge with itself.");
        }

        if (!placementsById.TryGetValue(record.PartAId, out IPartPlacement partA)
          || !placementsById.TryGetValue(record.PartBId, out IPartPlacement partB))
        {
          throw Invalid("A recorded common edge references a part that is no longer on this sheet.");
        }

        if (Math.Abs(record.Gap - placement.CommonLineGap) > placement.GeometryTolerance)
        {
          throw Invalid("A recorded common edge was created with a different Common Line Gap.");
        }

        if (!consumedEdges.Add(new PartEdgeKey(record.PartAId, record.EdgeAIndex))
          || !consumedEdges.Add(new PartEdgeKey(record.PartBId, record.EdgeBIndex)))
        {
          throw Invalid("An outside edge belongs to more than one common-line relationship.");
        }

        ResolvedEdge resolved = CalculateEdge(
          partA,
          record.EdgeAIndex,
          partB,
          record.EdgeBIndex,
          placement.CommonLineGap,
          placement.GeometryTolerance);

        if (record.Start == null || record.End == null
          || !SameUndirectedSegment(
              record.Start,
              record.End,
              resolved.MidStart,
              resolved.MidEnd,
              placement.GeometryTolerance))
        {
          throw Invalid("A recorded common edge no longer matches the final placed geometry.");
        }

        resolved.Record = record;
        resolvedEdges.Add(resolved);
        union.Join(record.PartAId, record.PartBId);
      }

      var clusters = new List<CommonLineCluster>();
      var sourceBoundaries = new Dictionary<string, SourceOuterDefinition>(StringComparer.OrdinalIgnoreCase);
      foreach (IGrouping<int, ResolvedEdge> group in resolvedEdges.GroupBy(e => union.Root(e.Record.PartAId)))
      {
        List<ResolvedEdge> clusterEdges = group.ToList();
        int[] memberIds = clusterEdges
          .SelectMany(e => new[] { e.Record.PartAId, e.Record.PartBId })
          .Distinct()
          .OrderBy(id => id)
          .ToArray();

        clusters.Add(BuildCluster(
          memberIds,
          clusterEdges,
          placementsById,
          placement.CommonLineGap,
          placement.GeometryTolerance,
          sourceBoundaries));
      }

      ValidateNoMaterialCollisions(placement.PartPlacements, placement.GeometryTolerance);
      ValidateCommonNetworkClear(clusters, placement.PartPlacements, placement.GeometryTolerance);

      return new CommonLineGeometryResult(clusters);
    }

    private static void ValidateNoMaterialCollisions(
      IReadOnlyList<IPartPlacement> placements,
      double tolerance)
    {
      for (int i = 0; i < placements.Count; i++)
      {
        for (int j = i + 1; j < placements.Count; j++)
        {
          IPartPlacement a = placements[i];
          IPartPlacement b = placements[j];
          IReadOnlyList<IReadOnlyList<CommonLinePoint>> ringsA = PlacedRings(a, tolerance);
          IReadOnlyList<IReadOnlyList<CommonLinePoint>> ringsB = PlacedRings(b, tolerance);
          if (ringsA.Any(ringA => ringsB.Any(ringB => RingsIntersect(ringA, ringB, tolerance))))
          {
            throw Invalid($"Parts {a.Id} and {b.Id} collide in the final common-line placement.");
          }

          if (ringsA[0].Count > 0 && IsPointInMaterial(ringsA[0][0], ringsB, tolerance))
          {
            throw Invalid($"Part {a.Id} overlaps material belonging to part {b.Id}.");
          }

          if (ringsB[0].Count > 0 && IsPointInMaterial(ringsB[0][0], ringsA, tolerance))
          {
            throw Invalid($"Part {b.Id} overlaps material belonging to part {a.Id}.");
          }
        }
      }
    }

    private static void ValidateCommonNetworkClear(
      IReadOnlyList<CommonLineCluster> clusters,
      IReadOnlyList<IPartPlacement> placements,
      double tolerance)
    {
      var ringsByPart = placements.ToDictionary(
        part => part.Id,
        part => PlacedRings(part, tolerance));
      foreach (CommonLineCluster cluster in clusters)
      {
        foreach (CommonLineSegment common in cluster.CommonLines)
        {
          var midpoint = new CommonLinePoint(
            (common.Start.X + common.End.X) / 2D,
            (common.Start.Y + common.End.Y) / 2D);
          foreach (IPartPlacement part in placements)
          {
            IReadOnlyList<IReadOnlyList<CommonLinePoint>> rings = ringsByPart[part.Id];
            if (rings.Any(ring => SegmentIntersectsRing(common.Start, common.End, ring, tolerance))
              || IsPointInMaterial(common.Start, rings, tolerance)
              || IsPointInMaterial(common.End, rings, tolerance)
              || IsPointInMaterial(midpoint, rings, tolerance))
            {
              throw Invalid($"A COMMON_LINE segment crosses material belonging to part {part.Id}.");
            }
          }
        }
      }
    }

    private static IReadOnlyList<IReadOnlyList<CommonLinePoint>> PlacedRings(
      IPartPlacement placement,
      double tolerance)
    {
      var rings = new List<IReadOnlyList<CommonLinePoint>>
      {
        PlacedRing(placement.Part, placement.X, placement.Y, tolerance),
      };
      foreach (INfp child in placement.Part.Children ?? Array.Empty<INfp>())
      {
        rings.Add(PlacedRing(child, placement.X, placement.Y, tolerance));
      }

      return rings;
    }

    private static IReadOnlyList<CommonLinePoint> PlacedRing(
      INfp polygon,
      double offsetX,
      double offsetY,
      double tolerance)
    {
      int count = RingCount(polygon.Points, tolerance);
      var result = new CommonLinePoint[count];
      for (int i = 0; i < count; i++)
      {
        result[i] = new CommonLinePoint(polygon.Points[i].X + offsetX, polygon.Points[i].Y + offsetY);
      }

      return result;
    }

    private static bool RingsIntersect(
      IReadOnlyList<CommonLinePoint> a,
      IReadOnlyList<CommonLinePoint> b,
      double tolerance)
    {
      for (int i = 0; i < a.Count; i++)
      {
        CommonLinePoint a0 = a[i];
        CommonLinePoint a1 = a[(i + 1) % a.Count];
        if (SegmentIntersectsRing(a0, a1, b, tolerance))
        {
          return true;
        }
      }

      return false;
    }

    private static bool SegmentIntersectsRing(
      CommonLinePoint start,
      CommonLinePoint end,
      IReadOnlyList<CommonLinePoint> ring,
      double tolerance)
    {
      for (int i = 0; i < ring.Count; i++)
      {
        CommonLinePoint a = ring[i];
        CommonLinePoint b = ring[(i + 1) % ring.Count];
        if (SegmentsIntersectOrTouch(start, end, a, b, tolerance))
        {
          return true;
        }
      }

      return false;
    }

    private static bool SegmentsIntersectOrTouch(
      CommonLinePoint a,
      CommonLinePoint b,
      CommonLinePoint c,
      CommonLinePoint d,
      double tolerance)
    {
      if (TryIntersection(
        new CommonLineSegment(a, b),
        new CommonLineSegment(c, d),
        tolerance,
        out _))
      {
        return true;
      }

      return PointOnSegment(a, c, d, tolerance)
        || PointOnSegment(b, c, d, tolerance)
        || PointOnSegment(c, a, b, tolerance)
        || PointOnSegment(d, a, b, tolerance);
    }

    private static bool PointOnSegment(
      CommonLinePoint point,
      CommonLinePoint start,
      CommonLinePoint end,
      double tolerance)
    {
      double dx = end.X - start.X;
      double dy = end.Y - start.Y;
      double length = Math.Sqrt((dx * dx) + (dy * dy));
      if (length <= tolerance)
      {
        return Distance(point, start) <= tolerance;
      }

      if (Math.Abs(Cross(point.X - start.X, point.Y - start.Y, dx / length, dy / length)) > tolerance)
      {
        return false;
      }

      double projection = Dot(point.X - start.X, point.Y - start.Y, dx / length, dy / length);
      return projection >= -tolerance && projection <= length + tolerance;
    }

    private static bool IsPointInMaterial(
      CommonLinePoint point,
      IReadOnlyList<IReadOnlyList<CommonLinePoint>> rings,
      double tolerance)
    {
      if (rings.Count == 0 || !PointInRing(point, rings[0], tolerance))
      {
        return false;
      }

      return !rings.Skip(1).Any(hole => PointInRing(point, hole, tolerance));
    }

    private static bool PointInRing(
      CommonLinePoint point,
      IReadOnlyList<CommonLinePoint> ring,
      double tolerance)
    {
      bool inside = false;
      for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
      {
        CommonLinePoint a = ring[j];
        CommonLinePoint b = ring[i];
        if (PointOnSegment(point, a, b, tolerance))
        {
          return true;
        }

        bool crosses = (a.Y > point.Y) != (b.Y > point.Y)
          && point.X < (((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X);
        if (crosses)
        {
          inside = !inside;
        }
      }

      return inside;
    }

    /// <summary>
    /// Uses the original closed LWPOLYLINE as the cluster boundary authority when available. The nester
    /// still addresses its tessellated NFP edges; only straight indexed edges may be removed, and every
    /// retained curved source segment keeps its exact DXF bulge. One immutable parse is shared by every
    /// copy of the same source during this Resolve call.
    /// </summary>
    private static bool TryAddSourceBoundary(
      IPartPlacement placement,
      int partId,
      IReadOnlySet<PartEdgeKey> removed,
      BoundaryGraph boundary,
      double tolerance,
      IDictionary<string, SourceOuterDefinition> sourceBoundaries)
    {
      SourceOuterDefinition source = GetSourceOuter(placement, sourceBoundaries);
      if (source.Status == SourceOuterStatus.NotAvailable
        || source.Status == SourceOuterStatus.StraightFallback)
      {
        return false;
      }

      if (source.Status != SourceOuterStatus.SupportedLwPolyline)
      {
        throw Invalid(source.Error ??
          $"Part {partId} has curved outside geometry in a format the common-line resolver cannot preserve.");
      }

      IReadOnlyList<CommonLinePoint> placedSamples = source.SampledPoints
        .Select(point => TransformSourcePoint(placement, point.X, point.Y))
        .ToArray();
      IReadOnlyList<CommonLinePoint> placedPart = PlacedRing(
        placement.Part,
        placement.X,
        placement.Y,
        tolerance);
      if (!SameCyclicRing(placedSamples, placedPart, tolerance))
      {
        throw Invalid(
          $"Part {partId}'s source LWPOLYLINE no longer matches its placed nesting geometry.");
      }

      var sourceSegments = new List<SourceBoundarySegment>(source.Vertices.Count);
      for (int i = 0; i < source.Vertices.Count; i++)
      {
        SourceBoundaryVertex start = source.Vertices[i];
        SourceBoundaryVertex end = source.Vertices[(i + 1) % source.Vertices.Count];
        CommonLinePoint transformedStart = TransformSourcePoint(placement, start.X, start.Y);
        CommonLinePoint transformedEnd = TransformSourcePoint(placement, end.X, end.Y);
        if (Distance(transformedStart, transformedEnd) <= tolerance)
        {
          throw Invalid($"Part {partId}'s source LWPOLYLINE contains a zero-length segment.");
        }

        sourceSegments.Add(new SourceBoundarySegment(
          transformedStart,
          transformedEnd,
          placement.IsMirrored ? -start.Bulge : start.Bulge));
      }

      var removedSourceSegments = new HashSet<int>();
      foreach (PartEdgeKey key in removed.Where(key => key.PartId == partId))
      {
        CommonLinePoint edgeStart = PlacedPoint(placement, key.EdgeIndex);
        int ringCount = RingCount(placement.Part.Points, tolerance);
        CommonLinePoint edgeEnd = PlacedPoint(placement, (key.EdgeIndex + 1) % ringCount);
        int[] matches = sourceSegments
          .Select((segment, index) => (segment, index))
          .Where(candidate => Math.Abs(candidate.segment.Bulge) <= 1e-12
            && SameUndirectedSegment(
              edgeStart,
              edgeEnd,
              candidate.segment.Start,
              candidate.segment.End,
              tolerance))
          .Select(candidate => candidate.index)
          .ToArray();
        if (matches.Length != 1)
        {
          throw Invalid(
            $"Part {partId}'s indexed common edge cannot be mapped uniquely to a straight source LWPOLYLINE segment.");
        }

        removedSourceSegments.Add(matches[0]);
      }

      for (int i = 0; i < sourceSegments.Count; i++)
      {
        if (!removedSourceSegments.Contains(i))
        {
          SourceBoundarySegment segment = sourceSegments[i];
          boundary.Add(segment.Start, segment.End, segment.Bulge);
        }
      }

      return true;
    }

    private static SourceOuterDefinition GetSourceOuter(
      IPartPlacement placement,
      IDictionary<string, SourceOuterDefinition> sourceBoundaries)
    {
      string sourceName = placement?.Part?.Name;
      if (string.IsNullOrWhiteSpace(sourceName)
        || !sourceName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
      {
        return SourceOuterDefinition.NotAvailable;
      }

      string path;
      try
      {
        path = Path.GetFullPath(sourceName);
      }
      catch
      {
        return SourceOuterDefinition.NotAvailable;
      }

      if (!File.Exists(path))
      {
        return SourceOuterDefinition.NotAvailable;
      }

      if (sourceBoundaries.TryGetValue(path, out SourceOuterDefinition cached))
      {
        return cached;
      }

      SourceOuterDefinition loaded;
      SourceCacheKey? attemptedCacheKey = null;
      try
      {
        var fileInfo = new FileInfo(path);
        var cacheKey = new SourceCacheKey(path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
        attemptedCacheKey = cacheKey;
        loaded = SourceOuterCache.GetOrAdd(
          cacheKey,
          key => new Lazy<SourceOuterDefinition>(
            () => LoadSourceOuter(key.Path),
            System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        // A source normally has one cache entry. Remove superseded fingerprints after the current
        // immutable DTO is available so replacing a DXF cannot grow the process cache without bound.
        foreach (SourceCacheKey oldKey in SourceOuterCache.Keys)
        {
          if (!oldKey.Equals(cacheKey)
            && string.Equals(oldKey.Path, path, StringComparison.OrdinalIgnoreCase))
          {
            SourceOuterCache.TryRemove(oldKey, out _);
          }
        }
      }
      catch (Exception ex)
      {
        // Do not pin a transient sharing/read failure in a faulted Lazy for the lifetime of the app.
        // A later nest should be allowed to retry the unchanged drawing.
        if (attemptedCacheKey.HasValue)
        {
          SourceOuterCache.TryRemove(attemptedCacheKey.Value, out _);
        }

        loaded = SourceOuterDefinition.Invalid(
          $"The common-line source DXF could not be reconstructed safely: {ex.Message}");
      }

      sourceBoundaries[path] = loaded;
      return loaded;
    }

    private static SourceOuterDefinition LoadSourceOuter(string path)
    {
      DxfFile file = DxfFile.Load(path);
      RawDetail<DxfEntity> detail = DxfParser.ConvertDxfToRawDetail(
        path,
        file.Entities.ToArray());
      LocalContour<DxfEntity>[] outers = detail.Outers
        .Where(contour => !contour.IsChild)
        .OfType<LocalContour<DxfEntity>>()
        .ToArray();
      if (outers.Length != 1)
      {
        return SourceOuterDefinition.Invalid(
          "A common-line source must contain exactly one outside contour.");
      }

      LocalContour<DxfEntity> outer = outers[0];
      DxfEntity[] entities = outer.Entities.ToArray();
      if (entities.Length == 1
        && entities[0] is DxfLwPolyline polyline
        && IsSupportedSourcePolyline(polyline))
      {
        return SourceOuterDefinition.Supported(
          polyline.Vertices.Select(vertex => new SourceBoundaryVertex(
            vertex.X,
            vertex.Y,
            vertex.Bulge)).ToArray(),
          NormalizeSampledSourceRing(outer.Points));
      }

      return entities.Any(SourceEntityContainsCurve)
        ? SourceOuterDefinition.Invalid(
          "Curved common-line outside geometry must be one closed planar LWPOLYLINE; other curve containers remain unsupported.")
        : SourceOuterDefinition.StraightFallback;
    }

    private static bool IsSupportedSourcePolyline(DxfLwPolyline polyline)
    {
      DxfVector extrusion = polyline.ExtrusionDirection;
      bool defaultPlane = extrusion.IsZeroVector
        || (Math.Abs(extrusion.X) <= 1e-12
          && Math.Abs(extrusion.Y) <= 1e-12
          && Math.Abs(extrusion.Z - 1D) <= 1e-12);
      return polyline.IsClosed
        && polyline.Vertices.Count >= 3
        && defaultPlane
        && double.IsFinite(polyline.Elevation)
        && polyline.Vertices.All(vertex => double.IsFinite(vertex.X)
          && double.IsFinite(vertex.Y)
          && double.IsFinite(vertex.Bulge));
    }

    private static bool SourceEntityContainsCurve(DxfEntity entity)
      => entity is DxfArc
        || entity is DxfCircle
        || entity is DxfEllipse
        || entity is DxfSpline
        || (entity is DxfLwPolyline lightweight
          && lightweight.Vertices.Any(vertex => Math.Abs(vertex.Bulge) > 1e-12))
        || (entity is DxfPolyline polyline
          && polyline.Vertices.Any(vertex => Math.Abs(vertex.Bulge) > 1e-12));

    private static IReadOnlyList<SourceSamplePoint> NormalizeSampledSourceRing(
      IReadOnlyList<PointF> points)
    {
      var result = points
        .Select(point => new SourceSamplePoint(point.X, point.Y))
        .ToList();
      if (result.Count > 1
        && Math.Abs(result[0].X - result[^1].X) <= 1e-6
        && Math.Abs(result[0].Y - result[^1].Y) <= 1e-6)
      {
        result.RemoveAt(result.Count - 1);
      }

      return result;
    }

    private static CommonLinePoint TransformSourcePoint(
      IPartPlacement placement,
      double x,
      double y)
    {
      if (placement.IsMirrored)
      {
        x = -x;
      }

      double angle = placement.Rotation * Math.PI / 180D;
      double cos = Math.Cos(angle);
      double sin = Math.Sin(angle);
      return new CommonLinePoint(
        (x * cos) - (y * sin) + placement.X,
        (x * sin) + (y * cos) + placement.Y);
    }

    private static bool SameCyclicRing(
      IReadOnlyList<CommonLinePoint> source,
      IReadOnlyList<CommonLinePoint> placed,
      double tolerance)
    {
      if (source.Count != placed.Count || source.Count == 0)
      {
        return false;
      }

      for (int offset = 0; offset < placed.Count; offset++)
      {
        if (!SamePoint(source[0], placed[offset], tolerance))
        {
          continue;
        }

        bool forward = true;
        bool reverse = true;
        for (int i = 1; i < source.Count && (forward || reverse); i++)
        {
          forward &= SamePoint(source[i], placed[(offset + i) % placed.Count], tolerance);
          reverse &= SamePoint(
            source[i],
            placed[(offset - i + placed.Count) % placed.Count],
            tolerance);
        }

        if (forward || reverse)
        {
          return true;
        }
      }

      return false;
    }

    private static CommonLineCluster BuildCluster(
      IReadOnlyList<int> memberIds,
      IReadOnlyList<ResolvedEdge> commonEdges,
      IReadOnlyDictionary<int, IPartPlacement> placementsById,
      double gap,
      double tolerance,
      IDictionary<string, SourceOuterDefinition> sourceBoundaries)
    {
      var quantizer = new PointQuantizer(tolerance);
      var removed = new HashSet<PartEdgeKey>();
      foreach (ResolvedEdge edge in commonEdges)
      {
        removed.Add(new PartEdgeKey(edge.Record.PartAId, edge.Record.EdgeAIndex));
        removed.Add(new PartEdgeKey(edge.Record.PartBId, edge.Record.EdgeBIndex));
      }

      var boundary = new BoundaryGraph(quantizer, tolerance);
      foreach (int partId in memberIds)
      {
        IPartPlacement part = placementsById[partId];
        int count = RingCount(part.Part.Points, tolerance);
        if (count < 3)
        {
          throw Invalid($"Part {partId} has no valid closed outside contour.");
        }

        if (TryAddSourceBoundary(
          part,
          partId,
          removed,
          boundary,
          tolerance,
          sourceBoundaries))
        {
          continue;
        }

        bool[] curvedSegments = (part.Part as NoFitPolygon)?.GetCurvedSegments();
        if (curvedSegments != null
          && (curvedSegments.Length < count || curvedSegments.Take(count).Any(isCurved => isCurved)))
        {
          throw Invalid(
            $"Part {partId} has curved outside geometry; V1 cannot rebuild its cluster boundary without distortion.");
        }

        for (int edgeIndex = 0; edgeIndex < count; edgeIndex++)
        {
          if (removed.Contains(new PartEdgeKey(partId, edgeIndex)))
          {
            continue;
          }

          CommonLinePoint start = PlacedPoint(part, edgeIndex);
          CommonLinePoint end = PlacedPoint(part, (edgeIndex + 1) % count);
          boundary.Add(start, end);
        }
      }

      // Classify against the retained graph before mutating it. In an orthogonal L chain a nominal
      // corner can lose both of its part edges (degree zero) while the paired endpoint still belongs to
      // the perimeter (degree one). Two such relationships must meet through that exact nominal corner;
      // joining their midpoints diagonally would bite material out of the corner.
      var immediateConnectors = new List<CommonLineSegment>();
      var deferredCorners = new Dictionary<PointKey, DeferredCorner>();
      foreach (ResolvedEdge edge in commonEdges)
      {
        ClassifyEndpointConnector(
          boundary,
          quantizer,
          edge.AStart,
          edge.BStart,
          edge.MidStart,
          immediateConnectors,
          deferredCorners);
        ClassifyEndpointConnector(
          boundary,
          quantizer,
          edge.AEnd,
          edge.BEnd,
          edge.MidEnd,
          immediateConnectors,
          deferredCorners);
      }

      foreach (CommonLineSegment connector in immediateConnectors)
      {
        boundary.Add(connector.Start, connector.End);
      }

      foreach (DeferredCorner corner in deferredCorners.Values)
      {
        CommonLinePoint[] midpoints = corner.Midpoints
          .GroupBy(point => quantizer.Key(point))
          .Select(group => group.First())
          .ToArray();
        if (midpoints.Length != 2)
        {
          throw Invalid(
            "A zero-degree common-line corner is dangling; exactly two perimeter seams must meet there.");
        }

        boundary.Add(midpoints[0], corner.NominalVertex);
        boundary.Add(corner.NominalVertex, midpoints[1]);
      }

      IReadOnlyList<CommonLinePath> outerPaths = boundary.BuildClosedPaths();
      if (outerPaths.Count == 0)
      {
        throw Invalid("A common-line cluster has no closed external boundary.");
      }

      IReadOnlyList<CommonLineSegment> network = BuildCommonNetwork(
        commonEdges.Select(e => new CommonLineSegment(e.MidStart, e.MidEnd)).ToList(),
        quantizer,
        gap,
        tolerance);

      if (network.Count == 0)
      {
        throw Invalid("A common-line cluster has no internal common cut.");
      }

      foreach (CommonLineSegment common in network)
      {
        foreach (CommonLinePath outer in outerPaths)
        {
          for (int i = 0; i < outer.Points.Count; i++)
          {
            CommonLinePoint a = outer.Points[i];
            CommonLinePoint b = outer.Points[(i + 1) % outer.Points.Count];
            if (Math.Abs(outer.Bulges[i]) <= 1e-12
              && SameUndirectedSegment(common.Start, common.End, a, b, tolerance))
            {
              throw Invalid("A common segment is duplicated in CUT_OUTER.");
            }
          }
        }
      }

      return new CommonLineCluster(memberIds, outerPaths, network);
    }

    private static void ClassifyEndpointConnector(
      BoundaryGraph graph,
      PointQuantizer quantizer,
      CommonLinePoint a,
      CommonLinePoint b,
      CommonLinePoint midpoint,
      ICollection<CommonLineSegment> immediate,
      IDictionary<PointKey, DeferredCorner> deferred)
    {
      int degreeA = graph.Degree(a);
      int degreeB = graph.Degree(b);
      if (degreeA == 0 && degreeB == 0)
      {
        return;
      }

      if (degreeA == 1 && degreeB == 1)
      {
        immediate.Add(new CommonLineSegment(a, midpoint));
        immediate.Add(new CommonLineSegment(midpoint, b));
        return;
      }

      if ((degreeA == 1 && degreeB == 0) || (degreeA == 0 && degreeB == 1))
      {
        CommonLinePoint outerVertex = degreeA == 1 ? a : b;
        CommonLinePoint zeroVertex = degreeA == 0 ? a : b;
        immediate.Add(new CommonLineSegment(outerVertex, midpoint));
        PointKey key = quantizer.Key(zeroVertex);
        if (!deferred.TryGetValue(key, out DeferredCorner corner))
        {
          corner = new DeferredCorner(zeroVertex);
          deferred.Add(key, corner);
        }

        corner.Midpoints.Add(midpoint);
        return;
      }

      throw Invalid("A common-edge endpoint creates an incompatible cluster junction.");
    }

    private static IReadOnlyList<CommonLineSegment> BuildCommonNetwork(
      IReadOnlyList<CommonLineSegment> input,
      PointQuantizer quantizer,
      double gap,
      double tolerance)
    {
      var merged = input
        .Select(s => CanonicalSegment(s, quantizer))
        .ToList();

      // Adjacent rows/columns in a grid leave a gap exactly CommonLineGap wide at the crossing. Join
      // only collinear runs across that deliberately-created slot; normal nesting gaps are larger and
      // are not eligible because they never entered this connected component.
      bool changed;
      do
      {
        changed = false;
        for (int i = 0; i < merged.Count && !changed; i++)
        {
          for (int j = i + 1; j < merged.Count; j++)
          {
            if (TryMergeCollinear(merged[i], merged[j], gap + (2D * tolerance), tolerance, out CommonLineSegment joined))
            {
              merged[i] = CanonicalSegment(joined, quantizer);
              merged.RemoveAt(j);
              changed = true;
              break;
            }
          }
        }
      }
      while (changed);

      var splitPoints = merged.Select(s => new List<CommonLinePoint> { s.Start, s.End }).ToArray();
      for (int i = 0; i < merged.Count; i++)
      {
        for (int j = i + 1; j < merged.Count; j++)
        {
          if (TryIntersection(merged[i], merged[j], tolerance, out CommonLinePoint intersection))
          {
            CommonLinePoint snapped = quantizer.Point(quantizer.Key(intersection));
            splitPoints[i].Add(snapped);
            splitPoints[j].Add(snapped);
          }
        }
      }

      var result = new List<CommonLineSegment>();
      var dedupe = new HashSet<SegmentKey>();
      for (int i = 0; i < merged.Count; i++)
      {
        CommonLineSegment source = merged[i];
        double dx = source.End.X - source.Start.X;
        double dy = source.End.Y - source.Start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        List<CommonLinePoint> ordered = splitPoints[i]
          .Select(p => quantizer.Point(quantizer.Key(p)))
          .GroupBy(p => quantizer.Key(p))
          .Select(g => g.First())
          .OrderBy(p => (((p.X - source.Start.X) * dx) + ((p.Y - source.Start.Y) * dy)) / lengthSquared)
          .ToList();

        for (int p = 0; p + 1 < ordered.Count; p++)
        {
          if (Distance(ordered[p], ordered[p + 1]) <= tolerance)
          {
            continue;
          }

          PointKey startKey = quantizer.Key(ordered[p]);
          PointKey endKey = quantizer.Key(ordered[p + 1]);
          var key = new SegmentKey(startKey, endKey);
          if (dedupe.Add(key))
          {
            result.Add(new CommonLineSegment(quantizer.Point(key.A), quantizer.Point(key.B)));
          }
        }
      }

      return result;
    }

    private static bool TryMergeCollinear(
      CommonLineSegment a,
      CommonLineSegment b,
      double bridgeDistance,
      double tolerance,
      out CommonLineSegment merged)
    {
      double adx = a.End.X - a.Start.X;
      double ady = a.End.Y - a.Start.Y;
      double bdx = b.End.X - b.Start.X;
      double bdy = b.End.Y - b.Start.Y;
      double alen = Math.Sqrt((adx * adx) + (ady * ady));
      double blen = Math.Sqrt((bdx * bdx) + (bdy * bdy));
      if (alen <= tolerance || blen <= tolerance)
      {
        merged = null;
        return false;
      }

      double ux = adx / alen;
      double uy = ady / alen;
      if (ux < -1e-12 || (Math.Abs(ux) <= 1e-12 && uy < 0))
      {
        ux = -ux;
        uy = -uy;
      }

      if (Math.Abs((ux * bdy) - (uy * bdx)) > tolerance)
      {
        merged = null;
        return false;
      }

      if (Math.Abs(Cross(b.Start.X - a.Start.X, b.Start.Y - a.Start.Y, ux, uy)) > tolerance
        || Math.Abs(Cross(b.End.X - a.Start.X, b.End.Y - a.Start.Y, ux, uy)) > tolerance)
      {
        merged = null;
        return false;
      }

      double a0 = Dot(a.Start.X, a.Start.Y, ux, uy);
      double a1 = Dot(a.End.X, a.End.Y, ux, uy);
      double b0 = Dot(b.Start.X, b.Start.Y, ux, uy);
      double b1 = Dot(b.End.X, b.End.Y, ux, uy);
      double amin = Math.Min(a0, a1);
      double amax = Math.Max(a0, a1);
      double bmin = Math.Min(b0, b1);
      double bmax = Math.Max(b0, b1);
      double between = Math.Max(amin, bmin) - Math.Min(amax, bmax);
      if (between > bridgeDistance)
      {
        merged = null;
        return false;
      }

      double min = Math.Min(amin, bmin);
      double max = Math.Max(amax, bmax);
      double anchorProjection = Dot(a.Start.X, a.Start.Y, ux, uy);
      merged = new CommonLineSegment(
        new CommonLinePoint(a.Start.X + ((min - anchorProjection) * ux), a.Start.Y + ((min - anchorProjection) * uy)),
        new CommonLinePoint(a.Start.X + ((max - anchorProjection) * ux), a.Start.Y + ((max - anchorProjection) * uy)));
      return true;
    }

    private static bool TryIntersection(
      CommonLineSegment a,
      CommonLineSegment b,
      double tolerance,
      out CommonLinePoint intersection)
    {
      double rx = a.End.X - a.Start.X;
      double ry = a.End.Y - a.Start.Y;
      double sx = b.End.X - b.Start.X;
      double sy = b.End.Y - b.Start.Y;
      double denominator = Cross(rx, ry, sx, sy);
      double scale = Math.Sqrt(((rx * rx) + (ry * ry)) * ((sx * sx) + (sy * sy)));
      if (scale <= tolerance * tolerance || Math.Abs(denominator) <= tolerance * Math.Sqrt(scale))
      {
        intersection = null;
        return false;
      }

      double qpx = b.Start.X - a.Start.X;
      double qpy = b.Start.Y - a.Start.Y;
      double t = Cross(qpx, qpy, sx, sy) / denominator;
      double u = Cross(qpx, qpy, rx, ry) / denominator;
      double tTolerance = tolerance / Math.Max(Math.Sqrt((rx * rx) + (ry * ry)), tolerance);
      double uTolerance = tolerance / Math.Max(Math.Sqrt((sx * sx) + (sy * sy)), tolerance);
      if (t < -tTolerance || t > 1D + tTolerance || u < -uTolerance || u > 1D + uTolerance)
      {
        intersection = null;
        return false;
      }

      intersection = new CommonLinePoint(a.Start.X + (t * rx), a.Start.Y + (t * ry));
      return true;
    }

    private static CommonLineSegment CanonicalSegment(CommonLineSegment segment, PointQuantizer quantizer)
    {
      PointKey a = quantizer.Key(segment.Start);
      PointKey b = quantizer.Key(segment.End);
      var key = new SegmentKey(a, b);
      return new CommonLineSegment(quantizer.Point(key.A), quantizer.Point(key.B));
    }

    private static ResolvedEdge CalculateEdge(
      IPartPlacement partA,
      int edgeAIndex,
      IPartPlacement partB,
      int edgeBIndex,
      double gap,
      double tolerance)
    {
      if (partA == null || partB == null)
      {
        throw Invalid("Both common-line parts are required.");
      }

      if (partA.Id == partB.Id)
      {
        throw Invalid("A part cannot share an outside edge with itself.");
      }

      EdgeGeometry edgeA = GetEdge(partA, edgeAIndex, tolerance);
      EdgeGeometry edgeB = GetEdge(partB, edgeBIndex, tolerance);
      if (Math.Abs(edgeA.Length - edgeB.Length) > tolerance)
      {
        throw Invalid("V1 common edges must have the same usable length.");
      }

      double cross = Math.Abs(Cross(edgeA.UnitX, edgeA.UnitY, edgeB.UnitX, edgeB.UnitY));
      double angularTolerance = Math.Min(1e-3, tolerance / Math.Max(Math.Min(edgeA.Length, edgeB.Length), tolerance));
      if (cross > angularTolerance)
      {
        throw Invalid("Common edges are not parallel within GeometryTolerance.");
      }

      double normalDot = Dot(edgeA.OutwardX, edgeA.OutwardY, edgeB.OutwardX, edgeB.OutwardY);
      if (normalDot > -1D + Math.Max(1e-10, 2D * angularTolerance * angularTolerance))
      {
        throw Invalid("Common edges do not have opposite-facing outward normals.");
      }

      CommonLinePoint bStart = edgeB.Start;
      CommonLinePoint bEnd = edgeB.End;
      double direct = Distance(edgeA.Start, bStart) + Distance(edgeA.End, bEnd);
      double reverse = Distance(edgeA.Start, bEnd) + Distance(edgeA.End, bStart);
      if (reverse < direct)
      {
        CommonLinePoint swap = bStart;
        bStart = bEnd;
        bEnd = swap;
      }

      ValidatePairedEndpoint(edgeA.Start, bStart, edgeA, gap, tolerance);
      ValidatePairedEndpoint(edgeA.End, bEnd, edgeA, gap, tolerance);

      var midStart = new CommonLinePoint((edgeA.Start.X + bStart.X) / 2D, (edgeA.Start.Y + bStart.Y) / 2D);
      var midEnd = new CommonLinePoint((edgeA.End.X + bEnd.X) / 2D, (edgeA.End.Y + bEnd.Y) / 2D);
      if (Distance(midStart, midEnd) <= tolerance)
      {
        throw Invalid("A common edge has zero usable length.");
      }

      Canonicalize(ref midStart, ref midEnd);
      CommonLinePoint aStart = edgeA.Start;
      CommonLinePoint aEnd = edgeA.End;
      CommonLinePoint pairedBStart = bStart;
      CommonLinePoint pairedBEnd = bEnd;
      if (!SamePoint(midStart, new CommonLinePoint((aStart.X + pairedBStart.X) / 2D, (aStart.Y + pairedBStart.Y) / 2D), tolerance))
      {
        Swap(ref aStart, ref aEnd);
        Swap(ref pairedBStart, ref pairedBEnd);
      }

      return new ResolvedEdge
      {
        AStart = aStart,
        AEnd = aEnd,
        BStart = pairedBStart,
        BEnd = pairedBEnd,
        MidStart = midStart,
        MidEnd = midEnd,
      };
    }

    private static void ValidatePairedEndpoint(
      CommonLinePoint a,
      CommonLinePoint b,
      EdgeGeometry edgeA,
      double gap,
      double tolerance)
    {
      double dx = b.X - a.X;
      double dy = b.Y - a.Y;
      double tangent = Dot(dx, dy, edgeA.UnitX, edgeA.UnitY);
      double separation = Dot(dx, dy, edgeA.OutwardX, edgeA.OutwardY);
      if (Math.Abs(tangent) > tolerance)
      {
        throw Invalid("Common edges do not overlap over their full intended section.");
      }

      if (separation <= 0D || Math.Abs(separation - gap) > tolerance
        || Math.Abs(Math.Sqrt((dx * dx) + (dy * dy)) - gap) > tolerance)
      {
        throw Invalid("Common-edge separation does not equal the active Common Line Gap.");
      }
    }

    private static EdgeGeometry GetEdge(IPartPlacement placement, int edgeIndex, double tolerance)
    {
      SvgPoint[] points = placement.Part?.Points;
      int count = RingCount(points, tolerance);
      if (count < 3 || edgeIndex < 0 || edgeIndex >= count)
      {
        throw Invalid($"Edge index {edgeIndex} is not a valid outside-contour edge for part {placement.Id}.");
      }

      bool[] curved = (placement.Part as NoFitPolygon)?.GetCurvedSegments();
      if (curved != null && edgeIndex < curved.Length && curved[edgeIndex])
      {
        throw Invalid("V1 common-line cutting supports straight outside segments only.");
      }

      CommonLinePoint start = PlacedPoint(placement, edgeIndex);
      CommonLinePoint end = PlacedPoint(placement, (edgeIndex + 1) % count);
      double dx = end.X - start.X;
      double dy = end.Y - start.Y;
      double length = Math.Sqrt((dx * dx) + (dy * dy));
      if (length <= tolerance)
      {
        throw Invalid("A common edge is zero-length or smaller than GeometryTolerance.");
      }

      double signedArea = SignedArea(points, count);
      if (Math.Abs(signedArea) <= tolerance * tolerance)
      {
        throw Invalid("A part outside contour has no valid orientation.");
      }

      double ux = dx / length;
      double uy = dy / length;
      bool counterClockwise = signedArea > 0D;
      double outwardX = counterClockwise ? uy : -uy;
      double outwardY = counterClockwise ? -ux : ux;
      return new EdgeGeometry(start, end, length, ux, uy, outwardX, outwardY);
    }

    private static int RingCount(SvgPoint[] points, double tolerance)
    {
      if (points == null)
      {
        return 0;
      }

      int count = points.Length;
      if (count > 1
        && Math.Abs(points[0].X - points[count - 1].X) <= tolerance
        && Math.Abs(points[0].Y - points[count - 1].Y) <= tolerance)
      {
        count--;
      }

      return count;
    }

    private static double SignedArea(SvgPoint[] points, int count)
    {
      double twiceArea = 0D;
      for (int i = 0; i < count; i++)
      {
        SvgPoint a = points[i];
        SvgPoint b = points[(i + 1) % count];
        twiceArea += (a.X * b.Y) - (b.X * a.Y);
      }

      return twiceArea / 2D;
    }

    private static CommonLinePoint PlacedPoint(IPartPlacement placement, int index)
      => new CommonLinePoint(
        placement.Part.Points[index].X + placement.X,
        placement.Part.Points[index].Y + placement.Y);

    private static void ValidateSettings(double gap, double tolerance)
    {
      if (!double.IsFinite(gap) || gap <= 0D)
      {
        throw Invalid("Common Line Gap must be a finite positive distance.");
      }

      if (!double.IsFinite(tolerance) || tolerance <= 0D)
      {
        throw Invalid("GeometryTolerance must be a finite positive distance.");
      }

      if (gap <= tolerance)
      {
        throw Invalid("Common Line Gap must be larger than the independent GeometryTolerance.");
      }
    }

    private static bool SameUndirectedSegment(
      CommonLinePoint a0,
      CommonLinePoint a1,
      CommonLinePoint b0,
      CommonLinePoint b1,
      double tolerance)
      => (SamePoint(a0, b0, tolerance) && SamePoint(a1, b1, tolerance))
        || (SamePoint(a0, b1, tolerance) && SamePoint(a1, b0, tolerance));

    private static bool SamePoint(CommonLinePoint a, CommonLinePoint b, double tolerance)
      => a != null && b != null && Distance(a, b) <= tolerance;

    private static double Distance(CommonLinePoint a, CommonLinePoint b)
    {
      double dx = b.X - a.X;
      double dy = b.Y - a.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double Dot(double ax, double ay, double bx, double by) => (ax * bx) + (ay * by);

    private static double Cross(double ax, double ay, double bx, double by) => (ax * by) - (ay * bx);

    private static void Canonicalize(ref CommonLinePoint start, ref CommonLinePoint end)
    {
      if (start.X > end.X || (start.X == end.X && start.Y > end.Y))
      {
        Swap(ref start, ref end);
      }
    }

    private static void Swap(ref CommonLinePoint a, ref CommonLinePoint b)
    {
      CommonLinePoint swap = a;
      a = b;
      b = swap;
    }

    private static CommonLineValidationException Invalid(string message)
      => new CommonLineValidationException(message);

    private sealed class EdgeGeometry
    {
      public EdgeGeometry(
        CommonLinePoint start,
        CommonLinePoint end,
        double length,
        double unitX,
        double unitY,
        double outwardX,
        double outwardY)
      {
        this.Start = start;
        this.End = end;
        this.Length = length;
        this.UnitX = unitX;
        this.UnitY = unitY;
        this.OutwardX = outwardX;
        this.OutwardY = outwardY;
      }

      public CommonLinePoint Start { get; }

      public CommonLinePoint End { get; }

      public double Length { get; }

      public double UnitX { get; }

      public double UnitY { get; }

      public double OutwardX { get; }

      public double OutwardY { get; }
    }

    private enum SourceOuterStatus
    {
      NotAvailable,
      StraightFallback,
      SupportedLwPolyline,
      Invalid,
    }

    private sealed class SourceOuterDefinition
    {
      private SourceOuterDefinition(
        SourceOuterStatus status,
        IReadOnlyList<SourceBoundaryVertex> vertices = null,
        IReadOnlyList<SourceSamplePoint> sampledPoints = null,
        string error = null)
      {
        this.Status = status;
        this.Vertices = vertices ?? Array.Empty<SourceBoundaryVertex>();
        this.SampledPoints = sampledPoints ?? Array.Empty<SourceSamplePoint>();
        this.Error = error;
      }

      public static SourceOuterDefinition NotAvailable { get; }
        = new SourceOuterDefinition(SourceOuterStatus.NotAvailable);

      public static SourceOuterDefinition StraightFallback { get; }
        = new SourceOuterDefinition(SourceOuterStatus.StraightFallback);

      public SourceOuterStatus Status { get; }

      public IReadOnlyList<SourceBoundaryVertex> Vertices { get; }

      public IReadOnlyList<SourceSamplePoint> SampledPoints { get; }

      public string Error { get; }

      public static SourceOuterDefinition Supported(
        IReadOnlyList<SourceBoundaryVertex> vertices,
        IReadOnlyList<SourceSamplePoint> sampledPoints)
        => new SourceOuterDefinition(
          SourceOuterStatus.SupportedLwPolyline,
          vertices,
          sampledPoints);

      public static SourceOuterDefinition Invalid(string error)
        => new SourceOuterDefinition(SourceOuterStatus.Invalid, error: error);
    }

    private readonly struct SourceBoundaryVertex
    {
      public SourceBoundaryVertex(double x, double y, double bulge)
      {
        this.X = x;
        this.Y = y;
        this.Bulge = bulge;
      }

      public double X { get; }

      public double Y { get; }

      public double Bulge { get; }
    }

    private readonly struct SourceCacheKey : IEquatable<SourceCacheKey>
    {
      public SourceCacheKey(string path, long length, long lastWriteUtcTicks)
      {
        this.Path = path;
        this.Length = length;
        this.LastWriteUtcTicks = lastWriteUtcTicks;
      }

      public string Path { get; }

      public long Length { get; }

      public long LastWriteUtcTicks { get; }

      public bool Equals(SourceCacheKey other)
        => this.Length == other.Length
          && this.LastWriteUtcTicks == other.LastWriteUtcTicks
          && string.Equals(this.Path, other.Path, StringComparison.OrdinalIgnoreCase);

      public override bool Equals(object obj)
        => obj is SourceCacheKey other && this.Equals(other);

      public override int GetHashCode()
        => HashCode.Combine(
          StringComparer.OrdinalIgnoreCase.GetHashCode(this.Path),
          this.Length,
          this.LastWriteUtcTicks);
    }

    private readonly struct SourceSamplePoint
    {
      public SourceSamplePoint(double x, double y)
      {
        this.X = x;
        this.Y = y;
      }

      public double X { get; }

      public double Y { get; }
    }

    private sealed class SourceBoundarySegment
    {
      public SourceBoundarySegment(CommonLinePoint start, CommonLinePoint end, double bulge)
      {
        this.Start = start;
        this.End = end;
        this.Bulge = bulge;
      }

      public CommonLinePoint Start { get; }

      public CommonLinePoint End { get; }

      public double Bulge { get; }
    }

    private sealed class ResolvedEdge
    {
      public CommonEdge Record { get; set; }

      public CommonLinePoint AStart { get; set; }

      public CommonLinePoint AEnd { get; set; }

      public CommonLinePoint BStart { get; set; }

      public CommonLinePoint BEnd { get; set; }

      public CommonLinePoint MidStart { get; set; }

      public CommonLinePoint MidEnd { get; set; }
    }

    private sealed class DeferredCorner
    {
      public DeferredCorner(CommonLinePoint nominalVertex)
      {
        this.NominalVertex = nominalVertex;
      }

      public CommonLinePoint NominalVertex { get; }

      public IList<CommonLinePoint> Midpoints { get; } = new List<CommonLinePoint>();
    }

    private readonly struct PartEdgeKey : IEquatable<PartEdgeKey>
    {
      public PartEdgeKey(int partId, int edgeIndex)
      {
        this.PartId = partId;
        this.EdgeIndex = edgeIndex;
      }

      public int PartId { get; }

      public int EdgeIndex { get; }

      public bool Equals(PartEdgeKey other) => this.PartId == other.PartId && this.EdgeIndex == other.EdgeIndex;

      public override bool Equals(object obj) => obj is PartEdgeKey other && this.Equals(other);

      public override int GetHashCode() => HashCode.Combine(this.PartId, this.EdgeIndex);
    }

    private readonly struct PointKey : IEquatable<PointKey>, IComparable<PointKey>
    {
      public PointKey(long x, long y)
      {
        this.X = x;
        this.Y = y;
      }

      public long X { get; }

      public long Y { get; }

      public int CompareTo(PointKey other)
      {
        int x = this.X.CompareTo(other.X);
        return x != 0 ? x : this.Y.CompareTo(other.Y);
      }

      public bool Equals(PointKey other) => this.X == other.X && this.Y == other.Y;

      public override bool Equals(object obj) => obj is PointKey other && this.Equals(other);

      public override int GetHashCode() => HashCode.Combine(this.X, this.Y);
    }

    private readonly struct SegmentKey : IEquatable<SegmentKey>
    {
      public SegmentKey(PointKey first, PointKey second)
      {
        if (first.CompareTo(second) <= 0)
        {
          this.A = first;
          this.B = second;
        }
        else
        {
          this.A = second;
          this.B = first;
        }
      }

      public PointKey A { get; }

      public PointKey B { get; }

      public bool Equals(SegmentKey other) => this.A.Equals(other.A) && this.B.Equals(other.B);

      public override bool Equals(object obj) => obj is SegmentKey other && this.Equals(other);

      public override int GetHashCode() => HashCode.Combine(this.A, this.B);
    }

    private readonly struct BoundaryEdge
    {
      public BoundaryEdge(PointKey start, PointKey end, double bulge)
      {
        this.Start = start;
        this.End = end;
        this.Bulge = bulge;
      }

      public PointKey Start { get; }

      public PointKey End { get; }

      public double Bulge { get; }
    }

    private sealed class PointQuantizer
    {
      private readonly double scale;

      public PointQuantizer(double tolerance)
      {
        this.scale = Math.Min(1_000_000_000D, Math.Max(1D, Math.Ceiling(1D / tolerance)));
      }

      public PointKey Key(CommonLinePoint point)
        => new PointKey(
          checked((long)Math.Round(point.X * this.scale)),
          checked((long)Math.Round(point.Y * this.scale)));

      public CommonLinePoint Point(PointKey key)
        => new CommonLinePoint(key.X / this.scale, key.Y / this.scale);
    }

    private sealed class BoundaryGraph
    {
      private readonly Dictionary<PointKey, List<int>> adjacency = new Dictionary<PointKey, List<int>>();
      private readonly HashSet<SegmentKey> unique = new HashSet<SegmentKey>();
      private readonly List<BoundaryEdge> edges = new List<BoundaryEdge>();
      private readonly PointQuantizer quantizer;
      private readonly double tolerance;

      public BoundaryGraph(PointQuantizer quantizer, double tolerance)
      {
        this.quantizer = quantizer;
        this.tolerance = tolerance;
      }

      public int Degree(CommonLinePoint point)
      {
        PointKey key = this.quantizer.Key(point);
        return this.adjacency.TryGetValue(key, out List<int> incident) ? incident.Count : 0;
      }

      public void Add(CommonLinePoint start, CommonLinePoint end, double bulge = 0D)
      {
        if (!double.IsFinite(bulge))
        {
          throw Invalid("A common-line cluster boundary contains a non-finite bulge.");
        }

        PointKey a = this.quantizer.Key(start);
        PointKey b = this.quantizer.Key(end);
        if (a.Equals(b) || Distance(this.quantizer.Point(a), this.quantizer.Point(b)) <= this.tolerance)
        {
          throw Invalid("A boolean boundary operation produced a zero-length or tiny segment.");
        }

        var segment = new SegmentKey(a, b);
        if (!this.unique.Add(segment))
        {
          throw Invalid("A common-line cluster boundary contains duplicate geometry.");
        }

        int index = this.edges.Count;
        this.edges.Add(new BoundaryEdge(a, b, bulge));
        this.AddIncident(segment.A, index);
        this.AddIncident(segment.B, index);
      }

      public IReadOnlyList<CommonLinePath> BuildClosedPaths()
      {
        foreach (KeyValuePair<PointKey, List<int>> node in this.adjacency)
        {
          if (node.Value.Count != 2)
          {
            throw Invalid("CUT_OUTER reconstruction contains an open or branched contour.");
          }
        }

        var used = new bool[this.edges.Count];
        var paths = new List<CommonLinePath>();
        for (int seed = 0; seed < this.edges.Count; seed++)
        {
          if (used[seed])
          {
            continue;
          }

          PointKey first = this.edges[seed].Start;
          PointKey current = first;
          var points = new List<CommonLinePoint>();
          var bulges = new List<double>();
          int traversed = 0;
          do
          {
            points.Add(this.quantizer.Point(current));
            int nextEdge = this.adjacency[current].FirstOrDefault(index => !used[index], -1);
            if (nextEdge < 0)
            {
              throw Invalid("CUT_OUTER reconstruction left an open contour.");
            }

            used[nextEdge] = true;
            BoundaryEdge segment = this.edges[nextEdge];
            bool forward = segment.Start.Equals(current);
            bulges.Add(forward ? segment.Bulge : -segment.Bulge);
            current = forward ? segment.End : segment.Start;
            traversed++;
            if (traversed > this.edges.Count)
            {
              throw Invalid("CUT_OUTER reconstruction did not terminate.");
            }
          }
          while (!current.Equals(first));

          IReadOnlyList<CommonLinePoint> flattened = Flatten(points, bulges, this.tolerance);
          if (points.Count < 3
            || flattened.Count < 3
            || Math.Abs(SignedArea(flattened)) <= this.tolerance * this.tolerance)
          {
            throw Invalid("CUT_OUTER reconstruction produced a degenerate contour.");
          }

          if (SelfIntersects(flattened, this.tolerance))
          {
            throw Invalid("CUT_OUTER reconstruction produced a self-intersecting contour.");
          }

          paths.Add(new CommonLinePath(points, isClosed: true, bulges: bulges));
        }

        return paths;
      }

      private static IReadOnlyList<CommonLinePoint> Flatten(
        IReadOnlyList<CommonLinePoint> points,
        IReadOnlyList<double> bulges,
        double tolerance)
      {
        var flattened = new List<CommonLinePoint>();
        for (int i = 0; i < points.Count; i++)
        {
          CommonLinePoint start = points[i];
          CommonLinePoint end = points[(i + 1) % points.Count];
          if (flattened.Count == 0)
          {
            flattened.Add(start);
          }

          flattened.AddRange(SampleBulge(start, end, bulges[i], tolerance));
        }

        if (flattened.Count > 1 && SamePoint(flattened[0], flattened[^1], tolerance))
        {
          flattened.RemoveAt(flattened.Count - 1);
        }

        return flattened;
      }

      /// <summary>Returns the intermediate arc samples followed by the exact segment endpoint.</summary>
      private static IEnumerable<CommonLinePoint> SampleBulge(
        CommonLinePoint start,
        CommonLinePoint end,
        double bulge,
        double tolerance)
      {
        if (Math.Abs(bulge) <= 1e-12)
        {
          yield return end;
          yield break;
        }

        double theta = 4D * Math.Atan(bulge);
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double chord = Math.Sqrt((dx * dx) + (dy * dy));
        double sinHalf = Math.Sin(theta / 2D);
        if (chord <= tolerance || Math.Abs(sinHalf) <= 1e-12)
        {
          throw Invalid("A curved CUT_OUTER segment is degenerate.");
        }

        double signedRadius = chord / (2D * sinHalf);
        double radius = Math.Abs(signedRadius);
        double middleX = (start.X + end.X) / 2D;
        double middleY = (start.Y + end.Y) / 2D;
        double apothem = signedRadius * Math.Cos(theta / 2D);
        double centerX = middleX + (apothem * (-dy / chord));
        double centerY = middleY + (apothem * (dx / chord));
        double firstAngle = Math.Atan2(start.Y - centerY, start.X - centerX);

        double sagitta = Math.Max(1e-9, tolerance);
        double cosine = Math.Max(-1D, Math.Min(1D, 1D - (sagitta / radius)));
        double step = 2D * Math.Acos(cosine);
        if (!double.IsFinite(step) || step <= 1e-12)
        {
          step = Math.PI / 180D;
        }

        int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(theta) / step));
        if (count > 16384)
        {
          throw Invalid("A curved CUT_OUTER segment requires too many validation samples.");
        }

        for (int i = 1; i <= count; i++)
        {
          if (i == count)
          {
            yield return end;
          }
          else
          {
            double angle = firstAngle + ((theta * i) / count);
            yield return new CommonLinePoint(
              centerX + (radius * Math.Cos(angle)),
              centerY + (radius * Math.Sin(angle)));
          }
        }
      }

      private static double SignedArea(IReadOnlyList<CommonLinePoint> points)
      {
        double area = 0D;
        for (int i = 0; i < points.Count; i++)
        {
          CommonLinePoint a = points[i];
          CommonLinePoint b = points[(i + 1) % points.Count];
          area += (a.X * b.Y) - (b.X * a.Y);
        }

        return area / 2D;
      }

      private static bool SelfIntersects(IReadOnlyList<CommonLinePoint> points, double tolerance)
      {
        for (int i = 0; i < points.Count; i++)
        {
          int iNext = (i + 1) % points.Count;
          for (int j = i + 1; j < points.Count; j++)
          {
            int jNext = (j + 1) % points.Count;
            if (i == j || iNext == j || jNext == i)
            {
              continue;
            }

            if (SegmentsConflict(points[i], points[iNext], points[j], points[jNext], tolerance))
            {
              return true;
            }
          }
        }

        return false;
      }

      private static bool SegmentsConflict(
        CommonLinePoint a,
        CommonLinePoint b,
        CommonLinePoint c,
        CommonLinePoint d,
        double tolerance)
      {
        var first = new CommonLineSegment(a, b);
        var second = new CommonLineSegment(c, d);
        if (TryIntersection(first, second, tolerance, out _))
        {
          return true;
        }

        return PointOnSegment(a, c, d, tolerance)
          || PointOnSegment(b, c, d, tolerance)
          || PointOnSegment(c, a, b, tolerance)
          || PointOnSegment(d, a, b, tolerance);
      }

      private static bool PointOnSegment(
        CommonLinePoint point,
        CommonLinePoint start,
        CommonLinePoint end,
        double tolerance)
      {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= tolerance
          || Math.Abs(Cross(point.X - start.X, point.Y - start.Y, dx / length, dy / length)) > tolerance)
        {
          return false;
        }

        double projection = Dot(point.X - start.X, point.Y - start.Y, dx / length, dy / length);
        return projection >= -tolerance && projection <= length + tolerance;
      }

      private void AddIncident(PointKey key, int edge)
      {
        if (!this.adjacency.TryGetValue(key, out List<int> incident))
        {
          incident = new List<int>();
          this.adjacency.Add(key, incident);
        }

        incident.Add(edge);
      }
    }

    private sealed class UnionFind
    {
      private readonly Dictionary<int, int> parents = new Dictionary<int, int>();

      public int Root(int item)
      {
        if (!this.parents.TryGetValue(item, out int parent))
        {
          this.parents[item] = item;
          return item;
        }

        if (parent != item)
        {
          this.parents[item] = this.Root(parent);
        }

        return this.parents[item];
      }

      public void Join(int a, int b)
      {
        int rootA = this.Root(a);
        int rootB = this.Root(b);
        if (rootA != rootB)
        {
          this.parents[rootB] = rootA;
        }
      }
    }
  }
}
