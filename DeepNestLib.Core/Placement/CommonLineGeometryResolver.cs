namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Concurrent;
  using System.Collections.Generic;
  using System.Drawing;
  using System.IO;
  using System.Linq;
  using System.Threading;
  using System.Security.Cryptography;
  using DeepNestLib.IO;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// Revalidates intentional common edges and turns their connected components into closed external
  /// contours plus a deterministic, deduplicated internal cut graph.
  /// </summary>
  public static class CommonLineGeometryResolver
  {
    private const int MaxValidationCurveSegments = 16384;
    private const int MaxValidationPathSegments = 131072;

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
    /// Creates a version-two parametric LINE relationship between normalized sub-spans of two nesting edges.
    /// The source DXF primitive and its fingerprint are recorded when they can be mapped uniquely.
    /// </summary>
    public static CommonEdge CreateCommonEdge(
      IPartPlacement partA,
      int edgeAIndex,
      double edgeAStartParameter,
      double edgeAEndParameter,
      IPartPlacement partB,
      int edgeBIndex,
      double edgeBStartParameter,
      double edgeBEndParameter,
      double commonLineGap,
      double geometryTolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      ValidateSettings(commonLineGap, geometryTolerance);
      CommonBoundarySpan spanA = CreateLineSpan(
        partA,
        edgeAIndex,
        edgeAStartParameter,
        edgeAEndParameter,
        geometryTolerance,
        sourceCatalogs);
      CommonBoundarySpan spanB = CreateLineSpan(
        partB,
        edgeBIndex,
        edgeBStartParameter,
        edgeBEndParameter,
        geometryTolerance,
        sourceCatalogs);
      return CreateCommonEdge(
        partA,
        edgeAIndex,
        spanA,
        partB,
        edgeBIndex,
        spanB,
        commonLineGap,
        geometryTolerance,
        sourceCatalogs);
    }

    public static bool TryCreateCommonEdge(
      IPartPlacement partA,
      int edgeAIndex,
      double edgeAStartParameter,
      double edgeAEndParameter,
      IPartPlacement partB,
      int edgeBIndex,
      double edgeBStartParameter,
      double edgeBEndParameter,
      double commonLineGap,
      double geometryTolerance,
      out CommonEdge commonEdge,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      try
      {
        commonEdge = CreateCommonEdge(
          partA,
          edgeAIndex,
          edgeAStartParameter,
          edgeAEndParameter,
          partB,
          edgeBIndex,
          edgeBStartParameter,
          edgeBEndParameter,
          commonLineGap,
          geometryTolerance,
          sourceCatalogs);
        return true;
      }
      catch (CommonLineValidationException)
      {
        commonEdge = null;
        return false;
      }
    }

    /// <summary>Creates an exact LINE or circular-ARC relationship from source-parametric spans.</summary>
    public static CommonEdge CreateCommonEdge(
      IPartPlacement partA,
      CommonBoundarySpan spanA,
      IPartPlacement partB,
      CommonBoundarySpan spanB,
      double commonLineGap,
      double geometryTolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
      => CreateCommonEdge(
        partA,
        -1,
        spanA,
        partB,
        -1,
        spanB,
        commonLineGap,
        geometryTolerance,
        sourceCatalogs);

    public static bool TryCreateCommonEdge(
      IPartPlacement partA,
      CommonBoundarySpan spanA,
      IPartPlacement partB,
      CommonBoundarySpan spanB,
      double commonLineGap,
      double geometryTolerance,
      out CommonEdge commonEdge,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      try
      {
        commonEdge = CreateCommonEdge(
          partA,
          spanA,
          partB,
          spanB,
          commonLineGap,
          geometryTolerance,
          sourceCatalogs);
        return true;
      }
      catch (CommonLineValidationException)
      {
        commonEdge = null;
        return false;
      }
    }

    private static CommonEdge CreateCommonEdge(
      IPartPlacement partA,
      int edgeAIndex,
      CommonBoundarySpan spanA,
      IPartPlacement partB,
      int edgeBIndex,
      CommonBoundarySpan spanB,
      double commonLineGap,
      double geometryTolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      ValidateSettings(commonLineGap, geometryTolerance);
      ResolvedEdge resolved = CalculateParametricEdge(
        partA,
        edgeAIndex,
        spanA,
        partB,
        edgeBIndex,
        spanB,
        commonLineGap,
        geometryTolerance,
        sourceCatalogs);
      return new CommonEdge(
        partA.Id,
        partB.Id,
        edgeAIndex,
        edgeBIndex,
        resolved.MidStart,
        resolved.MidEnd,
        commonLineGap,
        CommonEdge.ParametricSchemaVersion,
        resolved.CenterCurve.GeometryKind,
        spanA,
        spanB,
        resolved.CenterCurve);
    }

    /// <summary>
    /// Resolves all recorded relationships. No recovery or legacy duplicate-line merging is attempted:
    /// an invalid relationship throws so an unsafe nest cannot be exported ambiguously.
    /// </summary>
    public static CommonLineGeometryResult Resolve(ISheetPlacement placement)
      => Resolve(placement, null);

    /// <summary>
    /// Resolves against caller-owned immutable source catalogs when an export transaction already
    /// captured them. This prevents boundary reconstruction, record validation and emitted entities from
    /// observing different revisions of the same DXF.
    /// </summary>
    public static CommonLineGeometryResult Resolve(
      ISheetPlacement placement,
      IReadOnlyDictionary<string, DxfContourCatalog> sourceCatalogSnapshots,
      CancellationToken cancel = default)
    {
      cancel.ThrowIfCancellationRequested();
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
        cancel.ThrowIfCancellationRequested();
        if (placementsById.ContainsKey(part.Id))
        {
          throw Invalid($"Part placement id {part.Id} is not unique on the sheet.");
        }

        placementsById.Add(part.Id, part);
      }

      var consumedEdges = new Dictionary<PartEdgeKey, List<ParameterRange>>();
      var resolvedEdges = new List<ResolvedEdge>(records.Count);
      var union = new UnionFind();
      var sourceBoundaries = new Dictionary<string, SourceOuterDefinition>(StringComparer.OrdinalIgnoreCase);
      var sourceCatalogs = new Dictionary<string, DxfContourCatalog>(StringComparer.OrdinalIgnoreCase);
      if (sourceCatalogSnapshots != null)
      {
        foreach (KeyValuePair<string, DxfContourCatalog> snapshot in sourceCatalogSnapshots)
        {
          cancel.ThrowIfCancellationRequested();
          sourceCatalogs[Path.GetFullPath(snapshot.Key)] = snapshot.Value;
        }
      }

      foreach (CommonEdge record in records)
      {
        cancel.ThrowIfCancellationRequested();
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

        ResolvedEdge resolved;
        if (record.SchemaVersion == CommonEdge.LegacySchemaVersion)
        {
          resolved = CalculateEdge(
            partA,
            record.EdgeAIndex,
            partB,
            record.EdgeBIndex,
            placement.CommonLineGap,
            placement.GeometryTolerance);
        }
        else if (record.SchemaVersion == CommonEdge.ParametricSchemaVersion
          && record.SpanA != null
          && record.SpanB != null)
        {
          resolved = CalculateParametricEdge(
            partA,
            record.EdgeAIndex,
            record.SpanA,
            partB,
            record.EdgeBIndex,
            record.SpanB,
            placement.CommonLineGap,
            placement.GeometryTolerance,
            sourceCatalogs);
          ValidateRecordedCenterCurve(record, resolved.CenterCurve, placement.GeometryTolerance);
        }
        else
        {
          throw Invalid("A common-edge record uses an unsupported or incomplete geometry schema.");
        }

        AddConsumedSpan(
          consumedEdges,
          PartEdgeKey.For(record.PartAId, resolved.EdgeAIndex, record.SpanA),
          new ParameterRange(resolved.EdgeAStartParameter, resolved.EdgeAEndParameter),
          resolved.EdgeAParameterTolerance);
        AddConsumedSpan(
          consumedEdges,
          PartEdgeKey.For(record.PartBId, resolved.EdgeBIndex, record.SpanB),
          new ParameterRange(resolved.EdgeBStartParameter, resolved.EdgeBEndParameter),
          resolved.EdgeBParameterTolerance);

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
      foreach (IGrouping<int, ResolvedEdge> group in resolvedEdges.GroupBy(e => union.Root(e.Record.PartAId)))
      {
        cancel.ThrowIfCancellationRequested();
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
          sourceBoundaries,
          sourceCatalogs,
          cancel));
      }

      IReadOnlyDictionary<int, ValidationGeometry> validationGeometryByPart
        = placement.PartPlacements.ToDictionary(
          part => part.Id,
          part => new ValidationGeometry(
            ValidationRings(part, placement.GeometryTolerance, sourceCatalogs)));
      ValidateNoMaterialCollisions(
        placement.PartPlacements,
        validationGeometryByPart,
        placement.GeometryTolerance,
        cancel);
      ValidateCommonNetworkClear(
        clusters,
        placement.PartPlacements,
        validationGeometryByPart,
        placement.GeometryTolerance,
        cancel);

      return new CommonLineGeometryResult(clusters);
    }

    private static void ValidateNoMaterialCollisions(
      IReadOnlyList<IPartPlacement> placements,
      IReadOnlyDictionary<int, ValidationGeometry> geometryByPart,
      double tolerance,
      CancellationToken cancel)
    {
      for (int i = 0; i < placements.Count; i++)
      {
        cancel.ThrowIfCancellationRequested();
        for (int j = i + 1; j < placements.Count; j++)
        {
          cancel.ThrowIfCancellationRequested();
          IPartPlacement a = placements[i];
          IPartPlacement b = placements[j];
          ValidationGeometry geometryA = geometryByPart[a.Id];
          ValidationGeometry geometryB = geometryByPart[b.Id];
          if (!geometryA.Bounds.Overlaps(geometryB.Bounds, tolerance))
          {
            continue;
          }

          bool boundariesIntersect = false;
          for (int ringA = 0; ringA < geometryA.Rings.Count && !boundariesIntersect; ringA++)
          {
            cancel.ThrowIfCancellationRequested();
            for (int ringB = 0; ringB < geometryB.Rings.Count; ringB++)
            {
              cancel.ThrowIfCancellationRequested();
              if (geometryA.RingBounds[ringA].Overlaps(geometryB.RingBounds[ringB], tolerance)
                && RingsIntersect(
                  geometryA.Rings[ringA],
                  geometryB.Rings[ringB],
                  tolerance,
                  cancel))
              {
                boundariesIntersect = true;
                break;
              }
            }
          }

          if (boundariesIntersect)
          {
            throw Invalid($"Parts {a.Id} and {b.Id} collide in the final common-line placement.");
          }

          if (geometryA.Rings[0].Count > 0
            && IsPointInMaterial(geometryA.Rings[0][0], geometryB.Rings, tolerance))
          {
            throw Invalid($"Part {a.Id} overlaps material belonging to part {b.Id}.");
          }

          if (geometryB.Rings[0].Count > 0
            && IsPointInMaterial(geometryB.Rings[0][0], geometryA.Rings, tolerance))
          {
            throw Invalid($"Part {b.Id} overlaps material belonging to part {a.Id}.");
          }
        }
      }
    }

    private static void ValidateCommonNetworkClear(
      IReadOnlyList<CommonLineCluster> clusters,
      IReadOnlyList<IPartPlacement> placements,
      IReadOnlyDictionary<int, ValidationGeometry> geometryByPart,
      double tolerance,
      CancellationToken cancel)
    {
      foreach (CommonLineCluster cluster in clusters)
      {
        cancel.ThrowIfCancellationRequested();
        foreach (CommonCenterCurve common in cluster.CommonCurves)
        {
          cancel.ThrowIfCancellationRequested();
          foreach (CommonLineSegment segment in SampleCommonCurve(common, tolerance))
          {
            cancel.ThrowIfCancellationRequested();
            GeometryBounds segmentBounds = GeometryBounds.FromSegment(segment.Start, segment.End);
            var midpoint = new CommonLinePoint(
              (segment.Start.X + segment.End.X) / 2D,
              (segment.Start.Y + segment.End.Y) / 2D);
            foreach (IPartPlacement part in placements)
            {
              cancel.ThrowIfCancellationRequested();
              ValidationGeometry geometry = geometryByPart[part.Id];
              if (!segmentBounds.Overlaps(geometry.Bounds, tolerance))
              {
                continue;
              }

              bool intersectsBoundary = false;
              for (int ring = 0; ring < geometry.Rings.Count; ring++)
              {
                cancel.ThrowIfCancellationRequested();
                if (segmentBounds.Overlaps(geometry.RingBounds[ring], tolerance)
                  && SegmentIntersectsRing(
                    segment.Start,
                    segment.End,
                    geometry.Rings[ring],
                    tolerance,
                    cancel))
                {
                  intersectsBoundary = true;
                  break;
                }
              }

              if (intersectsBoundary
                || IsPointInMaterial(segment.Start, geometry.Rings, tolerance)
                || IsPointInMaterial(segment.End, geometry.Rings, tolerance)
                || IsPointInMaterial(midpoint, geometry.Rings, tolerance))
              {
                throw Invalid($"A COMMON_LINE segment crosses material belonging to part {part.Id}.");
              }
            }
          }
        }
      }
    }

    private static IReadOnlyList<IReadOnlyList<CommonLinePoint>> ValidationRings(
      IPartPlacement placement,
      double tolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs)
    {
      string sourceName = placement?.Part?.Name;
      if (string.IsNullOrWhiteSpace(sourceName)
        || !sourceName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
      {
        return PlacedRings(placement, tolerance);
      }

      string sourcePath;
      try
      {
        sourcePath = Path.GetFullPath(sourceName);
      }
      catch (Exception)
      {
        return PlacedRings(placement, tolerance);
      }

      if (!File.Exists(sourcePath))
      {
        // Placements made from SVGs and legacy/in-memory test polygons have no exact DXF source. Their
        // nesting polygon remains the only available validation boundary. Once a DXF does exist, any
        // catalog or sampling failure is safety-critical and must invalidate the common-line result.
        return PlacedRings(placement, tolerance);
      }

      DxfContourCatalog catalog = GetPlacementCatalog(placement, sourceCatalogs, tolerance);
      DxfSourceContour[] contours = catalog.Contours
        .Where(contour => contour.IsClosed)
        .ToArray();
      if (contours.Count(contour => !contour.IsChild) != 1
        || contours.Any(contour => contour.Primitives.Any(primitive =>
          primitive.GeometryKind != CommonLineGeometryKind.Line
          && primitive.GeometryKind != CommonLineGeometryKind.CircularArc)))
      {
        return PlacedRings(placement, tolerance);
      }

      int sampledSegments = 0;
      var rings = new List<IReadOnlyList<CommonLinePoint>>(contours.Length);
      foreach (DxfSourceContour contour in contours.OrderBy(contour => contour.IsChild ? 1 : 0))
      {
        rings.Add(SampleExactContour(
          placement,
          contour,
          tolerance,
          ref sampledSegments));
      }

      return rings;
    }

    private static IReadOnlyList<CommonLinePoint> SampleExactContour(
      IPartPlacement placement,
      DxfSourceContour contour,
      double tolerance,
      ref int sampledSegments)
    {
      var points = new List<CommonLinePoint>();
      foreach (DxfSourcePrimitive primitive in contour.Primitives)
      {
        int segmentCount = 1;
        if (primitive.GeometryKind == CommonLineGeometryKind.CircularArc)
        {
          double maximumStep = 2D * Math.Acos(Math.Max(
            -1D,
            Math.Min(1D, 1D - (tolerance / (4D * primitive.Radius)))));
          if (!double.IsFinite(maximumStep) || maximumStep <= 0D)
          {
            maximumStep = Math.PI / 360D;
          }

          double requiredSegments = Math.Ceiling(
            Math.Abs(primitive.SweepRadians) / maximumStep);
          if (!double.IsFinite(requiredSegments)
            || requiredSegments > MaxValidationCurveSegments)
          {
            throw Invalid("A curved source contour requires too many validation samples.");
          }

          segmentCount = Math.Max(1, (int)requiredSegments);
        }

        if (segmentCount > MaxValidationPathSegments - sampledSegments)
        {
          throw Invalid("A curved source contour exceeds the aggregate validation sample budget.");
        }

        sampledSegments += segmentCount;

        if (points.Count == 0)
        {
          points.Add(TransformSourcePoint(placement, primitive.Start.X, primitive.Start.Y));
        }

        for (int i = 1; i <= segmentCount; i++)
        {
          double parameter = (double)i / segmentCount;
          CommonLinePoint local = primitive.GeometryKind == CommonLineGeometryKind.Line
            ? Interpolate(primitive.Start, primitive.End, parameter)
            : new CommonLinePoint(
              primitive.Center.X + (primitive.Radius * Math.Cos(
                primitive.StartAngleRadians + (primitive.SweepRadians * parameter))),
              primitive.Center.Y + (primitive.Radius * Math.Sin(
                primitive.StartAngleRadians + (primitive.SweepRadians * parameter))));
          points.Add(TransformSourcePoint(placement, local.X, local.Y));
        }
      }

      if (points.Count > 1 && SamePoint(points[0], points[^1], tolerance))
      {
        points.RemoveAt(points.Count - 1);
      }

      return points;
    }

    private static IEnumerable<CommonLineSegment> SampleCommonCurve(
      CommonCenterCurve curve,
      double tolerance)
    {
      if (curve.GeometryKind == CommonLineGeometryKind.Line)
      {
        yield return new CommonLineSegment(curve.Start, curve.End);
        yield break;
      }

      if (curve.GeometryKind != CommonLineGeometryKind.CircularArc
        || curve.Center == null
        || curve.Radius <= tolerance
        || Math.Abs(curve.SweepRadians) * curve.Radius <= tolerance)
      {
        throw Invalid("COMMON_LINE contains an invalid or unsupported exact curve.");
      }

      double maximumStep = 2D * Math.Acos(Math.Max(-1D, Math.Min(1D, 1D - (tolerance / (2D * curve.Radius)))));
      if (!double.IsFinite(maximumStep) || maximumStep <= 0D)
      {
        maximumStep = Math.PI / 180D;
      }

      double requiredSegments = Math.Ceiling(Math.Abs(curve.SweepRadians) / maximumStep);
      if (!double.IsFinite(requiredSegments)
        || requiredSegments > MaxValidationCurveSegments)
      {
        throw Invalid("A curved COMMON_LINE requires too many validation samples.");
      }

      int count = Math.Max(1, (int)requiredSegments);
      CommonLinePoint prior = CircularPoint(curve, 0D);
      for (int i = 1; i <= count; i++)
      {
        CommonLinePoint next = CircularPoint(curve, (double)i / count);
        yield return new CommonLineSegment(prior, next);
        prior = next;
      }
    }

    private static CommonLinePoint CircularPoint(CommonCenterCurve curve, double parameter)
    {
      double angle = curve.StartAngleRadians + (curve.SweepRadians * parameter);
      return new CommonLinePoint(
        curve.Center.X + (curve.Radius * Math.Cos(angle)),
        curve.Center.Y + (curve.Radius * Math.Sin(angle)));
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
      double tolerance,
      CancellationToken cancel = default)
    {
      for (int i = 0; i < a.Count; i++)
      {
        cancel.ThrowIfCancellationRequested();
        CommonLinePoint a0 = a[i];
        CommonLinePoint a1 = a[(i + 1) % a.Count];
        if (SegmentIntersectsRing(a0, a1, b, tolerance, cancel))
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
      double tolerance,
      CancellationToken cancel = default)
    {
      double minX = Math.Min(start.X, end.X) - tolerance;
      double maxX = Math.Max(start.X, end.X) + tolerance;
      double minY = Math.Min(start.Y, end.Y) - tolerance;
      double maxY = Math.Max(start.Y, end.Y) + tolerance;
      for (int i = 0; i < ring.Count; i++)
      {
        if ((i & 255) == 0)
        {
          cancel.ThrowIfCancellationRequested();
        }

        CommonLinePoint a = ring[i];
        CommonLinePoint b = ring[(i + 1) % ring.Count];
        if (Math.Max(a.X, b.X) < minX || Math.Min(a.X, b.X) > maxX
          || Math.Max(a.Y, b.Y) < minY || Math.Min(a.Y, b.Y) > maxY)
        {
          continue;
        }

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
      IReadOnlyDictionary<PartEdgeKey, List<ParameterRange>> removed,
      BoundaryGraph boundary,
      double tolerance,
      IDictionary<string, SourceOuterDefinition> sourceBoundaries,
      IDictionary<string, DxfContourCatalog> sourceCatalogs)
    {
      string sourceName = placement?.Part?.Name;
      string sourcePath = null;
      if (!string.IsNullOrWhiteSpace(sourceName))
      {
        try
        {
          sourcePath = Path.GetFullPath(sourceName);
        }
        catch
        {
          // The normal non-DXF fallback below owns invalid/unavailable source names.
        }
      }

      if (removed.Keys.Any(key => key.PartId == partId && key.IsSource)
        || (sourcePath != null && sourceCatalogs.ContainsKey(sourcePath)))
      {
        AddCatalogBoundary(
          placement,
          partId,
          removed,
          boundary,
          tolerance,
          sourceCatalogs);
        return true;
      }

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
        .Select(point => TransformSourcePointForMapping(placement, point.X, point.Y))
        .ToArray();
      IReadOnlyList<CommonLinePoint> placedPart = PlacedRing(
        placement.Part,
        0D,
        0D,
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
          TransformSourcePointForMapping(placement, start.X, start.Y),
          TransformSourcePointForMapping(placement, end.X, end.Y),
          placement.IsMirrored ? -start.Bulge : start.Bulge,
          SourceMappingTolerance(
            placement,
            new CommonLinePoint(start.X, start.Y),
            tolerance),
          SourceMappingTolerance(
            placement,
            new CommonLinePoint(end.X, end.Y),
            tolerance)));
      }

      var removedSourceSegments = new Dictionary<int, List<ParameterRange>>();
      foreach (KeyValuePair<PartEdgeKey, List<ParameterRange>> removal
        in removed.Where(pair => pair.Key.PartId == partId))
      {
        PartEdgeKey key = removal.Key;
        CommonLinePoint edgeStart = NfpPoint(placement, key.EdgeIndex);
        int ringCount = RingCount(placement.Part.Points, tolerance);
        CommonLinePoint edgeEnd = NfpPoint(placement, (key.EdgeIndex + 1) % ringCount);
        var matches = sourceSegments
          .Select((segment, index) => (segment, index))
          .Where(candidate => Math.Abs(candidate.segment.Bulge) <= 1e-12)
          .Select(candidate =>
          {
            bool matched = TryMatchSourceSegment(
              edgeStart,
              edgeEnd,
              candidate.segment.MappingStart,
              candidate.segment.MappingEnd,
              candidate.segment.StartMappingTolerance,
              candidate.segment.EndMappingTolerance,
              out bool sameDirection);
            return (candidate.index, matched, sameDirection);
          })
          .Where(candidate => candidate.matched)
          .ToArray();
        if (matches.Length != 1)
        {
          throw Invalid(
            $"Part {partId}'s indexed common edge cannot be mapped uniquely to a straight source LWPOLYLINE segment.");
        }

        int sourceIndex = matches[0].index;
        bool sameDirection = matches[0].sameDirection;
        if (!removedSourceSegments.TryGetValue(sourceIndex, out List<ParameterRange> sourceRanges))
        {
          removedSourceSegments[sourceIndex] = sourceRanges = new List<ParameterRange>();
        }

        foreach (ParameterRange range in removal.Value)
        {
          sourceRanges.Add(sameDirection
            ? range
            : new ParameterRange(1D - range.End, 1D - range.Start));
        }
      }

      for (int i = 0; i < sourceSegments.Count; i++)
      {
        SourceBoundarySegment segment = sourceSegments[i];
        if (!removedSourceSegments.TryGetValue(i, out List<ParameterRange> sourceRanges))
        {
          boundary.Add(segment.Start, segment.End, segment.Bulge);
          continue;
        }

        if (Math.Abs(segment.Bulge) > 1e-12)
        {
          throw Invalid(
            $"Part {partId}'s indexed straight common edge mapped to a curved source segment.");
        }

        double length = Distance(segment.Start, segment.End);
        foreach (ParameterRange retained in ComplementRanges(
          sourceRanges,
          tolerance / Math.Max(length, tolerance)))
        {
          boundary.Add(
            Interpolate(segment.Start, segment.End, retained.Start),
            Interpolate(segment.Start, segment.End, retained.End));
        }
      }

      return true;
    }

    private static void AddCatalogBoundary(
      IPartPlacement placement,
      int partId,
      IReadOnlyDictionary<PartEdgeKey, List<ParameterRange>> removed,
      BoundaryGraph boundary,
      double tolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs)
    {
      DxfContourCatalog catalog = GetPlacementCatalog(placement, sourceCatalogs, tolerance);
      DxfSourceContour[] outsideContours = catalog.Contours
        .Where(contour => !contour.IsChild)
        .ToArray();
      if (outsideContours.Length != 1 || !outsideContours[0].IsClosed)
      {
        throw Invalid($"Part {partId}'s exact common-line source must have one closed outside contour.");
      }

      DxfSourceContour outside = outsideContours[0];
      if (outside.Primitives.Any(primitive =>
        primitive.GeometryKind != CommonLineGeometryKind.Line
        && primitive.GeometryKind != CommonLineGeometryKind.CircularArc))
      {
        throw Invalid(
          $"Part {partId}'s cluster boundary contains a curve that cannot be represented exactly.");
      }

      var removedByPrimitive = new Dictionary<int, List<ParameterRange>>();
      foreach (KeyValuePair<PartEdgeKey, List<ParameterRange>> removal
        in removed.Where(pair => pair.Key.PartId == partId))
      {
        PartEdgeKey key = removal.Key;
        int primitiveIndex;
        bool sameDirection = true;
        if (key.IsSource)
        {
          if (key.ContourIndex != outside.Index
            || key.PrimitiveIndex < 0
            || key.PrimitiveIndex >= outside.Primitives.Count)
          {
            throw Invalid($"Part {partId}'s common span no longer belongs to its outside contour.");
          }

          primitiveIndex = key.PrimitiveIndex;
        }
        else
        {
          CommonLinePoint edgeStart = NfpPoint(placement, key.EdgeIndex);
          int ringCount = RingCount(placement.Part.Points, tolerance);
          CommonLinePoint edgeEnd = NfpPoint(placement, (key.EdgeIndex + 1) % ringCount);
          var matches = new List<(int Index, DxfResolvedBoundarySpan Span, bool SameDirection)>();
          for (int i = 0; i < outside.Primitives.Count; i++)
          {
            DxfSourcePrimitive primitive = outside.Primitives[i];
            if (primitive.GeometryKind != CommonLineGeometryKind.Line)
            {
              continue;
            }

            CommonBoundarySpan fullSpan = DxfCommonLineSpanResolver.CreateSpan(
              catalog,
              outside.Index,
              i);
            DxfResolvedBoundarySpan world = DxfCommonLineSpanResolver.Resolve(
              catalog,
              fullSpan,
              placement);
            if (TryMatchSourceSegment(
              edgeStart,
              edgeEnd,
              TransformSourcePointForMapping(
                placement,
                primitive.Start.X,
                primitive.Start.Y),
              TransformSourcePointForMapping(
                placement,
                primitive.End.X,
                primitive.End.Y),
              SourceMappingTolerance(placement, primitive.Start, tolerance),
              SourceMappingTolerance(placement, primitive.End, tolerance),
              out bool candidateSameDirection))
            {
              matches.Add((i, world, candidateSameDirection));
            }
          }

          if (matches.Count != 1)
          {
            throw Invalid(
              $"Part {partId}'s indexed common edge cannot be mapped uniquely to its exact source contour.");
          }

          primitiveIndex = matches[0].Index;
          sameDirection = matches[0].SameDirection;
        }

        if (!removedByPrimitive.TryGetValue(primitiveIndex, out List<ParameterRange> ranges))
        {
          removedByPrimitive[primitiveIndex] = ranges = new List<ParameterRange>();
        }

        foreach (ParameterRange range in removal.Value)
        {
          ranges.Add(sameDirection
            ? range
            : new ParameterRange(1D - range.End, 1D - range.Start));
        }
      }

      for (int i = 0; i < outside.Primitives.Count; i++)
      {
        DxfSourcePrimitive primitive = outside.Primitives[i];
        double primitiveLength = primitive.GeometryKind == CommonLineGeometryKind.Line
          ? Distance(primitive.Start, primitive.End)
          : Math.Abs(primitive.SweepRadians) * primitive.Radius;
        IReadOnlyList<ParameterRange> retained = removedByPrimitive.TryGetValue(i, out List<ParameterRange> ranges)
          ? ComplementRanges(ranges, tolerance / Math.Max(primitiveLength, tolerance))
          : new[] { new ParameterRange(0D, 1D) };
        foreach (ParameterRange range in retained)
        {
          CommonBoundarySpan sourceSpan = DxfCommonLineSpanResolver.CreateSpan(
            catalog,
            outside.Index,
            i,
            range.Start,
            range.End);
          DxfResolvedBoundarySpan world = DxfCommonLineSpanResolver.Resolve(
            catalog,
            sourceSpan,
            placement);
          double bulge = world.GeometryKind == CommonLineGeometryKind.CircularArc
            ? Math.Tan(world.SweepRadians / 4D)
            : 0D;
          if (!double.IsFinite(bulge))
          {
            throw Invalid($"Part {partId}'s retained circular boundary cannot be emitted as a DXF bulge.");
          }

          boundary.Add(world.Start, world.End, bulge);
        }
      }
    }

    private static DxfContourCatalog GetPlacementCatalog(
      IPartPlacement placement,
      IDictionary<string, DxfContourCatalog> sourceCatalogs,
      double endpointTolerance)
    {
      string sourceName = placement?.Part?.Name;
      if (string.IsNullOrWhiteSpace(sourceName))
      {
        throw Invalid("An exact common-line cluster lost its source drawing name.");
      }

      string path;
      try
      {
        path = Path.GetFullPath(sourceName);
      }
      catch (Exception ex)
      {
        throw Invalid($"An exact common-line source path is invalid: {ex.Message}");
      }

      if (sourceCatalogs.TryGetValue(path, out DxfContourCatalog catalog))
      {
        return catalog;
      }

      catalog = LoadPlacementCatalog(placement, endpointTolerance);
      sourceCatalogs[path] = catalog;
      return catalog;
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
        // Use the same content-addressed, immutable-snapshot pattern as DxfContourCatalog. File
        // length and mtime are only hints and are routinely preserved by CAD save/copy workflows.
        byte[] sourceBytes = File.ReadAllBytes(path);
        string contentFingerprint = Convert.ToHexString(SHA256.HashData(sourceBytes));
        var cacheKey = new SourceCacheKey(path, contentFingerprint);
        attemptedCacheKey = cacheKey;
        loaded = SourceOuterCache.GetOrAdd(
          cacheKey,
          key => new Lazy<SourceOuterDefinition>(
            () => LoadSourceOuter(key.Path, sourceBytes),
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

    private static DxfContourCatalog LoadPlacementCatalog(
      IPartPlacement placement,
      double endpointTolerance)
    {
      string sourceName = placement?.Part?.Name;
      if (string.IsNullOrWhiteSpace(sourceName)
        || !sourceName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
      {
        throw Invalid("An exact common boundary span requires its source DXF.");
      }

      try
      {
        string path = Path.GetFullPath(sourceName);
        if (!File.Exists(path))
        {
          throw Invalid("An exact common boundary span source DXF is no longer available.");
        }

        return DxfContourCatalog.Load(path, endpointTolerance);
      }
      catch (CommonLineValidationException)
      {
        throw;
      }
      catch (Exception ex)
      {
        throw Invalid($"The exact common boundary source could not be loaded safely: {ex.Message}");
      }
    }

    private static SourceOuterDefinition LoadSourceOuter(string path, byte[] sourceBytes)
    {
      using var stream = new MemoryStream(sourceBytes, writable: false);
      DxfFile file = DxfFile.Load(stream);
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
        && Math.Abs(polyline.Elevation) <= 1e-12
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
      CommonLinePoint mapped = TransformSourcePointForMapping(placement, x, y);
      return new CommonLinePoint(mapped.X + placement.X, mapped.Y + placement.Y);
    }

    private static CommonLinePoint TransformSourcePointForMapping(
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
        (x * cos) - (y * sin),
        (x * sin) + (y * cos));
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

    private static void AddRemoval(
      IDictionary<PartEdgeKey, List<ParameterRange>> removed,
      PartEdgeKey key,
      ParameterRange span)
    {
      if (!removed.TryGetValue(key, out List<ParameterRange> spans))
      {
        removed[key] = spans = new List<ParameterRange>();
      }

      spans.Add(span);
    }

    private static IReadOnlyList<ParameterRange> ComplementRanges(
      IEnumerable<ParameterRange> removed,
      double parameterTolerance)
    {
      ParameterRange[] ordered = (removed ?? Array.Empty<ParameterRange>())
        .Select(range => new ParameterRange(
          Math.Max(0D, range.Start),
          Math.Min(1D, range.End)))
        .Where(range => range.End > range.Start)
        .OrderBy(range => range.Start)
        .ThenBy(range => range.End)
        .ToArray();
      var retained = new List<ParameterRange>();
      double cursor = 0D;
      foreach (ParameterRange range in ordered)
      {
        if (range.Start > cursor + parameterTolerance)
        {
          retained.Add(new ParameterRange(cursor, range.Start));
        }

        cursor = Math.Max(cursor, range.End);
      }

      if (cursor < 1D - parameterTolerance)
      {
        retained.Add(new ParameterRange(cursor, 1D));
      }

      return retained;
    }

    private static CommonLineCluster BuildCluster(
      IReadOnlyList<int> memberIds,
      IReadOnlyList<ResolvedEdge> commonEdges,
      IReadOnlyDictionary<int, IPartPlacement> placementsById,
      double gap,
      double tolerance,
      IDictionary<string, SourceOuterDefinition> sourceBoundaries,
      IDictionary<string, DxfContourCatalog> sourceCatalogs,
      CancellationToken cancel)
    {
      var quantizer = new PointQuantizer(tolerance);
      var removed = new Dictionary<PartEdgeKey, List<ParameterRange>>();
      foreach (ResolvedEdge edge in commonEdges)
      {
        cancel.ThrowIfCancellationRequested();
        AddRemoval(
          removed,
          PartEdgeKey.For(edge.Record.PartAId, edge.EdgeAIndex, edge.Record.SpanA),
          new ParameterRange(edge.EdgeAStartParameter, edge.EdgeAEndParameter));
        AddRemoval(
          removed,
          PartEdgeKey.For(edge.Record.PartBId, edge.EdgeBIndex, edge.Record.SpanB),
          new ParameterRange(edge.EdgeBStartParameter, edge.EdgeBEndParameter));
      }

      var boundary = new BoundaryGraph(quantizer, tolerance, cancel);
      foreach (int partId in memberIds)
      {
        cancel.ThrowIfCancellationRequested();
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
          sourceBoundaries,
          sourceCatalogs))
        {
          continue;
        }

        bool[] curvedSegments = (part.Part as NoFitPolygon)?.GetCurvedSegments();
        if (curvedSegments != null
          && (curvedSegments.Length < count || curvedSegments.Take(count).Any(isCurved => isCurved)))
        {
          throw Invalid(
            $"Part {partId} has curved outside geometry in the legacy fallback; "
            + "an exact source-parametric boundary is required to rebuild it without distortion.");
        }

        for (int edgeIndex = 0; edgeIndex < count; edgeIndex++)
        {
          CommonLinePoint start = PlacedPoint(part, edgeIndex);
          CommonLinePoint end = PlacedPoint(part, (edgeIndex + 1) % count);
          if (!removed.TryGetValue(
            new PartEdgeKey(partId, edgeIndex),
            out List<ParameterRange> removedRanges))
          {
            boundary.Add(start, end);
            continue;
          }

          double length = Distance(start, end);
          foreach (ParameterRange retained in ComplementRanges(
            removedRanges,
            tolerance / Math.Max(length, tolerance)))
          {
            boundary.Add(
              Interpolate(start, end, retained.Start),
              Interpolate(start, end, retained.End));
          }
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
        cancel.ThrowIfCancellationRequested();
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
        cancel.ThrowIfCancellationRequested();
        boundary.Add(connector.Start, connector.End);
      }

      foreach (DeferredCorner corner in deferredCorners.Values)
      {
        cancel.ThrowIfCancellationRequested();
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

      IReadOnlyList<CommonLinePath> closedPaths = boundary.BuildClosedPaths();
      ClassifyClusterBoundaries(
        closedPaths,
        tolerance,
        out IReadOnlyList<CommonLinePath> outerPaths,
        out IReadOnlyList<CommonLinePath> innerPaths);

      IReadOnlyList<CommonCenterCurve> commonCurves = CommonLineCurveNetwork.Build(
        commonEdges.Select(e => e.CenterCurve ?? new CommonCenterCurve(
          CommonLineGeometryKind.Line,
          e.MidStart,
          e.MidEnd)),
        gap,
        tolerance,
        cancel);
      IReadOnlyList<CommonLineSegment> network = commonCurves
        .Where(curve => curve.GeometryKind == CommonLineGeometryKind.Line)
        .Select(curve => new CommonLineSegment(curve.Start, curve.End))
        .ToArray();

      if (commonCurves.Count == 0)
      {
        throw Invalid("A common-line cluster has no internal common cut.");
      }

      ValidateCommonCurvesAgainstOuter(
        commonCurves,
        outerPaths.Concat(innerPaths).ToArray(),
        tolerance);

      return new CommonLineCluster(memberIds, outerPaths, innerPaths, network, commonCurves);
    }

    /// <summary>
    /// Classifies the disjoint cycles of the retained cluster graph by containment. A connected common-
    /// line cluster must have one material envelope; cycles directly inside it are voids and therefore
    /// CUT_INNER. Touching/intersecting cycles, multiple external islands and nested material islands are
    /// rejected because their cutting semantics would otherwise be ambiguous.
    /// </summary>
    internal static void ClassifyClusterBoundaries(
      IReadOnlyList<CommonLinePath> closedPaths,
      double tolerance,
      out IReadOnlyList<CommonLinePath> outerPaths,
      out IReadOnlyList<CommonLinePath> innerPaths)
    {
      if (closedPaths == null)
      {
        throw new ArgumentNullException(nameof(closedPaths));
      }

      if (!double.IsFinite(tolerance) || tolerance <= 0D)
      {
        throw Invalid("GeometryTolerance must be a finite positive distance.");
      }

      if (closedPaths.Count == 0)
      {
        throw Invalid("A common-line cluster has no closed external boundary.");
      }

      IReadOnlyList<CommonLinePoint>[] rings = closedPaths
        .Select(path =>
        {
          if (path == null
            || !path.IsClosed
            || path.Points.Count < 3
            || path.Bulges.Count != path.Points.Count)
          {
            throw Invalid("A common-line cluster contains an invalid closed boundary.");
          }

          return BoundaryGraph.Flatten(path.Points, path.Bulges, tolerance);
        })
        .ToArray();

      for (int i = 0; i < rings.Length; i++)
      {
        for (int j = i + 1; j < rings.Length; j++)
        {
          if (RingsIntersect(rings[i], rings[j], tolerance))
          {
            throw Invalid("Common-line cluster boundary cycles intersect or touch ambiguously.");
          }
        }
      }

      var outer = new List<CommonLinePath>();
      var inner = new List<CommonLinePath>();
      for (int i = 0; i < rings.Length; i++)
      {
        int depth = 0;
        CommonLinePoint probe = rings[i][0];
        for (int j = 0; j < rings.Length; j++)
        {
          if (i != j && PointInRing(probe, rings[j], tolerance))
          {
            depth++;
          }
        }

        if (depth == 0)
        {
          outer.Add(closedPaths[i]);
        }
        else if (depth == 1)
        {
          inner.Add(closedPaths[i]);
        }
        else
        {
          throw Invalid(
            "A common-line cluster contains a nested material island that V1 cannot cut unambiguously.");
        }
      }

      if (outer.Count != 1)
      {
        throw Invalid("A connected common-line cluster must have exactly one closed external boundary.");
      }

      outerPaths = outer;
      innerPaths = inner;
    }

    /// <summary>
    /// Fails closed when an internal common cut overlaps or crosses an external cluster boundary.
    /// A geometric contact is safe only when it is an endpoint of both finite primitives.
    /// </summary>
    internal static void ValidateCommonCurvesAgainstOuter(
      IReadOnlyList<CommonCenterCurve> commonCurves,
      IReadOnlyList<CommonLinePath> outerPaths,
      double tolerance)
    {
      if (commonCurves == null)
      {
        throw new ArgumentNullException(nameof(commonCurves));
      }

      if (outerPaths == null)
      {
        throw new ArgumentNullException(nameof(outerPaths));
      }

      if (!double.IsFinite(tolerance) || tolerance <= 0D)
      {
        throw Invalid("GeometryTolerance must be a finite positive distance.");
      }

      foreach (CommonCenterCurve common in commonCurves)
      {
        ValidateGuardCurve(common, tolerance, "COMMON_LINE");
        foreach (CommonLinePath outer in outerPaths)
        {
          if (outer == null
            || !outer.IsClosed
            || outer.Points.Count < 2
            || outer.Bulges.Count != outer.Points.Count)
          {
            throw Invalid("CUT_OUTER contains an invalid analytic boundary.");
          }

          for (int i = 0; i < outer.Points.Count; i++)
          {
            CommonCenterCurve boundary = CreateOuterCurve(
              outer.Points[i],
              outer.Points[(i + 1) % outer.Points.Count],
              outer.Bulges[i],
              tolerance);
            ValidateCommonOuterPair(common, boundary, tolerance);
          }
        }
      }
    }

    private static CommonCenterCurve CreateOuterCurve(
      CommonLinePoint start,
      CommonLinePoint end,
      double bulge,
      double tolerance)
    {
      if (Math.Abs(bulge) <= 1e-12)
      {
        var line = new CommonCenterCurve(CommonLineGeometryKind.Line, start, end);
        ValidateGuardCurve(line, tolerance, "CUT_OUTER");
        return line;
      }

      double theta = 4D * Math.Atan(bulge);
      double dx = end.X - start.X;
      double dy = end.Y - start.Y;
      double chord = Math.Sqrt((dx * dx) + (dy * dy));
      double sinHalf = Math.Sin(theta / 2D);
      if (!double.IsFinite(theta)
        || chord <= tolerance
        || Math.Abs(sinHalf) <= 1e-12)
      {
        throw Invalid("CUT_OUTER contains a degenerate circular ARC.");
      }

      double signedRadius = chord / (2D * sinHalf);
      double radius = Math.Abs(signedRadius);
      double apothem = signedRadius * Math.Cos(theta / 2D);
      double centerX = ((start.X + end.X) / 2D) + (apothem * (-dy / chord));
      double centerY = ((start.Y + end.Y) / 2D) + (apothem * (dx / chord));
      var center = new CommonLinePoint(centerX, centerY);
      var arc = new CommonCenterCurve(
        CommonLineGeometryKind.CircularArc,
        start,
        end,
        center,
        radius,
        Math.Atan2(start.Y - centerY, start.X - centerX),
        theta);
      ValidateGuardCurve(arc, tolerance, "CUT_OUTER");
      return arc;
    }

    private static void ValidateGuardCurve(
      CommonCenterCurve curve,
      double tolerance,
      string layer)
    {
      if (curve == null
        || curve.Start == null
        || curve.End == null
        || !double.IsFinite(curve.Start.X)
        || !double.IsFinite(curve.Start.Y)
        || !double.IsFinite(curve.End.X)
        || !double.IsFinite(curve.End.Y))
      {
        throw Invalid($"{layer} contains an invalid analytic primitive.");
      }

      if (curve.GeometryKind == CommonLineGeometryKind.Line)
      {
        if (Distance(curve.Start, curve.End) <= tolerance)
        {
          throw Invalid($"{layer} contains a zero-length or tiny LINE.");
        }

        return;
      }

      if (curve.GeometryKind != CommonLineGeometryKind.CircularArc
        || curve.Center == null
        || !double.IsFinite(curve.Center.X)
        || !double.IsFinite(curve.Center.Y)
        || !double.IsFinite(curve.Radius)
        || !double.IsFinite(curve.StartAngleRadians)
        || !double.IsFinite(curve.SweepRadians)
        || curve.Radius <= tolerance
        || Math.Abs(curve.SweepRadians) * curve.Radius <= tolerance
        || Math.Abs(curve.SweepRadians) > (2D * Math.PI) + (tolerance / curve.Radius))
      {
        throw Invalid($"{layer} contains an invalid or unsupported exact curve.");
      }
    }

    private static void ValidateCommonOuterPair(
      CommonCenterCurve common,
      CommonCenterCurve outer,
      double tolerance)
    {
      if (common.GeometryKind == CommonLineGeometryKind.Line
        && outer.GeometryKind == CommonLineGeometryKind.Line)
      {
        ValidateLineLineContact(common, outer, tolerance);
        return;
      }

      if (common.GeometryKind == CommonLineGeometryKind.Line)
      {
        ValidateLineArcContact(common, outer, tolerance);
        return;
      }

      if (outer.GeometryKind == CommonLineGeometryKind.Line)
      {
        ValidateLineArcContact(outer, common, tolerance);
        return;
      }

      ValidateArcArcContact(common, outer, tolerance);
    }

    private static void ValidateLineLineContact(
      CommonCenterCurve first,
      CommonCenterCurve second,
      double tolerance)
    {
      double firstDx = first.End.X - first.Start.X;
      double firstDy = first.End.Y - first.Start.Y;
      double secondDx = second.End.X - second.Start.X;
      double secondDy = second.End.Y - second.Start.Y;
      double firstLength = Math.Sqrt((firstDx * firstDx) + (firstDy * firstDy));
      double secondLength = Math.Sqrt((secondDx * secondDx) + (secondDy * secondDy));
      double firstUnitX = firstDx / firstLength;
      double firstUnitY = firstDy / firstLength;
      double secondUnitX = secondDx / secondLength;
      double secondUnitY = secondDy / secondLength;
      double angularTolerance = tolerance / Math.Max(Math.Min(firstLength, secondLength), tolerance);
      bool parallel = Math.Abs(Cross(firstUnitX, firstUnitY, secondUnitX, secondUnitY))
        <= angularTolerance;
      bool collinear = parallel
        && Math.Abs(Cross(
          second.Start.X - first.Start.X,
          second.Start.Y - first.Start.Y,
          firstUnitX,
          firstUnitY)) <= tolerance
        && Math.Abs(Cross(
          second.End.X - first.Start.X,
          second.End.Y - first.Start.Y,
          firstUnitX,
          firstUnitY)) <= tolerance;

      if (collinear)
      {
        double secondStart = Dot(
          second.Start.X - first.Start.X,
          second.Start.Y - first.Start.Y,
          firstUnitX,
          firstUnitY);
        double secondEnd = Dot(
          second.End.X - first.Start.X,
          second.End.Y - first.Start.Y,
          firstUnitX,
          firstUnitY);
        double overlap = Math.Min(firstLength, Math.Max(secondStart, secondEnd))
          - Math.Max(0D, Math.Min(secondStart, secondEnd));
        if (overlap > tolerance)
        {
          ThrowCommonOuterConflict();
        }

        if (overlap >= -tolerance && !HaveSharedEndpoint(first, second, tolerance))
        {
          ThrowCommonOuterConflict();
        }

        return;
      }

      if (TryIntersection(
        new CommonLineSegment(first.Start, first.End),
        new CommonLineSegment(second.Start, second.End),
        tolerance,
        out CommonLinePoint intersection))
      {
        RequireSharedEndpoint(first, second, intersection, tolerance);
      }

      ValidateEndpointOnLine(first.Start, first, second, tolerance);
      ValidateEndpointOnLine(first.End, first, second, tolerance);
      ValidateEndpointOnLine(second.Start, second, first, tolerance);
      ValidateEndpointOnLine(second.End, second, first, tolerance);
    }

    private static void ValidateEndpointOnLine(
      CommonLinePoint point,
      CommonCenterCurve owner,
      CommonCenterCurve line,
      double tolerance)
    {
      if (PointOnSegment(point, line.Start, line.End, tolerance))
      {
        RequireSharedEndpoint(owner, line, point, tolerance);
      }
    }

    private static void ValidateLineArcContact(
      CommonCenterCurve line,
      CommonCenterCurve arc,
      double tolerance)
    {
      foreach (CommonLinePoint intersection in LineArcIntersections(line, arc, tolerance))
      {
        RequireSharedEndpoint(line, arc, intersection, tolerance);
      }

      ValidateEndpointOnArc(line.Start, line, arc, tolerance);
      ValidateEndpointOnArc(line.End, line, arc, tolerance);
      ValidateEndpointOnLine(arc.Start, arc, line, tolerance);
      ValidateEndpointOnLine(arc.End, arc, line, tolerance);
    }

    private static IReadOnlyList<CommonLinePoint> LineArcIntersections(
      CommonCenterCurve line,
      CommonCenterCurve arc,
      double tolerance)
    {
      double dx = line.End.X - line.Start.X;
      double dy = line.End.Y - line.Start.Y;
      double lengthSquared = (dx * dx) + (dy * dy);
      double length = Math.Sqrt(lengthSquared);
      double fx = line.Start.X - arc.Center.X;
      double fy = line.Start.Y - arc.Center.Y;
      double coefficientB = 2D * Dot(fx, fy, dx, dy);
      double coefficientC = (fx * fx) + (fy * fy) - (arc.Radius * arc.Radius);
      double discriminant = (coefficientB * coefficientB) - (4D * lengthSquared * coefficientC);
      double parameterTolerance = tolerance / Math.Max(length, tolerance);
      var intersections = new List<CommonLinePoint>();

      if (discriminant >= 0D)
      {
        double root = Math.Sqrt(discriminant);
        AddLineArcIntersection(
          intersections,
          line,
          arc,
          (-coefficientB - root) / (2D * lengthSquared),
          parameterTolerance,
          tolerance);
        AddLineArcIntersection(
          intersections,
          line,
          arc,
          (-coefficientB + root) / (2D * lengthSquared),
          parameterTolerance,
          tolerance);
      }

      // A numerically near-tangent pair can have a slightly negative discriminant. The closest
      // finite point supplies a conservative contact without flattening either analytic curve.
      double closestParameter = Math.Max(
        0D,
        Math.Min(1D, -Dot(fx, fy, dx, dy) / lengthSquared));
      var closest = new CommonLinePoint(
        line.Start.X + (closestParameter * dx),
        line.Start.Y + (closestParameter * dy));
      if (Math.Abs(Distance(closest, arc.Center) - arc.Radius) <= tolerance
        && ArcContainsPoint(arc, closest, tolerance))
      {
        AddUniquePoint(intersections, closest, tolerance);
      }

      return intersections;
    }

    private static void AddLineArcIntersection(
      ICollection<CommonLinePoint> intersections,
      CommonCenterCurve line,
      CommonCenterCurve arc,
      double parameter,
      double parameterTolerance,
      double tolerance)
    {
      if (parameter < -parameterTolerance || parameter > 1D + parameterTolerance)
      {
        return;
      }

      double clamped = Math.Max(0D, Math.Min(1D, parameter));
      var point = new CommonLinePoint(
        line.Start.X + (clamped * (line.End.X - line.Start.X)),
        line.Start.Y + (clamped * (line.End.Y - line.Start.Y)));
      if (ArcContainsPoint(arc, point, tolerance))
      {
        AddUniquePoint(intersections, point, tolerance);
      }
    }

    private static void ValidateEndpointOnArc(
      CommonLinePoint point,
      CommonCenterCurve owner,
      CommonCenterCurve arc,
      double tolerance)
    {
      if (ArcContainsPoint(arc, point, tolerance))
      {
        RequireSharedEndpoint(owner, arc, point, tolerance);
      }
    }

    private static void ValidateArcArcContact(
      CommonCenterCurve first,
      CommonCenterCurve second,
      double tolerance)
    {
      double centerDistance = Distance(first.Center, second.Center);
      if (centerDistance <= tolerance && Math.Abs(first.Radius - second.Radius) <= tolerance)
      {
        double overlapLength = AngularOverlap(first, second, tolerance)
          * ((first.Radius + second.Radius) / 2D);
        if (overlapLength > tolerance)
        {
          ThrowCommonOuterConflict();
        }

        if (overlapLength > 0D && !HaveSharedEndpoint(first, second, tolerance))
        {
          ThrowCommonOuterConflict();
        }

        ValidateEndpointOnArc(first.Start, first, second, tolerance);
        ValidateEndpointOnArc(first.End, first, second, tolerance);
        ValidateEndpointOnArc(second.Start, second, first, tolerance);
        ValidateEndpointOnArc(second.End, second, first, tolerance);
        return;
      }

      foreach (CommonLinePoint intersection in ArcArcIntersections(first, second, tolerance))
      {
        RequireSharedEndpoint(first, second, intersection, tolerance);
      }

      ValidateEndpointOnArc(first.Start, first, second, tolerance);
      ValidateEndpointOnArc(first.End, first, second, tolerance);
      ValidateEndpointOnArc(second.Start, second, first, tolerance);
      ValidateEndpointOnArc(second.End, second, first, tolerance);
    }

    private static IReadOnlyList<CommonLinePoint> ArcArcIntersections(
      CommonCenterCurve first,
      CommonCenterCurve second,
      double tolerance)
    {
      double dx = second.Center.X - first.Center.X;
      double dy = second.Center.Y - first.Center.Y;
      double distance = Math.Sqrt((dx * dx) + (dy * dy));
      var intersections = new List<CommonLinePoint>();
      if (distance <= tolerance
        || distance > first.Radius + second.Radius + tolerance
        || distance < Math.Abs(first.Radius - second.Radius) - tolerance)
      {
        return intersections;
      }

      double along = ((first.Radius * first.Radius)
        - (second.Radius * second.Radius)
        + (distance * distance)) / (2D * distance);
      double heightSquared = (first.Radius * first.Radius) - (along * along);
      double heightTolerance = 2D * Math.Max(first.Radius, second.Radius) * tolerance;
      if (heightSquared < -heightTolerance)
      {
        return intersections;
      }

      double height = Math.Sqrt(Math.Max(0D, heightSquared));
      double baseX = first.Center.X + (along * dx / distance);
      double baseY = first.Center.Y + (along * dy / distance);
      double offsetX = -dy * height / distance;
      double offsetY = dx * height / distance;
      AddArcArcIntersection(
        intersections,
        first,
        second,
        new CommonLinePoint(baseX + offsetX, baseY + offsetY),
        tolerance);
      AddArcArcIntersection(
        intersections,
        first,
        second,
        new CommonLinePoint(baseX - offsetX, baseY - offsetY),
        tolerance);
      return intersections;
    }

    private static void AddArcArcIntersection(
      ICollection<CommonLinePoint> intersections,
      CommonCenterCurve first,
      CommonCenterCurve second,
      CommonLinePoint point,
      double tolerance)
    {
      if (ArcContainsPoint(first, point, tolerance)
        && ArcContainsPoint(second, point, tolerance))
      {
        AddUniquePoint(intersections, point, tolerance);
      }
    }

    private static void AddUniquePoint(
      ICollection<CommonLinePoint> points,
      CommonLinePoint point,
      double tolerance)
    {
      if (!points.Any(existing => SamePoint(existing, point, tolerance)))
      {
        points.Add(point);
      }
    }

    private static bool ArcContainsPoint(
      CommonCenterCurve arc,
      CommonLinePoint point,
      double tolerance)
    {
      if (Math.Abs(Distance(arc.Center, point) - arc.Radius) > tolerance)
      {
        return false;
      }

      double angularTolerance = tolerance / Math.Max(arc.Radius, tolerance);
      double sweep = arc.SweepRadians;
      if (Math.Abs(sweep) >= (2D * Math.PI) - angularTolerance)
      {
        return true;
      }

      double angle = Math.Atan2(point.Y - arc.Center.Y, point.X - arc.Center.X);
      double travel = sweep >= 0D
        ? PositiveAngle(arc.StartAngleRadians, angle)
        : PositiveAngle(angle, arc.StartAngleRadians);
      return travel <= Math.Abs(sweep) + angularTolerance;
    }

    private static double AngularOverlap(
      CommonCenterCurve first,
      CommonCenterCurve second,
      double tolerance)
    {
      double overlap = 0D;
      foreach (CurveAngleInterval a in CurveIntervals(first, tolerance))
      {
        foreach (CurveAngleInterval b in CurveIntervals(second, tolerance))
        {
          overlap += Math.Max(0D, Math.Min(a.End, b.End) - Math.Max(a.Start, b.Start));
        }
      }

      return overlap;
    }

    private static IEnumerable<CurveAngleInterval> CurveIntervals(
      CommonCenterCurve arc,
      double tolerance)
    {
      double length = Math.Min(2D * Math.PI, Math.Abs(arc.SweepRadians));
      double angularTolerance = tolerance / Math.Max(arc.Radius, tolerance);
      if (length >= (2D * Math.PI) - angularTolerance)
      {
        yield return new CurveAngleInterval(0D, 2D * Math.PI);
        yield break;
      }

      double start = arc.SweepRadians >= 0D
        ? NormalizeRadians(arc.StartAngleRadians)
        : NormalizeRadians(arc.StartAngleRadians + arc.SweepRadians);
      double end = start + length;
      if (end <= 2D * Math.PI)
      {
        yield return new CurveAngleInterval(start, end);
      }
      else
      {
        yield return new CurveAngleInterval(start, 2D * Math.PI);
        yield return new CurveAngleInterval(0D, end - (2D * Math.PI));
      }
    }

    private static double NormalizeRadians(double angle)
    {
      double normalized = angle % (2D * Math.PI);
      return normalized < 0D ? normalized + (2D * Math.PI) : normalized;
    }

    private static double PositiveAngle(double start, double end)
      => NormalizeRadians(end - start);

    private static bool HaveSharedEndpoint(
      CommonCenterCurve first,
      CommonCenterCurve second,
      double tolerance)
      => (CurveHasEndpoint(first, second.Start, tolerance)
          && CurveHasEndpoint(second, second.Start, tolerance))
        || (CurveHasEndpoint(first, second.End, tolerance)
          && CurveHasEndpoint(second, second.End, tolerance));

    private static void RequireSharedEndpoint(
      CommonCenterCurve first,
      CommonCenterCurve second,
      CommonLinePoint contact,
      double tolerance)
    {
      if (!CurveHasEndpoint(first, contact, tolerance)
        || !CurveHasEndpoint(second, contact, tolerance))
      {
        ThrowCommonOuterConflict();
      }
    }

    private static bool CurveHasEndpoint(
      CommonCenterCurve curve,
      CommonLinePoint point,
      double tolerance)
    {
      if (curve.GeometryKind == CommonLineGeometryKind.CircularArc
        && Math.Abs(curve.SweepRadians) >= (2D * Math.PI) - (tolerance / curve.Radius))
      {
        return false;
      }

      return SamePoint(curve.Start, point, tolerance)
        || SamePoint(curve.End, point, tolerance);
    }

    private static void ThrowCommonOuterConflict()
      => throw Invalid("COMMON_LINE overlaps or crosses CUT_OUTER away from a shared endpoint.");

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

    private static CommonBoundarySpan CreateLineSpan(
      IPartPlacement placement,
      int edgeIndex,
      double startParameter,
      double endParameter,
      double tolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      GetEdge(placement, edgeIndex, tolerance);
      ValidateParameterRange(startParameter, endParameter);
      int ringCount = RingCount(placement.Part.Points, tolerance);
      CommonLinePoint mappedEdgeStart = NfpPoint(placement, edgeIndex);
      CommonLinePoint mappedEdgeEnd = NfpPoint(placement, (edgeIndex + 1) % ringCount);

      // Prefer a stable address in the original DXF. Besides retaining exact source geometry, its
      // fingerprints make a saved nest fail closed if that drawing is later edited in place.
      string sourceName = placement?.Part?.Name;
      if (!string.IsNullOrWhiteSpace(sourceName)
        && sourceName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
      {
        DxfContourCatalog catalog = sourceCatalogs == null
          ? LoadPlacementCatalog(placement, tolerance)
          : GetPlacementCatalog(placement, sourceCatalogs, tolerance);
        DxfSourceContour[] outside = catalog.Contours
          .Where(contour => !contour.IsChild && contour.IsClosed)
          .ToArray();
        if (outside.Length != 1)
        {
          throw Invalid(
            $"Part {placement.Id}'s indexed common edge cannot be tied to one closed source contour.");
        }

        var matches = new List<(DxfSourcePrimitive Primitive, bool SameDirection)>();
        foreach (DxfSourcePrimitive primitive in outside[0].Primitives.Where(candidate =>
          candidate.GeometryKind == CommonLineGeometryKind.Line))
        {
          if (TryMatchSourceSegment(
            mappedEdgeStart,
            mappedEdgeEnd,
            TransformSourcePointForMapping(
              placement,
              primitive.Start.X,
              primitive.Start.Y),
            TransformSourcePointForMapping(
              placement,
              primitive.End.X,
              primitive.End.Y),
            SourceMappingTolerance(placement, primitive.Start, tolerance),
            SourceMappingTolerance(placement, primitive.End, tolerance),
            out bool candidateSameDirection))
          {
            matches.Add((primitive, candidateSameDirection));
          }
        }

        if (matches.Count != 1)
        {
          throw Invalid(
            $"Part {placement.Id}'s indexed common edge cannot be mapped uniquely to its source DXF.");
        }

        bool sameDirection = matches[0].SameDirection;
        double sourceStart = sameDirection ? startParameter : 1D - endParameter;
        double sourceEnd = sameDirection ? endParameter : 1D - startParameter;
        return DxfCommonLineSpanResolver.CreateSpan(
          catalog,
          outside[0].Index,
          matches[0].Primitive.Index,
          sourceStart,
          sourceEnd);
      }

      // Non-DXF/in-memory geometry has no immutable source identity. A negative contour index denotes
      // the already-rotated nesting contour and is revalidated against that exact edge on every export.
      return new CommonBoundarySpan(
        -1,
        edgeIndex,
        startParameter,
        endParameter);
    }

    private static ResolvedEdge CalculateParametricEdge(
      IPartPlacement partA,
      int edgeAIndex,
      CommonBoundarySpan spanA,
      IPartPlacement partB,
      int edgeBIndex,
      CommonBoundarySpan spanB,
      double gap,
      double tolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      if (partA == null || partB == null)
      {
        throw Invalid("Both common-line parts are required.");
      }

      if (partA.Id == partB.Id)
      {
        throw Invalid("A part cannot share an outside edge with itself.");
      }

      if (spanA == null || spanB == null)
      {
        throw Invalid("Both parametric common-line spans are required.");
      }

      ValidateParameterRange(spanA.StartParameter, spanA.EndParameter);
      ValidateParameterRange(spanB.StartParameter, spanB.EndParameter);

      bool sourceA = spanA.ContourIndex >= 0;
      bool sourceB = spanB.ContourIndex >= 0;
      if (sourceA != sourceB)
      {
        throw Invalid("Both common boundary spans must use the same source-addressing mode.");
      }

      if (sourceA)
      {
        DxfContourCatalog catalogA = sourceCatalogs == null
          ? LoadPlacementCatalog(partA, tolerance)
          : GetPlacementCatalog(partA, sourceCatalogs, tolerance);
        DxfContourCatalog catalogB = sourceCatalogs == null
          ? LoadPlacementCatalog(partB, tolerance)
          : GetPlacementCatalog(partB, sourceCatalogs, tolerance);
        DxfResolvedBoundarySpan resolvedA = DxfCommonLineSpanResolver.Resolve(catalogA, spanA, partA);
        DxfResolvedBoundarySpan resolvedB = DxfCommonLineSpanResolver.Resolve(catalogB, spanB, partB);
        CommonCenterCurve center = DxfCommonLineSpanResolver.CreateCenterCurve(
          resolvedA,
          resolvedB,
          gap,
          tolerance,
          out bool reverseSecondParameter);
        CommonLinePoint bStart = reverseSecondParameter ? resolvedB.End : resolvedB.Start;
        CommonLinePoint bEnd = reverseSecondParameter ? resolvedB.Start : resolvedB.End;
        return new ResolvedEdge
        {
          EdgeAIndex = -1,
          EdgeBIndex = -1,
          EdgeAStartParameter = spanA.StartParameter,
          EdgeAEndParameter = spanA.EndParameter,
          EdgeBStartParameter = spanB.StartParameter,
          EdgeBEndParameter = spanB.EndParameter,
          EdgeAParameterTolerance = (tolerance / Math.Max(resolvedA.Length, tolerance))
            * (spanA.EndParameter - spanA.StartParameter),
          EdgeBParameterTolerance = (tolerance / Math.Max(resolvedB.Length, tolerance))
            * (spanB.EndParameter - spanB.StartParameter),
          AStart = resolvedA.Start,
          AEnd = resolvedA.End,
          BStart = bStart,
          BEnd = bEnd,
          MidStart = center.Start,
          MidEnd = center.End,
          CenterCurve = center,
        };
      }

      if (edgeAIndex < 0)
      {
        edgeAIndex = spanA.PrimitiveIndex;
      }

      if (edgeBIndex < 0)
      {
        edgeBIndex = spanB.PrimitiveIndex;
      }

      if (spanA.PrimitiveIndex != edgeAIndex || spanB.PrimitiveIndex != edgeBIndex)
      {
        throw Invalid("A local common-line span no longer addresses its recorded outside edge.");
      }

      EdgeGeometry wholeA = GetEdge(partA, edgeAIndex, tolerance);
      EdgeGeometry wholeB = GetEdge(partB, edgeBIndex, tolerance);
      EdgeGeometry edgeA = SliceEdge(wholeA, spanA.StartParameter, spanA.EndParameter);
      EdgeGeometry edgeB = SliceEdge(wholeB, spanB.StartParameter, spanB.EndParameter);
      ResolvedEdge resolved = CalculateStraightPair(edgeA, edgeB, gap, tolerance);
      resolved.EdgeAIndex = edgeAIndex;
      resolved.EdgeBIndex = edgeBIndex;
      resolved.EdgeAStartParameter = spanA.StartParameter;
      resolved.EdgeAEndParameter = spanA.EndParameter;
      resolved.EdgeBStartParameter = spanB.StartParameter;
      resolved.EdgeBEndParameter = spanB.EndParameter;
      resolved.EdgeAParameterTolerance = tolerance / Math.Max(wholeA.Length, tolerance);
      resolved.EdgeBParameterTolerance = tolerance / Math.Max(wholeB.Length, tolerance);
      return resolved;
    }

    private static EdgeGeometry SliceEdge(EdgeGeometry edge, double startParameter, double endParameter)
    {
      CommonLinePoint start = Interpolate(edge.Start, edge.End, startParameter);
      CommonLinePoint end = Interpolate(edge.Start, edge.End, endParameter);
      double length = edge.Length * (endParameter - startParameter);
      return new EdgeGeometry(
        start,
        end,
        length,
        edge.UnitX,
        edge.UnitY,
        edge.OutwardX,
        edge.OutwardY);
    }

    private static ResolvedEdge CalculateStraightPair(
      EdgeGeometry edgeA,
      EdgeGeometry edgeB,
      double gap,
      double tolerance)
    {
      if (Math.Abs(edgeA.Length - edgeB.Length) > tolerance)
      {
        throw Invalid("Common-edge spans must have the same usable length.");
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
        Swap(ref bStart, ref bEnd);
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
      if (!SamePoint(midStart, new CommonLinePoint(
        (aStart.X + pairedBStart.X) / 2D,
        (aStart.Y + pairedBStart.Y) / 2D), tolerance))
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
        CenterCurve = new CommonCenterCurve(CommonLineGeometryKind.Line, midStart, midEnd),
      };
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
      ResolvedEdge resolved = CalculateStraightPair(edgeA, edgeB, gap, tolerance);
      resolved.EdgeAIndex = edgeAIndex;
      resolved.EdgeBIndex = edgeBIndex;
      resolved.EdgeAStartParameter = 0D;
      resolved.EdgeAEndParameter = 1D;
      resolved.EdgeBStartParameter = 0D;
      resolved.EdgeBEndParameter = 1D;
      resolved.EdgeAParameterTolerance = tolerance / Math.Max(edgeA.Length, tolerance);
      resolved.EdgeBParameterTolerance = tolerance / Math.Max(edgeB.Length, tolerance);
      return resolved;
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
        throw Invalid(
          "Legacy indexed common edges support straight outside segments only; circular seams require exact source spans.");
      }

      CommonLinePoint mappedStart = NfpPoint(placement, edgeIndex);
      CommonLinePoint mappedEnd = NfpPoint(placement, (edgeIndex + 1) % count);
      double dx = mappedEnd.X - mappedStart.X;
      double dy = mappedEnd.Y - mappedStart.Y;
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
      CommonLinePoint start = PlacedPoint(placement, edgeIndex);
      CommonLinePoint end = PlacedPoint(placement, (edgeIndex + 1) % count);
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
      if (count < 3)
      {
        return 0D;
      }

      double originX = points[0].X;
      double originY = points[0].Y;
      double twiceArea = 0D;
      for (int i = 1; i < count - 1; i++)
      {
        double ax = points[i].X - originX;
        double ay = points[i].Y - originY;
        double bx = points[i + 1].X - originX;
        double by = points[i + 1].Y - originY;
        twiceArea += (ax * by) - (bx * ay);
      }

      return twiceArea / 2D;
    }

    private static CommonLinePoint PlacedPoint(IPartPlacement placement, int index)
      => new CommonLinePoint(
        placement.Part.Points[index].X + placement.X,
        placement.Part.Points[index].Y + placement.Y);

    private static CommonLinePoint NfpPoint(IPartPlacement placement, int index)
      => new CommonLinePoint(
        placement.Part.Points[index].X,
        placement.Part.Points[index].Y);

    private static CommonLinePoint Interpolate(CommonLinePoint start, CommonLinePoint end, double parameter)
      => new CommonLinePoint(
        start.X + ((end.X - start.X) * parameter),
        start.Y + ((end.Y - start.Y) * parameter));

    private static void ValidateParameterRange(double startParameter, double endParameter)
    {
      if (!double.IsFinite(startParameter)
        || !double.IsFinite(endParameter)
        || startParameter < 0D
        || endParameter > 1D
        || endParameter <= startParameter)
      {
        throw Invalid("A common boundary span must be a finite increasing interval inside [0, 1].");
      }
    }

    private static void AddConsumedSpan(
      IDictionary<PartEdgeKey, List<ParameterRange>> consumed,
      PartEdgeKey key,
      ParameterRange span,
      double parameterTolerance)
    {
      if (!key.IsValid)
      {
        throw Invalid("A common boundary span has no stable outside-edge address.");
      }

      if (!consumed.TryGetValue(key, out List<ParameterRange> spans))
      {
        consumed[key] = spans = new List<ParameterRange>();
      }

      if (spans.Any(existing => existing.Overlaps(span, parameterTolerance)))
      {
        throw Invalid("An outside-edge interval belongs to more than one common-line relationship.");
      }

      spans.Add(span);
    }

    private static void ValidateRecordedCenterCurve(
      CommonEdge record,
      CommonCenterCurve resolved,
      double tolerance)
    {
      CommonCenterCurve recorded = record.CenterCurve;
      if (recorded == null || resolved == null
        || record.GeometryKind != resolved.GeometryKind
        || recorded.GeometryKind != resolved.GeometryKind
        || !SamePoint(recorded.Start, resolved.Start, tolerance)
        || !SamePoint(recorded.End, resolved.End, tolerance))
      {
        throw Invalid("A recorded parametric common curve no longer matches the final placed geometry.");
      }

      if (resolved.GeometryKind == CommonLineGeometryKind.CircularArc)
      {
        double angularTolerance = tolerance / Math.Max(resolved.Radius, tolerance);
        if (!SamePoint(recorded.Center, resolved.Center, tolerance)
          || Math.Abs(recorded.Radius - resolved.Radius) > tolerance
          || AngleDistance(recorded.StartAngleRadians, resolved.StartAngleRadians) > angularTolerance
          || Math.Abs(recorded.SweepRadians - resolved.SweepRadians) > angularTolerance)
        {
          throw Invalid("A recorded circular common curve no longer matches the final placed geometry.");
        }
      }
    }

    private static double AngleDistance(double first, double second)
    {
      double delta = Math.Abs((first - second) % (2D * Math.PI));
      return Math.Min(delta, (2D * Math.PI) - delta);
    }

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

    /// <summary>
    /// Matches a tessellated NFP edge back to its exact source primitive. Each exact endpoint gets
    /// only the additional error introduced by the parser's double-to-PointF round trip. This larger
    /// tolerance is deliberately confined to identity mapping; gap, collision, closure and export
    /// validation continue to use GeometryTolerance.
    /// </summary>
    private static bool TryMatchSourceSegment(
      CommonLinePoint nfpStart,
      CommonLinePoint nfpEnd,
      CommonLinePoint exactStart,
      CommonLinePoint exactEnd,
      double exactStartMappingTolerance,
      double exactEndMappingTolerance,
      out bool sameDirection)
    {
      bool forward = SamePoint(nfpStart, exactStart, exactStartMappingTolerance)
        && SamePoint(nfpEnd, exactEnd, exactEndMappingTolerance);
      bool reverse = SamePoint(nfpStart, exactEnd, exactEndMappingTolerance)
        && SamePoint(nfpEnd, exactStart, exactStartMappingTolerance);
      sameDirection = forward;
      return forward != reverse;
    }

    private static double SourceMappingTolerance(
      IPartPlacement placement,
      CommonLinePoint exactLocalPoint,
      double geometryTolerance)
    {
      float roundedX = (float)exactLocalPoint.X;
      float roundedY = (float)exactLocalPoint.Y;
      if (float.IsNaN(roundedX)
        || float.IsInfinity(roundedX)
        || float.IsNaN(roundedY)
        || float.IsInfinity(roundedY))
      {
        throw Invalid("An exact source coordinate cannot be represented by the nesting PointF geometry.");
      }

      CommonLinePoint exactMapped = TransformSourcePointForMapping(
        placement,
        exactLocalPoint.X,
        exactLocalPoint.Y);
      CommonLinePoint roundTrippedMapped = TransformSourcePointForMapping(
        placement,
        roundedX,
        roundedY);
      double roundTripError = Distance(exactMapped, roundTrippedMapped);
      if (!double.IsFinite(roundTripError))
      {
        throw Invalid("An exact source coordinate has a non-finite PointF round-trip error.");
      }

      return geometryTolerance + roundTripError;
    }

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
      public SourceCacheKey(string path, string contentFingerprint)
      {
        this.Path = path;
        this.ContentFingerprint = contentFingerprint ?? string.Empty;
      }

      public string Path { get; }

      public string ContentFingerprint { get; }

      public bool Equals(SourceCacheKey other)
        => string.Equals(
            this.ContentFingerprint,
            other.ContentFingerprint,
            StringComparison.Ordinal)
          && string.Equals(this.Path, other.Path, StringComparison.OrdinalIgnoreCase);

      public override bool Equals(object obj)
        => obj is SourceCacheKey other && this.Equals(other);

      public override int GetHashCode()
        => HashCode.Combine(
          StringComparer.OrdinalIgnoreCase.GetHashCode(this.Path),
          StringComparer.Ordinal.GetHashCode(this.ContentFingerprint));
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
      public SourceBoundarySegment(
        CommonLinePoint start,
        CommonLinePoint end,
        CommonLinePoint mappingStart,
        CommonLinePoint mappingEnd,
        double bulge,
        double startMappingTolerance,
        double endMappingTolerance)
      {
        this.Start = start;
        this.End = end;
        this.MappingStart = mappingStart;
        this.MappingEnd = mappingEnd;
        this.Bulge = bulge;
        this.StartMappingTolerance = startMappingTolerance;
        this.EndMappingTolerance = endMappingTolerance;
      }

      public CommonLinePoint Start { get; }

      public CommonLinePoint End { get; }

      public CommonLinePoint MappingStart { get; }

      public CommonLinePoint MappingEnd { get; }

      public double Bulge { get; }

      public double StartMappingTolerance { get; }

      public double EndMappingTolerance { get; }
    }

    private sealed class ResolvedEdge
    {
      public CommonEdge Record { get; set; }

      public int EdgeAIndex { get; set; }

      public int EdgeBIndex { get; set; }

      public double EdgeAStartParameter { get; set; }

      public double EdgeAEndParameter { get; set; } = 1D;

      public double EdgeBStartParameter { get; set; }

      public double EdgeBEndParameter { get; set; } = 1D;

      public double EdgeAParameterTolerance { get; set; }

      public double EdgeBParameterTolerance { get; set; }

      public CommonLinePoint AStart { get; set; }

      public CommonLinePoint AEnd { get; set; }

      public CommonLinePoint BStart { get; set; }

      public CommonLinePoint BEnd { get; set; }

      public CommonLinePoint MidStart { get; set; }

      public CommonLinePoint MidEnd { get; set; }

      public CommonCenterCurve CenterCurve { get; set; }
    }

    private readonly struct ParameterRange
    {
      public ParameterRange(double start, double end)
      {
        this.Start = start;
        this.End = end;
      }

      public double Start { get; }

      public double End { get; }

      public bool Overlaps(ParameterRange other, double tolerance)
        => Math.Max(this.Start, other.Start) < Math.Min(this.End, other.End) - tolerance;
    }

    private readonly struct CurveAngleInterval
    {
      public CurveAngleInterval(double start, double end)
      {
        this.Start = start;
        this.End = end;
      }

      public double Start { get; }

      public double End { get; }
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
        : this(partId, edgeIndex, -1, -1)
      {
      }

      private PartEdgeKey(
        int partId,
        int edgeIndex,
        int contourIndex,
        int primitiveIndex)
      {
        this.PartId = partId;
        this.EdgeIndex = edgeIndex;
        this.ContourIndex = contourIndex;
        this.PrimitiveIndex = primitiveIndex;
      }

      public static PartEdgeKey For(int partId, int edgeIndex, CommonBoundarySpan span)
        => span != null && span.ContourIndex >= 0
          ? new PartEdgeKey(partId, -1, span.ContourIndex, span.PrimitiveIndex)
          : new PartEdgeKey(partId, edgeIndex);

      public int PartId { get; }

      public int EdgeIndex { get; }

      public int ContourIndex { get; }

      public int PrimitiveIndex { get; }

      public bool IsSource => this.ContourIndex >= 0 && this.PrimitiveIndex >= 0;

      public bool IsValid => this.EdgeIndex >= 0 || this.IsSource;

      public bool Equals(PartEdgeKey other)
        => this.PartId == other.PartId
          && this.EdgeIndex == other.EdgeIndex
          && this.ContourIndex == other.ContourIndex
          && this.PrimitiveIndex == other.PrimitiveIndex;

      public override bool Equals(object obj) => obj is PartEdgeKey other && this.Equals(other);

      public override int GetHashCode()
        => HashCode.Combine(this.PartId, this.EdgeIndex, this.ContourIndex, this.PrimitiveIndex);
    }

    internal readonly struct PointKey : IEquatable<PointKey>, IComparable<PointKey>
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
        int x = BitConverter.Int64BitsToDouble(this.X)
          .CompareTo(BitConverter.Int64BitsToDouble(other.X));
        return x != 0
          ? x
          : BitConverter.Int64BitsToDouble(this.Y)
            .CompareTo(BitConverter.Int64BitsToDouble(other.Y));
      }

      public bool Equals(PointKey other) => this.X == other.X && this.Y == other.Y;

      public override bool Equals(object obj) => obj is PointKey other && this.Equals(other);

      public override int GetHashCode() => HashCode.Combine(this.X, this.Y);
    }

    private readonly struct PointBucketKey : IEquatable<PointBucketKey>
    {
      public PointBucketKey(long x, long y)
      {
        this.X = x;
        this.Y = y;
      }

      public long X { get; }

      public long Y { get; }

      public bool Equals(PointBucketKey other) => this.X == other.X && this.Y == other.Y;

      public override bool Equals(object obj) => obj is PointBucketKey other && this.Equals(other);

      public override int GetHashCode() => HashCode.Combine(this.X, this.Y);
    }

    private sealed class CanonicalBoundaryPoint
    {
      public CanonicalBoundaryPoint(PointKey key, CommonLinePoint point)
      {
        this.Key = key;
        this.Point = point;
      }

      public PointKey Key { get; }

      public CommonLinePoint Point { get; }
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

    internal sealed class PointQuantizer
    {
      private readonly Dictionary<PointBucketKey, List<CanonicalBoundaryPoint>> buckets
        = new Dictionary<PointBucketKey, List<CanonicalBoundaryPoint>>();
      private readonly Dictionary<PointKey, CommonLinePoint> points
        = new Dictionary<PointKey, CommonLinePoint>();
      private readonly int neighborRadius;
      private readonly double scale;
      private readonly double toleranceSquared;

      public PointQuantizer(double tolerance)
      {
        if (!double.IsFinite(tolerance) || tolerance <= 0D)
        {
          throw Invalid("GeometryTolerance must be a finite positive distance.");
        }

        this.scale = Math.Min(1_000_000_000D, Math.Max(1D, Math.Ceiling(1D / tolerance)));
        double requiredRadius = Math.Ceiling(tolerance * this.scale) + 1D;
        this.neighborRadius = requiredRadius <= 8D ? Math.Max(1, (int)requiredRadius) : 9;
        this.toleranceSquared = tolerance * tolerance;
      }

      public PointKey Key(CommonLinePoint point)
      {
        if (point == null
          || !double.IsFinite(point.X)
          || !double.IsFinite(point.Y))
        {
          throw Invalid("A boundary node is null or non-finite.");
        }

        PointBucketKey bucket = this.Bucket(point);
        CanonicalBoundaryPoint nearest = null;
        double nearestDistanceSquared = double.PositiveInfinity;
        if (this.neighborRadius <= 8)
        {
          for (int x = -this.neighborRadius; x <= this.neighborRadius; x++)
          {
            for (int y = -this.neighborRadius; y <= this.neighborRadius; y++)
            {
              this.ConsiderBucket(
                new PointBucketKey(unchecked(bucket.X + x), unchecked(bucket.Y + y)),
                point,
                ref nearest,
                ref nearestDistanceSquared);
            }
          }
        }
        else
        {
          foreach (PointBucketKey candidate in this.buckets.Keys.OrderBy(key => key.X).ThenBy(key => key.Y))
          {
            this.ConsiderBucket(candidate, point, ref nearest, ref nearestDistanceSquared);
          }
        }

        if (nearest != null)
        {
          return nearest.Key;
        }

        var key = new PointKey(CoordinateBits(point.X), CoordinateBits(point.Y));
        if (this.points.ContainsKey(key))
        {
          return key;
        }

        this.points.Add(key, point);
        if (!this.buckets.TryGetValue(bucket, out List<CanonicalBoundaryPoint> bucketPoints))
        {
          bucketPoints = new List<CanonicalBoundaryPoint>();
          this.buckets.Add(bucket, bucketPoints);
        }

        bucketPoints.Add(new CanonicalBoundaryPoint(key, point));
        return key;
      }

      public CommonLinePoint Point(PointKey key)
      {
        if (!this.points.TryGetValue(key, out CommonLinePoint point))
        {
          throw Invalid("A boundary node key has no canonical point.");
        }

        return point;
      }

      internal CommonLinePoint Canonical(CommonLinePoint point) => this.Point(this.Key(point));

      private static long CoordinateBits(double value)
        => BitConverter.DoubleToInt64Bits(value == 0D ? 0D : value);

      private PointBucketKey Bucket(CommonLinePoint point)
        => new PointBucketKey(
          checked((long)Math.Round(point.X * this.scale)),
          checked((long)Math.Round(point.Y * this.scale)));

      private void ConsiderBucket(
        PointBucketKey bucket,
        CommonLinePoint point,
        ref CanonicalBoundaryPoint nearest,
        ref double nearestDistanceSquared)
      {
        if (!this.buckets.TryGetValue(bucket, out List<CanonicalBoundaryPoint> candidates))
        {
          return;
        }

        foreach (CanonicalBoundaryPoint candidate in candidates)
        {
          double dx = candidate.Point.X - point.X;
          double dy = candidate.Point.Y - point.Y;
          double distanceSquared = (dx * dx) + (dy * dy);
          if (distanceSquared > this.toleranceSquared)
          {
            continue;
          }

          if (nearest == null
            || distanceSquared < nearestDistanceSquared
            || (distanceSquared == nearestDistanceSquared && candidate.Key.CompareTo(nearest.Key) < 0))
          {
            nearest = candidate;
            nearestDistanceSquared = distanceSquared;
          }
        }
      }
    }

    private sealed class ValidationGeometry
    {
      public ValidationGeometry(IReadOnlyList<IReadOnlyList<CommonLinePoint>> rings)
      {
        this.Rings = rings ?? throw new ArgumentNullException(nameof(rings));
        if (rings.Count == 0)
        {
          throw Invalid("A placed part has no validation boundary.");
        }

        this.RingBounds = rings.Select(GeometryBounds.FromRing).ToArray();
        GeometryBounds combined = this.RingBounds[0];
        for (int i = 1; i < this.RingBounds.Count; i++)
        {
          combined = combined.Union(this.RingBounds[i]);
        }

        this.Bounds = combined;
      }

      public IReadOnlyList<IReadOnlyList<CommonLinePoint>> Rings { get; }

      public IReadOnlyList<GeometryBounds> RingBounds { get; }

      public GeometryBounds Bounds { get; }
    }

    private readonly struct GeometryBounds
    {
      private GeometryBounds(double minX, double minY, double maxX, double maxY)
      {
        this.MinX = minX;
        this.MinY = minY;
        this.MaxX = maxX;
        this.MaxY = maxY;
      }

      public double MinX { get; }

      public double MinY { get; }

      public double MaxX { get; }

      public double MaxY { get; }

      public static GeometryBounds FromRing(IReadOnlyList<CommonLinePoint> ring)
      {
        if (ring == null || ring.Count < 3)
        {
          throw Invalid("A placed part contains an empty or degenerate validation ring.");
        }

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        foreach (CommonLinePoint point in ring)
        {
          if (point == null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
          {
            throw Invalid("A placed part contains non-finite validation geometry.");
          }

          minX = Math.Min(minX, point.X);
          minY = Math.Min(minY, point.Y);
          maxX = Math.Max(maxX, point.X);
          maxY = Math.Max(maxY, point.Y);
        }

        return new GeometryBounds(minX, minY, maxX, maxY);
      }

      public static GeometryBounds FromSegment(CommonLinePoint start, CommonLinePoint end)
      {
        if (start == null
          || end == null
          || !double.IsFinite(start.X)
          || !double.IsFinite(start.Y)
          || !double.IsFinite(end.X)
          || !double.IsFinite(end.Y))
        {
          throw Invalid("A COMMON_LINE segment contains non-finite geometry.");
        }

        return new GeometryBounds(
          Math.Min(start.X, end.X),
          Math.Min(start.Y, end.Y),
          Math.Max(start.X, end.X),
          Math.Max(start.Y, end.Y));
      }

      public GeometryBounds Union(GeometryBounds other)
        => new GeometryBounds(
          Math.Min(this.MinX, other.MinX),
          Math.Min(this.MinY, other.MinY),
          Math.Max(this.MaxX, other.MaxX),
          Math.Max(this.MaxY, other.MaxY));

      public bool Overlaps(GeometryBounds other, double tolerance)
        => this.MaxX + tolerance >= other.MinX
          && other.MaxX + tolerance >= this.MinX
          && this.MaxY + tolerance >= other.MinY
          && other.MaxY + tolerance >= this.MinY;
    }

    private sealed class BoundaryGraph
    {
      private readonly Dictionary<PointKey, List<int>> adjacency = new Dictionary<PointKey, List<int>>();
      private readonly HashSet<SegmentKey> unique = new HashSet<SegmentKey>();
      private readonly List<BoundaryEdge> edges = new List<BoundaryEdge>();
      private readonly PointQuantizer quantizer;
      private readonly double tolerance;
      private readonly CancellationToken cancel;

      public BoundaryGraph(PointQuantizer quantizer, double tolerance, CancellationToken cancel)
      {
        this.quantizer = quantizer;
        this.tolerance = tolerance;
        this.cancel = cancel;
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
          this.cancel.ThrowIfCancellationRequested();
          if (node.Value.Count != 2)
          {
            throw Invalid("CUT_OUTER reconstruction contains an open or branched contour.");
          }
        }

        var used = new bool[this.edges.Count];
        var paths = new List<CommonLinePath>();
        for (int seed = 0; seed < this.edges.Count; seed++)
        {
          this.cancel.ThrowIfCancellationRequested();
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

          if (SelfIntersects(flattened, this.tolerance, this.cancel))
          {
            throw Invalid("CUT_OUTER reconstruction produced a self-intersecting contour.");
          }

          paths.Add(new CommonLinePath(points, isClosed: true, bulges: bulges));
        }

        return paths;
      }

      internal static IReadOnlyList<CommonLinePoint> Flatten(
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
          if (flattened.Count > MaxValidationPathSegments)
          {
            throw Invalid(
              "A curved CUT_OUTER path exceeds the bounded validation-segment budget.");
          }
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

        double requiredSegments = Math.Ceiling(Math.Abs(theta) / step);
        if (!double.IsFinite(requiredSegments)
          || requiredSegments > MaxValidationCurveSegments)
        {
          throw Invalid("A curved CUT_OUTER segment requires too many validation samples.");
        }

        int count = Math.Max(1, (int)requiredSegments);
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
        if (points.Count < 3)
        {
          return 0D;
        }

        double originX = points[0].X;
        double originY = points[0].Y;
        double area = 0D;
        for (int i = 1; i < points.Count - 1; i++)
        {
          double ax = points[i].X - originX;
          double ay = points[i].Y - originY;
          double bx = points[i + 1].X - originX;
          double by = points[i + 1].Y - originY;
          area += (ax * by) - (bx * ay);
        }

        return area / 2D;
      }

      private static bool SelfIntersects(IReadOnlyList<CommonLinePoint> points, double tolerance, CancellationToken cancel = default)
      {
        var segments = new List<FlatSegmentBounds>(points.Count);
        for (int i = 0; i < points.Count; i++)
        {
          cancel.ThrowIfCancellationRequested();
          CommonLinePoint start = points[i];
          CommonLinePoint end = points[(i + 1) % points.Count];
          segments.Add(new FlatSegmentBounds(i, start, end, tolerance));
        }

        segments.Sort((left, right) =>
        {
          int byX = left.MinX.CompareTo(right.MinX);
          return byX != 0 ? byX : left.Index.CompareTo(right.Index);
        });
        var active = new List<FlatSegmentBounds>();
        foreach (FlatSegmentBounds current in segments)
        {
          cancel.ThrowIfCancellationRequested();
          active.RemoveAll(candidate => candidate.MaxX < current.MinX);
          foreach (FlatSegmentBounds candidate in active)
          {
            if (candidate.MaxY < current.MinY
              || current.MaxY < candidate.MinY
              || AreAdjacent(candidate.Index, current.Index, points.Count))
            {
              continue;
            }

            if (SegmentsConflict(
              candidate.Start,
              candidate.End,
              current.Start,
              current.End,
              tolerance))
            {
              return true;
            }
          }

          active.Add(current);
        }

        return false;
      }

      private static bool AreAdjacent(int first, int second, int count)
        => Math.Abs(first - second) == 1
          || (first == 0 && second == count - 1)
          || (second == 0 && first == count - 1);

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

      private readonly struct FlatSegmentBounds
      {
        public FlatSegmentBounds(
          int index,
          CommonLinePoint start,
          CommonLinePoint end,
          double tolerance)
        {
          this.Index = index;
          this.Start = start;
          this.End = end;
          this.MinX = Math.Min(start.X, end.X) - tolerance;
          this.MinY = Math.Min(start.Y, end.Y) - tolerance;
          this.MaxX = Math.Max(start.X, end.X) + tolerance;
          this.MaxY = Math.Max(start.Y, end.Y) + tolerance;
        }

        public int Index { get; }

        public CommonLinePoint Start { get; }

        public CommonLinePoint End { get; }

        public double MinX { get; }

        public double MinY { get; }

        public double MaxX { get; }

        public double MaxY { get; }
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
