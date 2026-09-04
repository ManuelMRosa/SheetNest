namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text.Json.Serialization;

  /// <summary>The analytic source or common-cut primitive used by a boundary span.</summary>
  public enum CommonLineGeometryKind
  {
    /// <summary>A finite straight line segment.</summary>
    Line = 0,

    /// <summary>A circular arc, including a span taken from a DXF CIRCLE.</summary>
    CircularArc = 1,

    /// <summary>An elliptic arc, preserved as source metadata but ineligible as a common seam.</summary>
    Ellipse = 2,

    /// <summary>A NURBS spline, preserved as source metadata but ineligible as a common seam.</summary>
    Spline = 3,
  }

  /// <summary>A serializable point used by deterministic common-line geometry.</summary>
  public sealed class CommonLinePoint
  {
    [JsonConstructor]
    public CommonLinePoint(double x, double y)
    {
      this.X = x;
      this.Y = y;
    }

    public double X { get; }

    public double Y { get; }
  }

  /// <summary>
  /// Identifies a stable parametric interval on one ordered source-contour primitive. Parameters are
  /// normalized to the primitive's directed range: zero is its start and one is its end.
  /// </summary>
  public sealed class CommonBoundarySpan
  {
    [JsonConstructor]
    public CommonBoundarySpan(
      int contourIndex,
      int primitiveIndex,
      double startParameter,
      double endParameter,
      string sourceFingerprint = null,
      string contourFingerprint = null)
    {
      this.ContourIndex = contourIndex;
      this.PrimitiveIndex = primitiveIndex;
      this.StartParameter = startParameter;
      this.EndParameter = endParameter;
      this.SourceFingerprint = sourceFingerprint;
      this.ContourFingerprint = contourFingerprint;
    }

    public int ContourIndex { get; }

    public int PrimitiveIndex { get; }

    public double StartParameter { get; }

    public double EndParameter { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string SourceFingerprint { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string ContourFingerprint { get; }
  }

  /// <summary>
  /// Describes the resolved center cut for a parametric common edge. Lines use Start/End; circular arcs
  /// additionally use Center/Radius/angles. Ellipses and splines are deliberately not emitted as
  /// sampled common cuts: they remain ineligible until an exact analytic representation exists.
  /// </summary>
  public sealed class CommonCenterCurve
  {
    [JsonConstructor]
    public CommonCenterCurve(
      CommonLineGeometryKind geometryKind,
      CommonLinePoint start,
      CommonLinePoint end,
      CommonLinePoint center = null,
      double radius = 0D,
      double startAngleRadians = 0D,
      double sweepRadians = 0D)
    {
      this.GeometryKind = geometryKind;
      this.Start = start;
      this.End = end;
      this.Center = center;
      this.Radius = radius;
      this.StartAngleRadians = startAngleRadians;
      this.SweepRadians = sweepRadians;
    }

    public CommonLineGeometryKind GeometryKind { get; }

    public CommonLinePoint Start { get; }

    public CommonLinePoint End { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CommonLinePoint Center { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Radius { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double StartAngleRadians { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double SweepRadians { get; }
  }

  /// <summary>
  /// Records one intentional common-edge relationship made by the nester. Legacy records address a
  /// complete tessellated edge by index; schema-two records address exact parametric source spans.
  /// Start and End retain the resolved midpoint cut for compatibility and fail-closed revalidation.
  /// </summary>
  public sealed class CommonEdge
  {
    public const int LegacySchemaVersion = 0;

    public const int ParametricSchemaVersion = 2;

    [JsonConstructor]
    public CommonEdge(
      int partAId,
      int partBId,
      int edgeAIndex,
      int edgeBIndex,
      CommonLinePoint start,
      CommonLinePoint end,
      double gap,
      int schemaVersion = LegacySchemaVersion,
      CommonLineGeometryKind geometryKind = CommonLineGeometryKind.Line,
      CommonBoundarySpan spanA = null,
      CommonBoundarySpan spanB = null,
      CommonCenterCurve centerCurve = null)
    {
      this.PartAId = partAId;
      this.PartBId = partBId;
      this.EdgeAIndex = edgeAIndex;
      this.EdgeBIndex = edgeBIndex;
      this.Start = start;
      this.End = end;
      this.Gap = gap;
      this.SchemaVersion = schemaVersion;
      this.GeometryKind = geometryKind;
      this.SpanA = spanA;
      this.SpanB = spanB;
      this.CenterCurve = centerCurve;
    }

    public int PartAId { get; }

    public int PartBId { get; }

    public int EdgeAIndex { get; }

    public int EdgeBIndex { get; }

    public CommonLinePoint Start { get; }

    public CommonLinePoint End { get; }

    public double Gap { get; }

    /// <summary>Zero/missing is the legacy full-straight-edge contract; two is parametric.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SchemaVersion { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public CommonLineGeometryKind GeometryKind { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CommonBoundarySpan SpanA { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CommonBoundarySpan SpanB { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CommonCenterCurve CenterCurve { get; }
  }

  /// <summary>An undirected line segment in the resolved common-cut graph.</summary>
  public sealed class CommonLineSegment
  {
    public CommonLineSegment(CommonLinePoint start, CommonLinePoint end)
    {
      this.Start = start;
      this.End = end;
    }

    public CommonLinePoint Start { get; }

    public CommonLinePoint End { get; }
  }

  /// <summary>A closed external cluster boundary.</summary>
  public sealed class CommonLinePath
  {
    public CommonLinePath(IReadOnlyList<CommonLinePoint> points, bool isClosed)
      : this(points, isClosed, null)
    {
    }

    /// <summary>
    /// Initializes a boundary whose bulges follow the DXF LWPOLYLINE convention: entry <c>i</c>
    /// describes the directed segment from point <c>i</c> to the next point (wrapping when closed).
    /// </summary>
    public CommonLinePath(
      IReadOnlyList<CommonLinePoint> points,
      bool isClosed,
      IReadOnlyList<double> bulges)
    {
      if (points == null)
      {
        throw new ArgumentNullException(nameof(points));
      }

      this.IsClosed = isClosed;
      int segmentCount = isClosed ? points.Count : Math.Max(0, points.Count - 1);
      if (bulges != null && bulges.Count != segmentCount)
      {
        throw new ArgumentException("A common-line path must have one bulge per directed segment.", nameof(bulges));
      }

      this.Points = points.ToArray();
      this.Bulges = bulges?.ToArray() ?? new double[segmentCount];
    }

    public IReadOnlyList<CommonLinePoint> Points { get; }

    public bool IsClosed { get; }

    public IReadOnlyList<double> Bulges { get; }
  }

  /// <summary>A connected component of parts joined by intentional common edges.</summary>
  public sealed class CommonLineCluster
  {
    public CommonLineCluster(
      IReadOnlyList<int> partIds,
      IReadOnlyList<CommonLinePath> outerBoundaries,
      IReadOnlyList<CommonLineSegment> commonLines)
      : this(partIds, outerBoundaries, Array.Empty<CommonLinePath>(), commonLines, null)
    {
    }

    public CommonLineCluster(
      IReadOnlyList<int> partIds,
      IReadOnlyList<CommonLinePath> outerBoundaries,
      IReadOnlyList<CommonLineSegment> commonLines,
      IReadOnlyList<CommonCenterCurve> commonCurves)
      : this(partIds, outerBoundaries, Array.Empty<CommonLinePath>(), commonLines, commonCurves)
    {
    }

    public CommonLineCluster(
      IReadOnlyList<int> partIds,
      IReadOnlyList<CommonLinePath> outerBoundaries,
      IReadOnlyList<CommonLinePath> innerBoundaries,
      IReadOnlyList<CommonLineSegment> commonLines,
      IReadOnlyList<CommonCenterCurve> commonCurves)
    {
      this.PartIds = partIds ?? throw new ArgumentNullException(nameof(partIds));
      this.OuterBoundaries = outerBoundaries ?? throw new ArgumentNullException(nameof(outerBoundaries));
      this.InnerBoundaries = innerBoundaries ?? throw new ArgumentNullException(nameof(innerBoundaries));
      this.CommonLines = commonLines?.ToArray()
        ?? (commonCurves ?? Array.Empty<CommonCenterCurve>())
          .Where(curve => curve?.GeometryKind == CommonLineGeometryKind.Line)
          .Select(curve => new CommonLineSegment(curve.Start, curve.End))
          .ToArray();
      this.CommonCurves = commonCurves?.ToArray()
        ?? this.CommonLines
          .Select(line => new CommonCenterCurve(CommonLineGeometryKind.Line, line.Start, line.End))
          .ToArray();
    }

    public IReadOnlyList<int> PartIds { get; }

    public IReadOnlyList<CommonLinePath> OuterBoundaries { get; }

    /// <summary>
    /// Closed voids formed by the external boundaries of multiple cluster members. These are cutting
    /// holes, not additional outside islands, and therefore belong on CUT_INNER.
    /// </summary>
    public IReadOnlyList<CommonLinePath> InnerBoundaries { get; }

    public IReadOnlyList<CommonLineSegment> CommonLines { get; }

    /// <summary>
    /// Runtime cutting primitives. Legacy straight seams are projected into this collection, while
    /// circular common seams can coexist without pretending to be their endpoint chord.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<CommonCenterCurve> CommonCurves { get; }
  }

  /// <summary>The fully validated cutting geometry for every common-line component on a sheet.</summary>
  public sealed class CommonLineGeometryResult
  {
    public CommonLineGeometryResult(IReadOnlyList<CommonLineCluster> clusters)
    {
      this.Clusters = clusters ?? throw new ArgumentNullException(nameof(clusters));
    }

    public IReadOnlyList<CommonLineCluster> Clusters { get; }
  }

  /// <summary>Signals that an intentional common-line placement is unsafe to export.</summary>
  public sealed class CommonLineValidationException : InvalidOperationException
  {
    public CommonLineValidationException(string message)
      : base(message)
    {
    }
  }
}
