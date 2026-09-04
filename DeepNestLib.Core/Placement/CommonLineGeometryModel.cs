namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Text.Json.Serialization;

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
  /// Records one intentional full-edge relationship made by the nester. Edge indexes address
  /// <c>Part.Points[i] -&gt; Part.Points[(i + 1) % ringLength]</c>; Start and End are the midpoint cut.
  /// </summary>
  public sealed class CommonEdge
  {
    [JsonConstructor]
    public CommonEdge(
      int partAId,
      int partBId,
      int edgeAIndex,
      int edgeBIndex,
      CommonLinePoint start,
      CommonLinePoint end,
      double gap)
    {
      this.PartAId = partAId;
      this.PartBId = partBId;
      this.EdgeAIndex = edgeAIndex;
      this.EdgeBIndex = edgeBIndex;
      this.Start = start;
      this.End = end;
      this.Gap = gap;
    }

    public int PartAId { get; }

    public int PartBId { get; }

    public int EdgeAIndex { get; }

    public int EdgeBIndex { get; }

    public CommonLinePoint Start { get; }

    public CommonLinePoint End { get; }

    public double Gap { get; }
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
    {
      this.PartIds = partIds ?? throw new ArgumentNullException(nameof(partIds));
      this.OuterBoundaries = outerBoundaries ?? throw new ArgumentNullException(nameof(outerBoundaries));
      this.CommonLines = commonLines ?? throw new ArgumentNullException(nameof(commonLines));
    }

    public IReadOnlyList<int> PartIds { get; }

    public IReadOnlyList<CommonLinePath> OuterBoundaries { get; }

    public IReadOnlyList<CommonLineSegment> CommonLines { get; }
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
