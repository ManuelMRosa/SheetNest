namespace DeepNestLib.CiTests.Placement
{
  using System;
  using System.Text.Json;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using Xunit;

  public class CommonLineGeometryModelV2Fixture
  {
    [Fact]
    public void LegacyCommonEdgeJsonShapeAndDefaultsRemainCompatible()
    {
      var expected = new CommonEdge(
        11,
        17,
        2,
        5,
        new CommonLinePoint(1D, 2D),
        new CommonLinePoint(3D, 4D),
        0.01D);

      string json = JsonSerializer.Serialize(expected);

      json.Should().NotContain("\"SchemaVersion\"");
      json.Should().NotContain("\"GeometryKind\"");
      json.Should().NotContain("\"SpanA\"");
      json.Should().NotContain("\"SpanB\"");
      json.Should().NotContain("\"CenterCurve\"");

      CommonEdge actual = JsonSerializer.Deserialize<CommonEdge>(json);
      actual.Should().NotBeNull();
      actual.PartAId.Should().Be(11);
      actual.PartBId.Should().Be(17);
      actual.EdgeAIndex.Should().Be(2);
      actual.EdgeBIndex.Should().Be(5);
      actual.Start.X.Should().Be(1D);
      actual.End.Y.Should().Be(4D);
      actual.Gap.Should().Be(0.01D);
      actual.SchemaVersion.Should().Be(CommonEdge.LegacySchemaVersion);
      actual.GeometryKind.Should().Be(CommonLineGeometryKind.Line);
      actual.SpanA.Should().BeNull();
      actual.SpanB.Should().BeNull();
      actual.CenterCurve.Should().BeNull();
    }

    [Fact]
    public void ParametricCircularArcCommonEdgeRoundTripsAllV2Data()
    {
      var expected = new CommonEdge(
        4,
        9,
        -1,
        -1,
        new CommonLinePoint(10D, 0D),
        new CommonLinePoint(0D, 10D),
        0.015D,
        CommonEdge.ParametricSchemaVersion,
        CommonLineGeometryKind.CircularArc,
        new CommonBoundarySpan(0, 3, 0.2D, 0.8D, "SOURCE-A", "CONTOUR-A"),
        new CommonBoundarySpan(0, 7, 0.8D, 0.2D, "SOURCE-B", "CONTOUR-B"),
        new CommonCenterCurve(
          CommonLineGeometryKind.CircularArc,
          new CommonLinePoint(10D, 0D),
          new CommonLinePoint(0D, 10D),
          new CommonLinePoint(0D, 0D),
          10D,
          0D,
          Math.PI / 2D));

      string json = JsonSerializer.Serialize(expected);
      CommonEdge actual = JsonSerializer.Deserialize<CommonEdge>(json);

      json.Should().Contain("\"SchemaVersion\":2");
      json.Should().Contain("\"GeometryKind\":1");
      actual.Should().NotBeNull();
      actual.SchemaVersion.Should().Be(CommonEdge.ParametricSchemaVersion);
      actual.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      actual.EdgeAIndex.Should().Be(-1, "parametric V2 records must fail closed in a legacy edge-index reader");
      actual.EdgeBIndex.Should().Be(-1);
      actual.SpanA.ContourIndex.Should().Be(0);
      actual.SpanA.PrimitiveIndex.Should().Be(3);
      actual.SpanA.StartParameter.Should().Be(0.2D);
      actual.SpanA.EndParameter.Should().Be(0.8D);
      actual.SpanA.SourceFingerprint.Should().Be("SOURCE-A");
      actual.SpanA.ContourFingerprint.Should().Be("CONTOUR-A");
      actual.SpanB.PrimitiveIndex.Should().Be(7);
      actual.CenterCurve.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      actual.CenterCurve.Center.X.Should().Be(0D);
      actual.CenterCurve.Radius.Should().Be(10D);
      actual.CenterCurve.SweepRadians.Should().BeApproximately(Math.PI / 2D, 1e-12D);
    }

    [Fact]
    public void ClusterProjectsLegacyLinesIntoRuntimeCurves()
    {
      var line = new CommonLineSegment(
        new CommonLinePoint(1D, 2D),
        new CommonLinePoint(3D, 4D));

      var cluster = new CommonLineCluster(
        new[] { 1, 2 },
        Array.Empty<CommonLinePath>(),
        new[] { line });

      cluster.CommonLines.Should().ContainSingle().Which.Should().BeSameAs(line);
      cluster.CommonCurves.Should().ContainSingle();
      cluster.CommonCurves[0].GeometryKind.Should().Be(CommonLineGeometryKind.Line);
      cluster.CommonCurves[0].Start.Should().BeSameAs(line.Start);
      cluster.CommonCurves[0].End.Should().BeSameAs(line.End);
    }

    [Fact]
    public void ClusterProjectsRuntimeLineCurvesIntoLegacyLinesButNotArcChords()
    {
      var line = new CommonCenterCurve(
        CommonLineGeometryKind.Line,
        new CommonLinePoint(0D, 0D),
        new CommonLinePoint(5D, 0D));
      var arc = new CommonCenterCurve(
        CommonLineGeometryKind.CircularArc,
        new CommonLinePoint(5D, 0D),
        new CommonLinePoint(0D, 5D),
        new CommonLinePoint(0D, 0D),
        5D,
        0D,
        Math.PI / 2D);

      var cluster = new CommonLineCluster(
        new[] { 1, 2 },
        Array.Empty<CommonLinePath>(),
        null,
        new[] { line, arc });

      cluster.CommonCurves.Should().HaveCount(2);
      cluster.CommonLines.Should().ContainSingle();
      cluster.CommonLines[0].Start.Should().BeSameAs(line.Start);
      cluster.CommonLines[0].End.Should().BeSameAs(line.End);
    }
  }
}
