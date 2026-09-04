namespace DeepNestLib.CiTests.Placement
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using Xunit;

  public class CommonLineCurveNetworkFixture
  {
    private const double Tolerance = 0.0001D;

    [Fact]
    public void CrossingLinesUseOneExactFourWayNode()
    {
      var input = new[]
      {
        Line(-5D, 0D, 5D, 0D),
        Line(0D, -5D, 0D, 5D),
      };

      var result = CommonLineCurveNetwork.Build(input, 0.01D, Tolerance);

      result.Should().HaveCount(4);
      result.SelectMany(curve => new[] { curve.Start, curve.End })
        .Count(point => Math.Abs(point.X) < Tolerance && Math.Abs(point.Y) < Tolerance)
        .Should().Be(4);
    }

    [Fact]
    public void LineArcIntersectionSplitsBothExactPrimitives()
    {
      var arc = new CommonCenterCurve(
        CommonLineGeometryKind.CircularArc,
        new CommonLinePoint(5D, 0D),
        new CommonLinePoint(-5D, 0D),
        new CommonLinePoint(0D, 0D),
        5D,
        0D,
        Math.PI);
      var input = new[] { arc, Line(0D, -1D, 0D, 6D) };

      var result = CommonLineCurveNetwork.Build(input, 0.01D, Tolerance);

      result.Count(curve => curve.GeometryKind == CommonLineGeometryKind.Line).Should().Be(2);
      result.Count(curve => curve.GeometryKind == CommonLineGeometryKind.CircularArc).Should().Be(2);
      result.Where(curve => curve.GeometryKind == CommonLineGeometryKind.CircularArc)
        .Sum(curve => curve.SweepRadians)
        .Should().BeApproximately(Math.PI, Tolerance);

      CommonLinePoint[] lineNodes = result
        .Where(curve => curve.GeometryKind == CommonLineGeometryKind.Line)
        .SelectMany(curve => new[] { curve.Start, curve.End })
        .Where(point => Math.Abs(point.X) < Tolerance && Math.Abs(point.Y - 5D) < Tolerance)
        .ToArray();
      CommonLinePoint[] arcNodes = result
        .Where(curve => curve.GeometryKind == CommonLineGeometryKind.CircularArc)
        .SelectMany(curve => new[] { curve.Start, curve.End })
        .Where(point => Math.Abs(point.X) < Tolerance && Math.Abs(point.Y - 5D) < Tolerance)
        .ToArray();
      lineNodes.Should().HaveCount(2);
      arcNodes.Should().HaveCount(2);
      lineNodes.Concat(arcNodes).Should().OnlyContain(
        point => point.X == lineNodes[0].X && point.Y == lineNodes[0].Y,
        "all graph edges must carry the exact same canonical node, not merely nearby coordinates");
    }

    [Fact]
    public void DuplicateAndAdjacentCircularArcsBecomeOnePath()
    {
      CommonLinePoint center = new CommonLinePoint(2D, 3D);
      var first = Arc(center, 4D, 0D, Math.PI / 2D);
      var duplicate = Arc(center, 4D, 0D, Math.PI / 2D);
      var adjacent = Arc(center, 4D, Math.PI / 2D, Math.PI / 2D);

      CommonCenterCurve result = CommonLineCurveNetwork.Build(
        new[] { first, duplicate, adjacent },
        0.01D,
        Tolerance).Should().ContainSingle().Subject;

      result.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      result.SweepRadians.Should().BeApproximately(Math.PI, Tolerance);
    }

    [Fact]
    public void ArcEndpointAndLineEndpointUseTheSameExactNode()
    {
      CommonLinePoint center = new CommonLinePoint(0D, 0D);
      var input = new[]
      {
        Arc(center, 5D, 0D, Math.PI / 2D),
        Line(0D, 5D, 0D, 8D),
      };

      var result = CommonLineCurveNetwork.Build(
        input,
        0.01D,
        Tolerance);

      CommonLinePoint lineNode = result.Single(curve => curve.GeometryKind == CommonLineGeometryKind.Line).Start;
      CommonLinePoint arcNode = result.Single(curve => curve.GeometryKind == CommonLineGeometryKind.CircularArc).End;
      arcNode.X.Should().Be(lineNode.X);
      arcNode.Y.Should().Be(lineNode.Y);
    }

    [Fact]
    public void IsolatedCollinearSeamsDoNotBridgeAnUnsupportedGap()
    {
      var input = new[]
      {
        Line(0D, 0D, 4D, 0D),
        Line(4.01D, 0D, 8D, 0D),
      };

      IReadOnlyList<CommonCenterCurve> result = CommonLineCurveNetwork.Build(
        input,
        bridgeDistance: 0.01D,
        Tolerance);

      result.Should().HaveCount(2,
        "proximity alone does not prove that the empty interval is cutting geometry");
    }

    [Fact]
    public void FourHalfSeamsBridgeIntoOneExactFourWayNode()
    {
      const double halfGap = 0.005D;
      var input = new[]
      {
        Line(-5D, 0D, -halfGap, 0D),
        Line(halfGap, 0D, 5D, 0D),
        Line(0D, -5D, 0D, -halfGap),
        Line(0D, halfGap, 0D, 5D),
      };

      IReadOnlyList<CommonCenterCurve> result = CommonLineCurveNetwork.Build(
        input,
        bridgeDistance: 0.01D,
        Tolerance);

      result.Should().HaveCount(4);
      CommonLinePoint[] centerNodes = result
        .SelectMany(curve => new[] { curve.Start, curve.End })
        .Where(point => Math.Abs(point.X) <= Tolerance && Math.Abs(point.Y) <= Tolerance)
        .ToArray();
      centerNodes.Should().HaveCount(4);
      centerNodes.Should().OnlyContain(point => ReferenceEquals(point, centerNodes[0]),
        "both mutually-supported bridges must split at one canonical + node");
    }

    [Fact]
    public void PerpendicularOriginalArmAuthorizesAThreeArmTJunction()
    {
      const double halfGap = 0.005D;
      var input = new[]
      {
        Line(-5D, 0D, -halfGap, 0D),
        Line(halfGap, 0D, 5D, 0D),
        Line(0D, -5D, 0D, 0D),
      };

      IReadOnlyList<CommonCenterCurve> result = CommonLineCurveNetwork.Build(
        input,
        bridgeDistance: 0.01D,
        Tolerance);

      result.Should().HaveCount(3);
      CommonLinePoint[] centerNodes = result
        .SelectMany(curve => new[] { curve.Start, curve.End })
        .Where(point => Math.Abs(point.X) <= Tolerance && Math.Abs(point.Y) <= Tolerance)
        .ToArray();
      centerNodes.Should().HaveCount(3);
      centerNodes.Should().OnlyContain(point => ReferenceEquals(point, centerNodes[0]));
    }

    [Fact]
    public void NearbyNonIntersectingArcDoesNotAuthorizeABridge()
    {
      const double halfGap = 0.005D;
      var nearbyArc = Arc(
        new CommonLinePoint(0D, -1.001D),
        1D,
        0D,
        Math.PI);
      var input = new[]
      {
        Line(-5D, 0D, -halfGap, 0D),
        Line(halfGap, 0D, 5D, 0D),
        nearbyArc,
      };

      IReadOnlyList<CommonCenterCurve> result = CommonLineCurveNetwork.Build(
        input,
        bridgeDistance: 0.01D,
        Tolerance);

      result.Count(curve => curve.GeometryKind == CommonLineGeometryKind.Line).Should().Be(2);
      result.Should().NotContain(curve =>
        curve.GeometryKind == CommonLineGeometryKind.Line
        && Math.Min(curve.Start.X, curve.End.X) < 0D
        && Math.Max(curve.Start.X, curve.End.X) > 0D);
    }

    [Fact]
    public void IntersectingOriginalArcAuthorizesABridge()
    {
      const double halfGap = 0.005D;
      var crossingArc = Arc(
        new CommonLinePoint(0D, -halfGap),
        halfGap,
        0D,
        Math.PI);
      var input = new[]
      {
        Line(-5D, 0D, -halfGap, 0D),
        Line(halfGap, 0D, 5D, 0D),
        crossingArc,
      };

      IReadOnlyList<CommonCenterCurve> result = CommonLineCurveNetwork.Build(
        input,
        bridgeDistance: 0.01D,
        Tolerance);

      result.SelectMany(curve => new[] { curve.Start, curve.End })
        .Should().Contain(point => Math.Abs(point.X) <= Tolerance && Math.Abs(point.Y) <= Tolerance,
          "an ARC intersecting the bridge interior is valid transverse evidence");
      result.Should().Contain(curve =>
        curve.GeometryKind == CommonLineGeometryKind.Line
        && Math.Abs(curve.Start.X) <= Tolerance
        && Math.Abs(curve.Start.Y) <= Tolerance);
      result.Should().Contain(curve =>
        curve.GeometryKind == CommonLineGeometryKind.Line
        && Math.Abs(curve.End.X) <= Tolerance
        && Math.Abs(curve.End.Y) <= Tolerance);
    }

    [Fact]
    public void NearTangentLineArcIntersectionIsPhysicallyEquivalentInInchesAndMillimeters()
    {
      const double inchScale = 1D;
      double millimeterScale = CommonLineDefaults.InchesToMillimeters;
      IReadOnlyList<CommonCenterCurve> inch = BuildNearTangentNetwork(
        inchScale,
        Tolerance);
      IReadOnlyList<CommonCenterCurve> metric = BuildNearTangentNetwork(
        millimeterScale,
        Tolerance * millimeterScale);

      inch.Count(curve => curve.GeometryKind == CommonLineGeometryKind.Line).Should().Be(2);
      inch.Count(curve => curve.GeometryKind == CommonLineGeometryKind.CircularArc).Should().Be(2);
      metric.Count(curve => curve.GeometryKind == CommonLineGeometryKind.Line).Should().Be(2);
      metric.Count(curve => curve.GeometryKind == CommonLineGeometryKind.CircularArc).Should().Be(2);

      CommonLinePoint inchNode = JunctionNearestTop(inch);
      CommonLinePoint metricNode = JunctionNearestTop(metric);
      (metricNode.X / millimeterScale).Should().BeApproximately(inchNode.X, Tolerance);
      (metricNode.Y / millimeterScale).Should().BeApproximately(inchNode.Y, Tolerance);
      inch.SelectMany(curve => new[] { curve.Start, curve.End })
        .Count(point => ReferenceEquals(point, inchNode)).Should().Be(4);
      metric.SelectMany(curve => new[] { curve.Start, curve.End })
        .Count(point => ReferenceEquals(point, metricNode)).Should().Be(4);
    }

    [Fact]
    public void PointsInSameQuantizedCellRemainDistinctWhenFartherThanTolerance()
    {
      double delta = 0.49D * Tolerance;
      var firstStart = new CommonLinePoint(delta, delta);
      var secondStart = new CommonLinePoint(-delta, -delta);
      var input = new[]
      {
        new CommonCenterCurve(
          CommonLineGeometryKind.Line,
          firstStart,
          new CommonLinePoint(1D, delta)),
        new CommonCenterCurve(
          CommonLineGeometryKind.Line,
          secondStart,
          new CommonLinePoint(1D, -0.1D - delta)),
      };

      IReadOnlyList<CommonCenterCurve> result = CommonLineCurveNetwork.Build(
        input,
        bridgeDistance: 0D,
        Tolerance);

      result.Should().HaveCount(2);
      result.SelectMany(curve => new[] { curve.Start, curve.End }).Should().Contain(point =>
        point.X == firstStart.X && point.Y == firstStart.Y);
      result.SelectMany(curve => new[] { curve.Start, curve.End }).Should().Contain(point =>
        point.X == secondStart.X && point.Y == secondStart.Y,
          "a shared hash cell is not proof that two nodes are within GeometryTolerance");
    }

    [Fact]
    public void MetricToleranceCanonicalizesPointsAcrossTwoRoundedBuckets()
    {
      const double metricTolerance = 0.00254D;
      const double firstX = 0.001268D;
      double secondX = firstX + (0.9999D * metricTolerance);
      var firstEndpoint = new CommonLinePoint(firstX, 0D);
      var secondEndpoint = new CommonLinePoint(secondX, 0D);
      var input = new[]
      {
        new CommonCenterCurve(
          CommonLineGeometryKind.Line,
          firstEndpoint,
          new CommonLinePoint(-1D, -0.25D)),
        new CommonCenterCurve(
          CommonLineGeometryKind.Line,
          secondEndpoint,
          new CommonLinePoint(0.25D, 1D)),
      };

      IReadOnlyList<CommonCenterCurve> result = CommonLineCurveNetwork.Build(
        input,
        bridgeDistance: 0D,
        metricTolerance);

      CommonLinePoint[] junctions = result
        .SelectMany(curve => new[] { curve.Start, curve.End })
        .Where(point => Math.Abs(point.X - firstX) <= metricTolerance
          && Math.Abs(point.Y) <= metricTolerance)
        .ToArray();
      junctions.Should().HaveCount(2);
      junctions.Should().OnlyContain(point => ReferenceEquals(point, junctions[0]),
        "points within GeometryTolerance must share one exact node even when rounded bucket ids differ by two");
    }

    [Fact]
    public void TransitiveNearEqualArcsNeverMoveAnEndpointBeyondTolerance()
    {
      CommonCenterCurve first = Arc(
        new CommonLinePoint(0D, 0D),
        5D,
        0D,
        Math.PI / 2D);
      CommonCenterCurve bridge = Arc(
        new CommonLinePoint(0.75D * Tolerance, 0D),
        5D,
        0D,
        Math.PI / 2D);
      CommonCenterCurve last = Arc(
        new CommonLinePoint(1.5D * Tolerance, 0D),
        5D,
        0D,
        Math.PI / 2D);

      IReadOnlyList<CommonCenterCurve> forward = CommonLineCurveNetwork.Build(
        new[] { first, bridge, last },
        0.01D,
        Tolerance);
      IReadOnlyList<CommonCenterCurve> reordered = CommonLineCurveNetwork.Build(
        new[] { bridge, last, first },
        0.01D,
        Tolerance);

      forward.Should().HaveCount(2,
        "a transitive chain must not replace an arc by a representative more than one tolerance away");
      reordered.Select(ArcSignature).Should().Equal(forward.Select(ArcSignature),
        "complete-link grouping must remain independent of input order");

      static string ArcSignature(CommonCenterCurve curve)
        => FormattableString.Invariant(
          $"{curve.Center.X:R}|{curve.Center.Y:R}|{curve.Radius:R}|{curve.StartAngleRadians:R}|{curve.SweepRadians:R}");
    }

    private static IReadOnlyList<CommonCenterCurve> BuildNearTangentNetwork(
      double scale,
      double tolerance)
    {
      double radius = 5D * scale;
      double outsideOffset = tolerance / 200D;
      var arc = Arc(new CommonLinePoint(0D, 0D), radius, 0D, Math.PI);
      CommonCenterCurve line = Line(
        -2D * scale,
        radius + outsideOffset,
        2D * scale,
        radius + outsideOffset);
      return CommonLineCurveNetwork.Build(
        new[] { arc, line },
        0.01D * scale,
        tolerance);
    }

    private static CommonLinePoint JunctionNearestTop(
      IReadOnlyList<CommonCenterCurve> curves)
      => curves.SelectMany(curve => new[] { curve.Start, curve.End })
        .OrderBy(point => Math.Abs(point.X))
        .ThenByDescending(point => point.Y)
        .First();

    private static CommonCenterCurve Line(double x1, double y1, double x2, double y2)
      => new CommonCenterCurve(
        CommonLineGeometryKind.Line,
        new CommonLinePoint(x1, y1),
        new CommonLinePoint(x2, y2));

    private static CommonCenterCurve Arc(
      CommonLinePoint center,
      double radius,
      double start,
      double sweep)
      => new CommonCenterCurve(
        CommonLineGeometryKind.CircularArc,
        new CommonLinePoint(center.X + (radius * Math.Cos(start)), center.Y + (radius * Math.Sin(start))),
        new CommonLinePoint(
          center.X + (radius * Math.Cos(start + sweep)),
          center.Y + (radius * Math.Sin(start + sweep))),
        center,
        radius,
        start,
        sweep);
  }
}
