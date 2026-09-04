namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Concurrent;
  using System.Collections.Generic;
  using System.Collections.ObjectModel;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Security.Cryptography;
  using System.Text;
  using System.Threading;
  using DeepNestLib.Placement;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// An immutable, deterministic view of the cut contours in a DXF. Unlike the nesting polygon, this
  /// catalog keeps exact LINE and circular-arc parameters and retains ELLIPSE/SPLINE source metadata
  /// without mistaking their tessellation chords for common-line candidates. ELLIPSE and SPLINE are
  /// intentionally ineligible until an exact analytic common-cut representation exists.
  /// </summary>
  public sealed class DxfContourCatalog
  {
    private const double FingerprintCoordinateTolerance = 1e-7;
    private const double PlanarityTolerance = 1e-12;
    private static readonly ConcurrentDictionary<CatalogCacheKey, Lazy<DxfContourCatalog>> Cache
      = new ConcurrentDictionary<CatalogCacheKey, Lazy<DxfContourCatalog>>();

    private DxfContourCatalog(
      string sourceName,
      string sourceFingerprint,
      IReadOnlyList<DxfSourceContour> contours)
    {
      this.SourceName = sourceName ?? string.Empty;
      this.SourceFingerprint = sourceFingerprint;
      this.Contours = new ReadOnlyCollection<DxfSourceContour>(contours.ToArray());
    }

    public string SourceName { get; }

    public string SourceFingerprint { get; }

    public IReadOnlyList<DxfSourceContour> Contours { get; }

    public static DxfContourCatalog Load(string path)
      => LoadCore(path, null);

    /// <summary>
    /// Loads a DXF using an endpoint-joining tolerance expressed in the active drawing units. This is
    /// the authoritative overload for nesting operations because a unitless DXF cannot reliably infer
    /// whether the current project coordinates are inches or millimetres.
    /// </summary>
    public static DxfContourCatalog Load(string path, double endpointTolerance)
      => LoadCore(path, ValidateEndpointTolerance(endpointTolerance));

    private static DxfContourCatalog LoadCore(string path, double? endpointTolerance)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        throw new ArgumentException("A DXF path is required.", nameof(path));
      }

      string fullPath = Path.GetFullPath(path);
      var fileInfo = new FileInfo(fullPath);
      if (!fileInfo.Exists)
      {
        throw new FileNotFoundException("The DXF contour source was not found.", fullPath);
      }

      // Length and LastWriteTimeUtc are only hints: CAD programs and file-copy tools can replace a
      // drawing while preserving both.  Parse the same immutable byte snapshot whose digest keys the
      // cache, otherwise a saved CommonBoundarySpan could validate against stale geometry and suppress
      // the newly loaded outside contour during export.
      byte[] sourceBytes = File.ReadAllBytes(fullPath);
      string contentFingerprint = Convert.ToHexString(SHA256.HashData(sourceBytes));
      var key = new CatalogCacheKey(
        fullPath,
        contentFingerprint,
        EndpointToleranceCacheKey(endpointTolerance));
      Lazy<DxfContourCatalog> lazy = Cache.GetOrAdd(
        key,
        candidate => new Lazy<DxfContourCatalog>(
          () =>
          {
            using var stream = new MemoryStream(sourceBytes, writable: false);
            DxfFile file = DxfFile.Load(stream);
            return Create(
              candidate.Path,
              file.Entities.ToArray(),
              endpointTolerance ?? DefaultEndpointTolerance(file.Header));
          },
          LazyThreadSafetyMode.ExecutionAndPublication));
      try
      {
        DxfContourCatalog catalog = lazy.Value;
        foreach (CatalogCacheKey oldKey in Cache.Keys)
        {
          if (!oldKey.Equals(key)
            && string.Equals(oldKey.Path, fullPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(
              oldKey.ContentFingerprint,
              contentFingerprint,
              StringComparison.Ordinal))
          {
            Cache.TryRemove(oldKey, out _);
          }
        }

        return catalog;
      }
      catch
      {
        Cache.TryRemove(key, out _);
        throw;
      }
    }

    /// <summary>
    /// Captures one immutable source snapshot for an export transaction. The catalog fingerprint and
    /// every later entity parse are derived from these exact bytes, preventing a CAD file replacement
    /// from mixing two drawing revisions in one common-line DXF.
    /// </summary>
    internal static DxfSourceSnapshot ReadSnapshot(string path)
      => ReadSnapshotCore(path, null);

    internal static DxfSourceSnapshot ReadSnapshot(string path, double endpointTolerance)
      => ReadSnapshotCore(path, ValidateEndpointTolerance(endpointTolerance));

    private static DxfSourceSnapshot ReadSnapshotCore(string path, double? endpointTolerance)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        throw new ArgumentException("A DXF path is required.", nameof(path));
      }

      string fullPath = Path.GetFullPath(path);
      byte[] sourceBytes = File.ReadAllBytes(fullPath);
      using var stream = new MemoryStream(sourceBytes, writable: false);
      DxfFile file = DxfFile.Load(stream);
      DxfContourCatalog catalog = Create(
        fullPath,
        file.Entities.ToArray(),
        endpointTolerance ?? DefaultEndpointTolerance(file.Header));
      return new DxfSourceSnapshot(fullPath, sourceBytes, catalog);
    }

    public static DxfContourCatalog Create(string sourceName, IEnumerable<DxfEntity> entities)
      => Create(sourceName, entities, CommonLineDefaults.DefaultGeometryToleranceInches);

    /// <summary>
    /// Creates a catalog with an endpoint tolerance expressed in the source drawing's units. This
    /// overload lets callers preserve the same physical joining tolerance for inch, metric, and
    /// unitless drawings without changing the established two-argument API.
    /// </summary>
    public static DxfContourCatalog Create(
      string sourceName,
      IEnumerable<DxfEntity> entities,
      double endpointTolerance)
    {
      if (entities == null)
      {
        throw new ArgumentNullException(nameof(entities));
      }

      if (!double.IsFinite(endpointTolerance) || endpointTolerance <= 0D)
      {
        throw new ArgumentOutOfRangeException(
          nameof(endpointTolerance),
          "The DXF endpoint tolerance must be a finite positive distance.");
      }

      DxfEntity[] sourceEntities = entities.ToArray();
      string planarFrameFingerprint = ValidatePlanarGeometry(sourceEntities);
      RawDetail<DxfEntity> detail = DxfParser.ConvertDxfToRawDetail(
        sourceName ?? string.Empty,
        sourceEntities);
      var contours = new List<DxfSourceContour>();
      foreach (LocalContour<DxfEntity> contour in detail.Outers.OfType<LocalContour<DxfEntity>>())
      {
        contours.Add(BuildContour(contour, endpointTolerance));
      }

      DxfSourceContour[] ordered = contours
        .OrderBy(contour => contour.IsChild ? 1 : 0)
        .ThenBy(contour => contour.Fingerprint, StringComparer.Ordinal)
        .Select((contour, index) => contour.WithIndex(index))
        .ToArray();
      string sourceFingerprint = Fingerprint(
        planarFrameFingerprint + "|" + string.Join("|", ordered.Select(contour => contour.Fingerprint)));
      return new DxfContourCatalog(sourceName, sourceFingerprint, ordered);
    }

    private static DxfSourceContour BuildContour(
      LocalContour<DxfEntity> contour,
      double endpointTolerance)
    {
      List<DxfEntity> source = contour.Entities.ToList();
      List<DxfSourcePrimitive> primitives;
      bool isClosed;

      // This exact LINE/ARC chainer already owns all endpoint and bulge conventions used by export.
      // Work on a list copy: it consumes the collection but does not mutate the source entities.
      if (source.Count > 1
          && source.All(entity => entity is DxfLine || entity is DxfArc))
      {
        var joined = source.ToList();
        if (FlatDxfJoiner.JoinEntities(joined, endpointTolerance)
            && joined.Count == 1
            && joined[0] is DxfLwPolyline joinedPolyline)
        {
          primitives = FromLightweightPolyline(joinedPolyline);
          isClosed = joinedPolyline.IsClosed;
          return FinalizeContour(contour.IsChild, primitives, isClosed);
        }
      }

      if (source.Count == 1)
      {
        primitives = FromEntity(source[0], endpointTolerance, out isClosed);
        return FinalizeContour(contour.IsChild, primitives, isClosed);
      }

      primitives = source.SelectMany(entity => FromEntity(entity, endpointTolerance, out _)).ToList();
      primitives = OrderConnected(primitives, endpointTolerance, out isClosed);
      return FinalizeContour(contour.IsChild, primitives, isClosed);
    }

    private static DxfSourceContour FinalizeContour(
      bool isChild,
      List<DxfSourcePrimitive> primitives,
      bool isClosed)
    {
      if (isClosed && primitives.Count > 1
          && primitives.All(primitive => primitive.GeometryKind == CommonLineGeometryKind.Line
            || primitive.GeometryKind == CommonLineGeometryKind.CircularArc))
      {
        double area = SignedArea(primitives);
        bool shouldBeCounterClockwise = !isChild;
        if ((area > 0D) != shouldBeCounterClockwise)
        {
          primitives.Reverse();
          primitives = primitives.Select(primitive => primitive.Reversed()).ToList();
        }

        primitives = RotateToCanonicalStart(primitives);
      }

      DxfSourcePrimitive[] indexed = primitives
        .Select((primitive, index) => primitive.WithIndex(index))
        .ToArray();
      string fingerprint = Fingerprint(ContourSignature(isChild, isClosed, indexed));
      return new DxfSourceContour(-1, isChild, isClosed, fingerprint, indexed);
    }

    private static List<DxfSourcePrimitive> FromEntity(
      DxfEntity entity,
      double endpointTolerance,
      out bool isClosed)
    {
      switch (entity)
      {
        case DxfLwPolyline lightweight:
          isClosed = lightweight.IsClosed;
          return FromLightweightPolyline(lightweight);
        case DxfPolyline polyline:
          isClosed = polyline.IsClosed;
          return FromPolyline(polyline);
        case DxfLine line:
          isClosed = false;
          return new List<DxfSourcePrimitive>
          {
            DxfSourcePrimitive.Line(Point(line.P1), Point(line.P2)),
          };
        case DxfArc arc:
          isClosed = false;
          return new List<DxfSourcePrimitive> { FromArc(arc) };
        case DxfCircle circle:
          isClosed = true;
          return FromCircle(circle);
        case DxfEllipse ellipse:
          return FromEllipse(ellipse, out isClosed);
        case DxfSpline spline:
          return FromSpline(spline, endpointTolerance, out isClosed);
        default:
          throw new NotSupportedException(
            $"DXF entity {entity?.EntityTypeString ?? "(null)"} cannot be represented in the contour catalog.");
      }
    }

    private static List<DxfSourcePrimitive> FromLightweightPolyline(DxfLwPolyline polyline)
    {
      int count = polyline.Vertices.Count;
      int segmentCount = polyline.IsClosed ? count : Math.Max(0, count - 1);
      var result = new List<DxfSourcePrimitive>(segmentCount);
      for (int i = 0; i < segmentCount; i++)
      {
        DxfLwPolylineVertex a = polyline.Vertices[i];
        DxfLwPolylineVertex b = polyline.Vertices[(i + 1) % count];
        result.Add(FromBulge(
          new CommonLinePoint(a.X, a.Y),
          new CommonLinePoint(b.X, b.Y),
          a.Bulge));
      }

      return result;
    }

    private static List<DxfSourcePrimitive> FromPolyline(DxfPolyline polyline)
    {
      int count = polyline.Vertices.Count;
      int segmentCount = polyline.IsClosed ? count : Math.Max(0, count - 1);
      var result = new List<DxfSourcePrimitive>(segmentCount);
      for (int i = 0; i < segmentCount; i++)
      {
        DxfVertex a = polyline.Vertices[i];
        DxfVertex b = polyline.Vertices[(i + 1) % count];
        result.Add(FromBulge(Point(a.Location), Point(b.Location), a.Bulge));
      }

      return result;
    }

    private static DxfSourcePrimitive FromBulge(
      CommonLinePoint start,
      CommonLinePoint end,
      double bulge)
    {
      if (Math.Abs(bulge) <= 1e-12)
      {
        return DxfSourcePrimitive.Line(start, end);
      }

      double sweep = 4D * Math.Atan(bulge);
      double dx = end.X - start.X;
      double dy = end.Y - start.Y;
      double chord = Math.Sqrt((dx * dx) + (dy * dy));
      double sinHalf = Math.Sin(sweep / 2D);
      if (chord <= 0D || Math.Abs(sinHalf) <= 1e-12)
      {
        throw new InvalidDataException("A DXF bulge describes a degenerate circular arc.");
      }

      double signedRadius = chord / (2D * sinHalf);
      double apothem = signedRadius * Math.Cos(sweep / 2D);
      double centerX = ((start.X + end.X) / 2D) + (apothem * (-dy / chord));
      double centerY = ((start.Y + end.Y) / 2D) + (apothem * (dx / chord));
      var center = new CommonLinePoint(centerX, centerY);
      return DxfSourcePrimitive.CircularArc(
        start,
        end,
        center,
        Math.Abs(signedRadius),
        Math.Atan2(start.Y - centerY, start.X - centerX),
        sweep);
    }

    private static DxfSourcePrimitive FromArc(DxfArc arc)
    {
      double startAngle = DegreesToRadians(arc.StartAngle);
      double sweep = DegreesToRadians(arc.EndAngle - arc.StartAngle);
      while (sweep <= 0D)
      {
        sweep += 2D * Math.PI;
      }

      var center = Point(arc.Center);
      return DxfSourcePrimitive.CircularArc(
        PointOnCircle(center, arc.Radius, startAngle),
        PointOnCircle(center, arc.Radius, startAngle + sweep),
        center,
        arc.Radius,
        startAngle,
        sweep);
    }

    private static List<DxfSourcePrimitive> FromCircle(DxfCircle circle)
    {
      var center = Point(circle.Center);
      var result = new List<DxfSourcePrimitive>(4);
      for (int i = 0; i < 4; i++)
      {
        double start = i * Math.PI / 2D;
        double sweep = Math.PI / 2D;
        result.Add(DxfSourcePrimitive.CircularArc(
          PointOnCircle(center, circle.Radius, start),
          PointOnCircle(center, circle.Radius, start + sweep),
          center,
          circle.Radius,
          start,
          sweep));
      }

      return result;
    }

    private static List<DxfSourcePrimitive> FromEllipse(DxfEllipse ellipse, out bool isClosed)
    {
      double startParameter = ellipse.StartParameter;
      double endParameter = ellipse.EndParameter;
      double sweep = endParameter - startParameter;
      while (sweep <= 0D)
      {
        sweep += 2D * Math.PI;
      }

      isClosed = Math.Abs(sweep - (2D * Math.PI)) <= 1e-10;
      var metadata = new DxfEllipseMetadata(
        Point(ellipse.Center),
        ellipse.MajorAxis.X,
        ellipse.MajorAxis.Y,
        ellipse.MinorAxisRatio,
        startParameter,
        endParameter);
      return new List<DxfSourcePrimitive>
      {
        DxfSourcePrimitive.Ellipse(
          PointOnEllipse(metadata, startParameter),
          PointOnEllipse(metadata, startParameter + sweep),
          metadata),
      };
    }

    private static List<DxfSourcePrimitive> FromSpline(
      DxfSpline spline,
      double endpointTolerance,
      out bool isClosed)
    {
      var metadata = new DxfSplineMetadata(
        spline.DegreeOfCurve,
        spline.IsClosed,
        spline.IsPeriodic,
        spline.IsRational,
        spline.KnotValues.ToArray(),
        spline.ControlPoints.Select(point => new DxfSplineControlPoint(
          Point(point.Point),
          point.Weight)).ToArray(),
        spline.FitPoints.Select(Point).ToArray());
      CommonLinePoint start = metadata.FitPoints.FirstOrDefault()
        ?? metadata.ControlPoints.FirstOrDefault()?.Point;
      CommonLinePoint end = metadata.FitPoints.LastOrDefault()
        ?? metadata.ControlPoints.LastOrDefault()?.Point;
      if (start == null || end == null)
      {
        throw new InvalidDataException("A DXF spline has no usable endpoints.");
      }

      isClosed = spline.IsClosed
        || spline.IsPeriodic
        || SamePoint(start, end, endpointTolerance);
      return new List<DxfSourcePrimitive>
      {
        DxfSourcePrimitive.Spline(start, end, metadata),
      };
    }

    private static List<DxfSourcePrimitive> OrderConnected(
      List<DxfSourcePrimitive> source,
      double endpointTolerance,
      out bool isClosed)
    {
      if (source.Count <= 1)
      {
        isClosed = source.Count == 1
          && SamePoint(source[0].Start, source[0].End, endpointTolerance);
        return source;
      }

      var pending = source.OrderBy(UndirectedSortKey, StringComparer.Ordinal).ToList();
      DxfSourcePrimitive seed = pending[0];
      pending.RemoveAt(0);
      if (PointKey(seed.End).CompareTo(PointKey(seed.Start)) < 0)
      {
        seed = seed.Reversed();
      }

      var ordered = new List<DxfSourcePrimitive> { seed };
      while (pending.Count > 0)
      {
        CommonLinePoint current = ordered[^1].End;
        DxfSourcePrimitive next = pending
          .Where(candidate => SamePoint(candidate.Start, current, endpointTolerance)
            || SamePoint(candidate.End, current, endpointTolerance))
          .OrderBy(UndirectedSortKey, StringComparer.Ordinal)
          .FirstOrDefault();
        if (next == null)
        {
          ordered.AddRange(pending);
          isClosed = false;
          return ordered;
        }

        pending.Remove(next);
        ordered.Add(SamePoint(next.Start, current, endpointTolerance) ? next : next.Reversed());
      }

      isClosed = SamePoint(ordered[^1].End, ordered[0].Start, endpointTolerance);
      return ordered;
    }

    private static List<DxfSourcePrimitive> RotateToCanonicalStart(List<DxfSourcePrimitive> source)
    {
      if (source.Count < 2)
      {
        return source;
      }

      string best = null;
      int bestOffset = 0;
      for (int offset = 0; offset < source.Count; offset++)
      {
        string candidate = string.Join(";", Enumerable.Range(0, source.Count)
          .Select(index => PrimitiveSignature(source[(offset + index) % source.Count])));
        if (best == null || string.CompareOrdinal(candidate, best) < 0)
        {
          best = candidate;
          bestOffset = offset;
        }
      }

      return Enumerable.Range(0, source.Count)
        .Select(index => source[(bestOffset + index) % source.Count])
        .ToList();
    }

    private static double SignedArea(IReadOnlyList<DxfSourcePrimitive> primitives)
    {
      double twiceArea = 0D;
      foreach (DxfSourcePrimitive primitive in primitives)
      {
        if (primitive.GeometryKind == CommonLineGeometryKind.Line)
        {
          twiceArea += (primitive.Start.X * primitive.End.Y) - (primitive.End.X * primitive.Start.Y);
          continue;
        }

        double first = primitive.StartAngleRadians;
        double last = first + primitive.SweepRadians;
        twiceArea += primitive.Radius * primitive.Center.X * (Math.Sin(last) - Math.Sin(first));
        twiceArea -= primitive.Radius * primitive.Center.Y * (Math.Cos(last) - Math.Cos(first));
        twiceArea += primitive.Radius * primitive.Radius * primitive.SweepRadians;
      }

      return twiceArea / 2D;
    }

    private static string ContourSignature(
      bool isChild,
      bool isClosed,
      IReadOnlyList<DxfSourcePrimitive> primitives)
      => FormattableString.Invariant(
        $"child={isChild};closed={isClosed};{string.Join(";", primitives.Select(PrimitiveSignature))}");

    private static string PrimitiveSignature(DxfSourcePrimitive primitive)
    {
      var signature = new StringBuilder();
      signature.Append((int)primitive.GeometryKind).Append('|');
      AppendPoint(signature, primitive.Start);
      AppendPoint(signature, primitive.End);
      AppendPoint(signature, primitive.Center);
      AppendDouble(signature, primitive.Radius);
      AppendDouble(signature, primitive.StartAngleRadians);
      AppendDouble(signature, primitive.SweepRadians);
      if (primitive.GeometryKind == CommonLineGeometryKind.Ellipse
          || primitive.GeometryKind == CommonLineGeometryKind.Spline)
      {
        signature.Append(primitive.IsReversed ? '1' : '0');
      }
      if (primitive.EllipseMetadata != null)
      {
        DxfEllipseMetadata ellipse = primitive.EllipseMetadata;
        AppendPoint(signature, ellipse.Center);
        AppendDouble(signature, ellipse.MajorAxisX);
        AppendDouble(signature, ellipse.MajorAxisY);
        AppendDouble(signature, ellipse.MinorAxisRatio);
        AppendDouble(signature, ellipse.StartParameter);
        AppendDouble(signature, ellipse.EndParameter);
      }

      if (primitive.SplineMetadata != null)
      {
        DxfSplineMetadata spline = primitive.SplineMetadata;
        signature.Append('|').Append(spline.DegreeOfCurve)
          .Append('|').Append(spline.IsClosed)
          .Append('|').Append(spline.IsPeriodic)
          .Append('|').Append(spline.IsRational);
        foreach (double knot in spline.KnotValues)
        {
          AppendDouble(signature, knot);
        }

        foreach (DxfSplineControlPoint control in spline.ControlPoints)
        {
          AppendPoint(signature, control.Point);
          AppendDouble(signature, control.Weight);
        }

        foreach (CommonLinePoint fit in spline.FitPoints)
        {
          AppendPoint(signature, fit);
        }
      }

      return signature.ToString();
    }

    private static string UndirectedSortKey(DxfSourcePrimitive primitive)
    {
      string first = PointKey(primitive.Start);
      string second = PointKey(primitive.End);
      return string.CompareOrdinal(first, second) <= 0
        ? first + '|' + second + '|' + (int)primitive.GeometryKind
        : second + '|' + first + '|' + (int)primitive.GeometryKind;
    }

    private static string PointKey(CommonLinePoint point)
      => point == null
        ? string.Empty
        : FormattableString.Invariant(
          $"{FingerprintCoordinate(point.X):R},{FingerprintCoordinate(point.Y):R}");

    private static double DefaultEndpointTolerance(DxfHeader header)
    {
      DxfUnits units = header.DefaultDrawingUnits;
      double drawingUnitsPerInch;
      switch (units)
      {
        case DxfUnits.Inches:
        case DxfUnits.USSurveyInch:
          drawingUnitsPerInch = 1D;
          break;
        case DxfUnits.Feet:
        case DxfUnits.USSurveyFeet:
          drawingUnitsPerInch = 1D / 12D;
          break;
        case DxfUnits.Yards:
        case DxfUnits.USSurveyYard:
          drawingUnitsPerInch = 1D / 36D;
          break;
        case DxfUnits.Miles:
        case DxfUnits.USSurveyMile:
          drawingUnitsPerInch = 1D / 63360D;
          break;
        case DxfUnits.Millimeters:
          drawingUnitsPerInch = CommonLineDefaults.InchesToMillimeters;
          break;
        case DxfUnits.Centimeters:
          drawingUnitsPerInch = 2.54D;
          break;
        case DxfUnits.Meters:
          drawingUnitsPerInch = 0.0254D;
          break;
        case DxfUnits.Kilometers:
          drawingUnitsPerInch = 0.0000254D;
          break;
        case DxfUnits.Microinches:
          drawingUnitsPerInch = 1000000D;
          break;
        case DxfUnits.Mils:
          drawingUnitsPerInch = 1000D;
          break;
        case DxfUnits.Angstroms:
          drawingUnitsPerInch = 254000000D;
          break;
        case DxfUnits.Nanometers:
          drawingUnitsPerInch = 25400000D;
          break;
        case DxfUnits.Microns:
          drawingUnitsPerInch = 25400D;
          break;
        case DxfUnits.Decimeters:
          drawingUnitsPerInch = 0.254D;
          break;
        case DxfUnits.Decameters:
          drawingUnitsPerInch = 0.00254D;
          break;
        case DxfUnits.Hectometers:
          drawingUnitsPerInch = 0.000254D;
          break;
        case DxfUnits.Gigameters:
          drawingUnitsPerInch = 2.54e-11D;
          break;
        default:
          // Unitless files still carry the coarse $MEASUREMENT hint. It is the only safe signal
          // available, so treat metric unitless drawings as millimetres and English as inches.
          drawingUnitsPerInch = header.DrawingUnits == DxfDrawingUnits.Metric
            ? CommonLineDefaults.InchesToMillimeters
            : 1D;
          break;
      }

      return CommonLineDefaults.DefaultGeometryToleranceInches * drawingUnitsPerInch;
    }

    private static double ValidateEndpointTolerance(double endpointTolerance)
    {
      if (!double.IsFinite(endpointTolerance) || endpointTolerance <= 0D)
      {
        throw new ArgumentOutOfRangeException(
          nameof(endpointTolerance),
          "The DXF endpoint tolerance must be a finite positive distance.");
      }

      return endpointTolerance;
    }

    private static long EndpointToleranceCacheKey(double? endpointTolerance)
      => endpointTolerance.HasValue
        ? BitConverter.DoubleToInt64Bits(endpointTolerance.Value)
        : long.MinValue;

    /// <summary>
    /// Verifies that every entity represented by this catalog is already expressed in the world XY
    /// plane. The catalog deliberately does not project OCS geometry: doing so partially would make
    /// its exact primitives disagree with the polygon used for nesting. The returned version marker
    /// participates in the source fingerprint so catalogs created before this planar contract cannot
    /// be mistaken for catalogs created after it.
    /// </summary>
    private static string ValidatePlanarGeometry(IReadOnlyList<DxfEntity> entities)
    {
      foreach (DxfEntity entity in entities)
      {
        if (entity == null)
        {
          throw new InvalidDataException("A DXF contour contains a null geometry entity.");
        }

        switch (entity)
        {
          case DxfLine line:
            ValidatePoint(line.P1, "LINE start point");
            ValidatePoint(line.P2, "LINE end point");
            ValidateZero(line.Elevation, "LINE elevation");
            ValidateZero(line.Thickness, "LINE thickness");
            ValidateWorldNormal(line.ExtrusionDirection, "LINE extrusion direction");
            break;
          case DxfArc arc:
            ValidatePoint(arc.Center, "ARC center");
            ValidatePositive(arc.Radius, "ARC radius");
            ValidateAngularPair(arc.StartAngle, arc.EndAngle, "ARC");
            ValidateCircularExtent(arc.Center, arc.Radius, "ARC");
            ValidateZero(arc.Elevation, "ARC elevation");
            ValidateZero(arc.Thickness, "ARC thickness");
            ValidateWorldNormal(arc.Normal, "ARC normal");
            break;
          case DxfCircle circle:
            ValidatePoint(circle.Center, "CIRCLE center");
            ValidatePositive(circle.Radius, "CIRCLE radius");
            ValidateCircularExtent(circle.Center, circle.Radius, "CIRCLE");
            ValidateZero(circle.Elevation, "CIRCLE elevation");
            ValidateZero(circle.Thickness, "CIRCLE thickness");
            ValidateWorldNormal(circle.Normal, "CIRCLE normal");
            break;
          case DxfLwPolyline lightweight:
            ValidateZero(lightweight.Elevation, "LWPOLYLINE elevation");
            ValidateZero(lightweight.Thickness, "LWPOLYLINE thickness");
            ValidateWorldNormal(lightweight.ExtrusionDirection, "LWPOLYLINE extrusion direction");
            foreach (DxfLwPolylineVertex vertex in lightweight.Vertices)
            {
              ValidateFinite(vertex.X, "LWPOLYLINE vertex X");
              ValidateFinite(vertex.Y, "LWPOLYLINE vertex Y");
              ValidateFinite(vertex.Bulge, "LWPOLYLINE vertex bulge");
            }

            break;
          case DxfPolyline polyline:
            if (polyline.Is3DPolyline || polyline.Is3DPolygonMesh || polyline.IsPolyfaceMesh)
            {
              throw NonPlanar("POLYLINE 3D/mesh flags");
            }

            ValidatePoint(polyline.Location, "POLYLINE location");
            ValidateZero(polyline.Elevation, "POLYLINE elevation");
            ValidateZero(polyline.Thickness, "POLYLINE thickness");
            ValidateWorldNormal(polyline.Normal, "POLYLINE normal");
            foreach (DxfVertex vertex in polyline.Vertices)
            {
              if (vertex.Is3DPolylineVertex
                  || vertex.Is3DPolygonMesh
                  || vertex.IsPolyfaceMeshVertex)
              {
                throw NonPlanar("POLYLINE vertex 3D/mesh flags");
              }

              ValidatePoint(vertex.Location, "POLYLINE vertex");
              ValidateZero(vertex.Elevation, "POLYLINE vertex elevation");
              ValidateFinite(vertex.Bulge, "POLYLINE vertex bulge");
            }

            break;
          case DxfEllipse ellipse:
            ValidatePoint(ellipse.Center, "ELLIPSE center");
            ValidatePlanarVector(ellipse.MajorAxis, "ELLIPSE major axis");
            ValidateEllipse(ellipse);
            ValidateZero(ellipse.Elevation, "ELLIPSE elevation");
            ValidateWorldNormal(ellipse.Normal, "ELLIPSE normal");
            break;
          case DxfSpline spline:
            ValidateZero(spline.Elevation, "SPLINE elevation");
            ValidateWorldNormal(spline.Normal, "SPLINE normal");
            ValidatePoint(spline.StartTangent, "SPLINE start tangent");
            ValidatePoint(spline.EndTangent, "SPLINE end tangent");
            foreach (DxfControlPoint controlPoint in spline.ControlPoints)
            {
              ValidatePoint(controlPoint.Point, "SPLINE control point");
              ValidateFinite(controlPoint.Weight, "SPLINE control-point weight");
            }

            foreach (DxfPoint fitPoint in spline.FitPoints)
            {
              ValidatePoint(fitPoint, "SPLINE fit point");
            }

            foreach (double knot in spline.KnotValues)
            {
              ValidateFinite(knot, "SPLINE knot");
            }

            break;
        }
      }

      return "WORLD-XY-PLANAR-V1";
    }

    private static void ValidatePoint(DxfPoint point, string context)
    {
      ValidateFinite(point.X, context + " X");
      ValidateFinite(point.Y, context + " Y");
      ValidateZero(point.Z, context + " Z");
    }

    private static void ValidatePlanarVector(DxfVector vector, string context)
    {
      ValidateFinite(vector.X, context + " X");
      ValidateFinite(vector.Y, context + " Y");
      ValidateZero(vector.Z, context + " Z");
    }

    private static void ValidateWorldNormal(DxfVector normal, string context)
    {
      ValidateFinite(normal.X, context + " X");
      ValidateFinite(normal.Y, context + " Y");
      ValidateFinite(normal.Z, context + " Z");
      if (Math.Abs(normal.X) > PlanarityTolerance
          || Math.Abs(normal.Y) > PlanarityTolerance
          || normal.Z <= PlanarityTolerance)
      {
        throw NonPlanar(context);
      }
    }

    private static void ValidateZero(double value, string context)
    {
      ValidateFinite(value, context);
      if (Math.Abs(value) > PlanarityTolerance)
      {
        throw NonPlanar(context);
      }
    }

    private static void ValidateFinite(double value, string context)
    {
      if (!double.IsFinite(value))
      {
        throw new InvalidDataException(
          $"{context} is not finite; common-line geometry requires finite world XY-plane coordinates.");
      }
    }

    private static void ValidatePositive(double value, string context)
    {
      ValidateFinite(value, context);
      if (value <= 0D)
      {
        throw new InvalidDataException($"{context} must be positive for common-line geometry.");
      }
    }

    private static void ValidateAngularPair(double start, double end, string context)
    {
      ValidateFinite(start, context + " start angle");
      ValidateFinite(end, context + " end angle");
      double sweep = end - start;
      ValidateFinite(sweep, context + " angular sweep");
      ValidateFinite(DegreesToRadians(start), context + " start angle in radians");
      ValidateFinite(DegreesToRadians(sweep), context + " angular sweep in radians");
    }

    private static void ValidateCircularExtent(DxfPoint center, double radius, string context)
    {
      // A finite center and radius can still overflow when PointOnCircle adds them.  Reject that
      // range before any trigonometric reconstruction so generated primitive endpoints stay finite.
      if (Math.Abs(center.X) > double.MaxValue - radius
          || Math.Abs(center.Y) > double.MaxValue - radius)
      {
        throw new InvalidDataException($"{context} center/radius exceeds the finite coordinate range.");
      }
    }

    private static void ValidateEllipse(DxfEllipse ellipse)
    {
      double majorX = ellipse.MajorAxis.X;
      double majorY = ellipse.MajorAxis.Y;
      double axisScale = Math.Max(Math.Abs(majorX), Math.Abs(majorY));
      if (axisScale <= 0D)
      {
        throw new InvalidDataException("ELLIPSE major axis must have positive length.");
      }

      double normalizedLength = Math.Sqrt(
        ((majorX / axisScale) * (majorX / axisScale))
        + ((majorY / axisScale) * (majorY / axisScale)));
      double majorLength = axisScale * normalizedLength;
      ValidatePositive(majorLength, "ELLIPSE major-axis length");
      ValidateFinite(ellipse.MinorAxisRatio, "ELLIPSE minor-axis ratio");
      if (ellipse.MinorAxisRatio <= 0D || ellipse.MinorAxisRatio > 1D)
      {
        throw new InvalidDataException("ELLIPSE minor-axis ratio must satisfy 0 < ratio <= 1.");
      }

      ValidateFinite(ellipse.StartParameter, "ELLIPSE start parameter");
      ValidateFinite(ellipse.EndParameter, "ELLIPSE end parameter");
      double sweep = ellipse.EndParameter - ellipse.StartParameter;
      ValidateFinite(sweep, "ELLIPSE parameter sweep");

      double minorExtentX = Math.Abs(majorY * ellipse.MinorAxisRatio);
      double minorExtentY = Math.Abs(majorX * ellipse.MinorAxisRatio);
      ValidateFinite(minorExtentX, "ELLIPSE minor-axis X extent");
      ValidateFinite(minorExtentY, "ELLIPSE minor-axis Y extent");
      if (Math.Abs(ellipse.Center.X) > double.MaxValue - Math.Abs(majorX)
          || Math.Abs(ellipse.Center.X) + Math.Abs(majorX) > double.MaxValue - minorExtentX
          || Math.Abs(ellipse.Center.Y) > double.MaxValue - Math.Abs(majorY)
          || Math.Abs(ellipse.Center.Y) + Math.Abs(majorY) > double.MaxValue - minorExtentY)
      {
        throw new InvalidDataException("ELLIPSE axes exceed the finite coordinate range.");
      }
    }

    private static InvalidDataException NonPlanar(string context)
      => new InvalidDataException(
        $"{context} is outside the world XY plane. Common-line geometry cannot safely project elevation, 3D, or non-default OCS geometry.");

    private static void AppendPoint(StringBuilder builder, CommonLinePoint point)
    {
      if (point == null)
      {
        builder.Append("null|");
        return;
      }

      AppendDouble(builder, FingerprintCoordinate(point.X));
      AppendDouble(builder, FingerprintCoordinate(point.Y));
    }

    private static double FingerprintCoordinate(double value)
    {
      double snapped = Math.Round(
        value / FingerprintCoordinateTolerance,
        MidpointRounding.AwayFromZero) * FingerprintCoordinateTolerance;
      return Math.Abs(snapped) < FingerprintCoordinateTolerance ? 0D : snapped;
    }

    private static void AppendDouble(StringBuilder builder, double value)
    {
      // Equivalent DXF chains can acquire sub-picometre trigonometric noise when a primitive is
      // reversed and re-oriented. That noise is not geometry identity and must not make a persisted
      // source fingerprint depend on entity order.
      double normalized = double.IsFinite(value)
        ? Math.Round(value, 12, MidpointRounding.AwayFromZero)
        : value;
      if (normalized == 0D)
      {
        normalized = 0D;
      }

      builder.Append(normalized.ToString("R", CultureInfo.InvariantCulture)).Append('|');
    }

    private static string Fingerprint(string value)
    {
      byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
      return Convert.ToHexString(bytes);
    }

    private static CommonLinePoint Point(DxfPoint point) => new CommonLinePoint(point.X, point.Y);

    private static CommonLinePoint PointOnCircle(
      CommonLinePoint center,
      double radius,
      double angle)
      => new CommonLinePoint(
        center.X + (radius * Math.Cos(angle)),
        center.Y + (radius * Math.Sin(angle)));

    private static CommonLinePoint PointOnEllipse(DxfEllipseMetadata ellipse, double parameter)
    {
      double cosine = Math.Cos(parameter);
      double sine = Math.Sin(parameter);
      double x = ellipse.Center.X + (ellipse.MajorAxisX * cosine)
        - (ellipse.MajorAxisY * ellipse.MinorAxisRatio * sine);
      double y = ellipse.Center.Y + (ellipse.MajorAxisY * cosine)
        + (ellipse.MajorAxisX * ellipse.MinorAxisRatio * sine);
      return new CommonLinePoint(x, y);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180D;

    private static bool SamePoint(
      CommonLinePoint first,
      CommonLinePoint second,
      double endpointTolerance)
      => first != null
        && second != null
        && Math.Abs(first.X - second.X) <= endpointTolerance
        && Math.Abs(first.Y - second.Y) <= endpointTolerance;

    private readonly struct CatalogCacheKey : IEquatable<CatalogCacheKey>
    {
      public CatalogCacheKey(
        string path,
        string contentFingerprint,
        long endpointToleranceKey)
      {
        this.Path = path;
        this.ContentFingerprint = contentFingerprint;
        this.EndpointToleranceKey = endpointToleranceKey;
      }

      public string Path { get; }

      public string ContentFingerprint { get; }

      public long EndpointToleranceKey { get; }

      public bool Equals(CatalogCacheKey other)
        => string.Equals(this.ContentFingerprint, other.ContentFingerprint, StringComparison.Ordinal)
          && string.Equals(this.Path, other.Path, StringComparison.OrdinalIgnoreCase)
          && this.EndpointToleranceKey == other.EndpointToleranceKey;

      public override bool Equals(object obj)
        => obj is CatalogCacheKey other && this.Equals(other);

      public override int GetHashCode()
        => HashCode.Combine(
          StringComparer.OrdinalIgnoreCase.GetHashCode(this.Path),
          StringComparer.Ordinal.GetHashCode(this.ContentFingerprint),
          this.EndpointToleranceKey);
    }
  }

  /// <summary>One closed or explicitly-open source contour and its deterministic primitive order.</summary>
  internal sealed class DxfSourceSnapshot
  {
    private readonly byte[] sourceBytes;

    internal DxfSourceSnapshot(
      string path,
      byte[] sourceBytes,
      DxfContourCatalog catalog)
    {
      this.Path = path ?? throw new ArgumentNullException(nameof(path));
      this.sourceBytes = sourceBytes?.ToArray() ?? throw new ArgumentNullException(nameof(sourceBytes));
      this.Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    internal string Path { get; }

    internal DxfContourCatalog Catalog { get; }

    internal DxfFile OpenFile()
    {
      using var stream = new MemoryStream(this.sourceBytes, writable: false);
      return DxfFile.Load(stream);
    }
  }

  public sealed class DxfSourceContour
  {
    internal DxfSourceContour(
      int index,
      bool isChild,
      bool isClosed,
      string fingerprint,
      IReadOnlyList<DxfSourcePrimitive> primitives)
    {
      this.Index = index;
      this.IsChild = isChild;
      this.IsClosed = isClosed;
      this.Fingerprint = fingerprint;
      this.Primitives = new ReadOnlyCollection<DxfSourcePrimitive>(primitives.ToArray());
    }

    public int Index { get; }

    public bool IsChild { get; }

    public bool IsClosed { get; }

    public string Fingerprint { get; }

    public IReadOnlyList<DxfSourcePrimitive> Primitives { get; }

    internal DxfSourceContour WithIndex(int index)
      => new DxfSourceContour(index, this.IsChild, this.IsClosed, this.Fingerprint, this.Primitives);
  }

  /// <summary>An immutable primitive in an ordered source contour.</summary>
  public sealed class DxfSourcePrimitive
  {
    private DxfSourcePrimitive(
      int index,
      CommonLineGeometryKind geometryKind,
      CommonLinePoint start,
      CommonLinePoint end,
      CommonLinePoint center = null,
      double radius = 0D,
      double startAngleRadians = 0D,
      double sweepRadians = 0D,
      DxfEllipseMetadata ellipseMetadata = null,
      DxfSplineMetadata splineMetadata = null,
      bool isReversed = false)
    {
      this.Index = index;
      this.GeometryKind = geometryKind;
      this.Start = start;
      this.End = end;
      this.Center = center;
      this.Radius = radius;
      this.StartAngleRadians = startAngleRadians;
      this.SweepRadians = sweepRadians;
      this.EllipseMetadata = ellipseMetadata;
      this.SplineMetadata = splineMetadata;
      this.IsReversed = isReversed;
    }

    public int Index { get; }

    public CommonLineGeometryKind GeometryKind { get; }

    public CommonLinePoint Start { get; }

    public CommonLinePoint End { get; }

    public CommonLinePoint Center { get; }

    public double Radius { get; }

    public double StartAngleRadians { get; }

    public double SweepRadians { get; }

    public DxfEllipseMetadata EllipseMetadata { get; }

    public DxfSplineMetadata SplineMetadata { get; }

    public bool IsReversed { get; }

    internal static DxfSourcePrimitive Line(CommonLinePoint start, CommonLinePoint end)
      => new DxfSourcePrimitive(-1, CommonLineGeometryKind.Line, start, end);

    internal static DxfSourcePrimitive CircularArc(
      CommonLinePoint start,
      CommonLinePoint end,
      CommonLinePoint center,
      double radius,
      double startAngleRadians,
      double sweepRadians)
      => new DxfSourcePrimitive(
        -1,
        CommonLineGeometryKind.CircularArc,
        start,
        end,
        center,
        radius,
        startAngleRadians,
        sweepRadians);

    internal static DxfSourcePrimitive Ellipse(
      CommonLinePoint start,
      CommonLinePoint end,
      DxfEllipseMetadata metadata)
      => new DxfSourcePrimitive(
        -1,
        CommonLineGeometryKind.Ellipse,
        start,
        end,
        ellipseMetadata: metadata);

    internal static DxfSourcePrimitive Spline(
      CommonLinePoint start,
      CommonLinePoint end,
      DxfSplineMetadata metadata)
      => new DxfSourcePrimitive(
        -1,
        CommonLineGeometryKind.Spline,
        start,
        end,
        splineMetadata: metadata);

    internal DxfSourcePrimitive WithIndex(int index)
      => new DxfSourcePrimitive(
        index,
        this.GeometryKind,
        this.Start,
        this.End,
        this.Center,
        this.Radius,
        this.StartAngleRadians,
        this.SweepRadians,
        this.EllipseMetadata,
        this.SplineMetadata,
        this.IsReversed);

    internal DxfSourcePrimitive Reversed()
      => new DxfSourcePrimitive(
        this.Index,
        this.GeometryKind,
        this.End,
        this.Start,
        this.Center,
        this.Radius,
        this.GeometryKind == CommonLineGeometryKind.CircularArc
          ? this.StartAngleRadians + this.SweepRadians
          : this.StartAngleRadians,
        this.GeometryKind == CommonLineGeometryKind.CircularArc
          ? -this.SweepRadians
          : this.SweepRadians,
        this.EllipseMetadata,
        this.SplineMetadata,
        !this.IsReversed);
  }

  /// <summary>The exact source parameters of a DXF ELLIPSE.</summary>
  public sealed class DxfEllipseMetadata
  {
    public DxfEllipseMetadata(
      CommonLinePoint center,
      double majorAxisX,
      double majorAxisY,
      double minorAxisRatio,
      double startParameter,
      double endParameter)
    {
      this.Center = center;
      this.MajorAxisX = majorAxisX;
      this.MajorAxisY = majorAxisY;
      this.MinorAxisRatio = minorAxisRatio;
      this.StartParameter = startParameter;
      this.EndParameter = endParameter;
    }

    public CommonLinePoint Center { get; }

    public double MajorAxisX { get; }

    public double MajorAxisY { get; }

    public double MinorAxisRatio { get; }

    public double StartParameter { get; }

    public double EndParameter { get; }
  }

  /// <summary>An immutable NURBS control point and its homogeneous weight.</summary>
  public sealed class DxfSplineControlPoint
  {
    public DxfSplineControlPoint(CommonLinePoint point, double weight)
    {
      this.Point = point;
      this.Weight = weight;
    }

    public CommonLinePoint Point { get; }

    public double Weight { get; }
  }

  /// <summary>The source parameters needed for deterministic adaptive sampling of a DXF SPLINE.</summary>
  public sealed class DxfSplineMetadata
  {
    public DxfSplineMetadata(
      int degreeOfCurve,
      bool isClosed,
      bool isPeriodic,
      bool isRational,
      IReadOnlyList<double> knotValues,
      IReadOnlyList<DxfSplineControlPoint> controlPoints,
      IReadOnlyList<CommonLinePoint> fitPoints)
    {
      this.DegreeOfCurve = degreeOfCurve;
      this.IsClosed = isClosed;
      this.IsPeriodic = isPeriodic;
      this.IsRational = isRational;
      this.KnotValues = new ReadOnlyCollection<double>((knotValues ?? Array.Empty<double>()).ToArray());
      this.ControlPoints = new ReadOnlyCollection<DxfSplineControlPoint>(
        (controlPoints ?? Array.Empty<DxfSplineControlPoint>()).ToArray());
      this.FitPoints = new ReadOnlyCollection<CommonLinePoint>(
        (fitPoints ?? Array.Empty<CommonLinePoint>()).ToArray());
    }

    public int DegreeOfCurve { get; }

    public bool IsClosed { get; }

    public bool IsPeriodic { get; }

    public bool IsRational { get; }

    public IReadOnlyList<double> KnotValues { get; }

    public IReadOnlyList<DxfSplineControlPoint> ControlPoints { get; }

    public IReadOnlyList<CommonLinePoint> FitPoints { get; }
  }
}
