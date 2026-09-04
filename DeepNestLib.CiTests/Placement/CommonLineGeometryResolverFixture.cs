namespace DeepNestLib.CiTests.Placement
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using Xunit;

  public class CommonLineGeometryResolverFixture
  {
    private const double Gap = 0.01D;
    private const double Tolerance = 0.0001D;

    [Fact]
    public void TwoRectanglesProduceOneClosedOuterAndOneSeam()
    {
      List<IPartPlacement> parts = Row(2, 10D, 5D);
      SheetPlacement placement = Placement(parts, Edges(parts, (0, 1, 1, 3)));

      CommonLineGeometryResult result = CommonLineGeometryResolver.Resolve(placement);

      result.Clusters.Should().ContainSingle();
      result.Clusters[0].OuterBoundaries.Should().ContainSingle(path => path.IsClosed);
      result.Clusters[0].CommonLines.Should().ContainSingle();
      Perimeter(result.Clusters[0].OuterBoundaries[0]).Should().BeApproximately(50.02D, Tolerance);
    }

    [Fact]
    public void ThreeRectanglesProduceOneClosedOuterAndTwoSeams()
    {
      List<IPartPlacement> parts = Row(3, 10D, 5D);
      SheetPlacement placement = Placement(parts, Edges(parts, (0, 1, 1, 3), (1, 1, 2, 3)));

      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(placement).Clusters.Single();

      cluster.OuterBoundaries.Should().ContainSingle(path => path.IsClosed);
      cluster.CommonLines.Should().HaveCount(2);
    }

    [Fact]
    public void TwoByTwoGridHasOneOuterAndOneExactFourWayNode()
    {
      List<IPartPlacement> parts = Grid(2, 2, 10D, 5D);
      SheetPlacement placement = Placement(parts, Edges(
        parts,
        (0, 1, 1, 3),
        (2, 1, 3, 3),
        (0, 2, 2, 0),
        (1, 2, 3, 0)));

      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(placement).Clusters.Single();

      cluster.OuterBoundaries.Should().ContainSingle(path => path.IsClosed);
      cluster.CommonLines.Should().HaveCount(4, "the horizontal and vertical runs are split at their exact + node");
      var nodeDegrees = cluster.CommonLines
        .SelectMany(line => new[] { PointKey(line.Start), PointKey(line.End) })
        .GroupBy(key => key)
        .Select(group => group.Count())
        .ToList();
      nodeDegrees.Should().ContainSingle(degree => degree == 4);
    }

    [Fact]
    public void ThreeRectangleOrthogonalLChainKeepsItsNominalConcaveCorner()
    {
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(Rectangle(10D, 5D)) { Id = 0, X = 0D, Y = 0D },
        new PartPlacement(Rectangle(10D, 5D)) { Id = 1, X = 10D + Gap, Y = 0D },
        new PartPlacement(Rectangle(10D, 5D)) { Id = 2, X = 0D, Y = 5D + Gap },
      };
      SheetPlacement placement = Placement(parts, Edges(
        parts,
        (0, 1, 1, 3),
        (0, 2, 2, 0)));

      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(placement).Clusters.Single();

      cluster.PartIds.Should().HaveCount(3);
      cluster.OuterBoundaries.Should().ContainSingle(path => path.IsClosed);
      cluster.CommonLines.Should().HaveCount(2);
      cluster.CommonLines.Select(line => UndirectedKey(line.Start, line.End))
        .Should().OnlyHaveUniqueItems();

      CommonLinePath outer = cluster.OuterBoundaries.Single();
      string nominalCorner = PointKey(new CommonLinePoint(10D, 5D));
      outer.Points.Select(PointKey).Should().Contain(nominalCorner,
        "the L notch must pass through the nominal corner instead of cutting it diagonally");

      var outerNodes = outer.Points.Select(PointKey).ToHashSet();
      cluster.CommonLines.SelectMany(line => new[] { line.Start, line.End })
        .Should().OnlyContain(point => outerNodes.Contains(PointKey(point)),
          "every open seam endpoint must be an exact node on the closed external boundary");
    }

    [Fact]
    public void TenByTenGridIsStableAndFullyConnected()
    {
      const int columns = 10;
      const int rows = 10;
      List<IPartPlacement> parts = Grid(columns, rows, 3D, 2D);
      var links = new List<(int A, int EdgeA, int B, int EdgeB)>();
      for (int row = 0; row < rows; row++)
      {
        for (int column = 0; column < columns; column++)
        {
          int id = (row * columns) + column;
          if (column + 1 < columns)
          {
            links.Add((id, 1, id + 1, 3));
          }

          if (row + 1 < rows)
          {
            links.Add((id, 2, id + columns, 0));
          }
        }
      }

      SheetPlacement placement = Placement(parts, Edges(parts, links.ToArray()));

      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(placement).Clusters.Single();

      cluster.PartIds.Should().HaveCount(100);
      cluster.OuterBoundaries.Should().ContainSingle(path => path.IsClosed);
      cluster.CommonLines.Should().HaveCount(180, "18 grid runs are split into ten deterministic graph edges each");
      cluster.CommonLines
        .Select(line => UndirectedKey(line.Start, line.End))
        .Should().OnlyHaveUniqueItems("no common segment may be exported twice");

      var graph = new Dictionary<string, HashSet<string>>();
      foreach (CommonLineSegment line in cluster.CommonLines)
      {
        string start = PointKey(line.Start);
        string end = PointKey(line.End);
        if (!graph.TryGetValue(start, out HashSet<string> fromStart))
        {
          graph[start] = fromStart = new HashSet<string>();
        }

        if (!graph.TryGetValue(end, out HashSet<string> fromEnd))
        {
          graph[end] = fromEnd = new HashSet<string>();
        }

        fromStart.Add(end);
        fromEnd.Add(start);
      }

      var reached = new HashSet<string>();
      var pending = new Queue<string>();
      pending.Enqueue(graph.Keys.First());
      while (pending.Count > 0)
      {
        string node = pending.Dequeue();
        if (!reached.Add(node))
        {
          continue;
        }

        foreach (string neighbour in graph[node])
        {
          pending.Enqueue(neighbour);
        }
      }

      reached.Should().HaveCount(graph.Count, "every T/+ intersection in the 10x10 common network must share one exact node");
    }

    [Fact]
    public void AQuarterTurnStillResolvesFromPlacedGeometry()
    {
      INfp aNfp = Rectangle(10D, 5D).Rotate(90D);
      INfp bNfp = Rectangle(10D, 5D).Rotate(90D);
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(aNfp) { Id = 0, Rotation = 90D, X = 5D, Y = 0D },
        new PartPlacement(bNfp) { Id = 1, Rotation = 90D, X = 10D + Gap, Y = 0D },
      };

      CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(parts[0], 0, parts[1], 2, Gap, Tolerance);

      CommonLineGeometryResolver.Resolve(Placement(parts, new[] { edge }))
        .Clusters.Single().CommonLines.Should().ContainSingle();
    }

    [Fact]
    public void InnerHolesDoNotBecomePartOfTheClusterOuterBoundary()
    {
      List<IPartPlacement> parts = Row(2, 10D, 5D);
      parts[0].Part.Children.Add(Rectangle(2D, 1D).Shift(2D, 2D));
      SheetPlacement placement = Placement(parts, Edges(parts, (0, 1, 1, 3)));

      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(placement).Clusters.Single();

      cluster.OuterBoundaries.Should().ContainSingle();
      cluster.OuterBoundaries[0].Points.Should().NotContain(point =>
        point.X > 1.9D && point.X < 4.1D && point.Y > 1.9D && point.Y < 3.1D);
    }

    [Theory]
    [InlineData(0.010D)]
    [InlineData(0.015D)]
    [InlineData(0.020D)]
    public void ActiveGapControlsPlacementValidationAndMidpoint(double gap)
    {
      List<IPartPlacement> parts = Row(2, 10D, 5D, gap);

      CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(parts[0], 1, parts[1], 3, gap, Tolerance);

      edge.Start.X.Should().BeApproximately(10D + (gap / 2D), Tolerance);
      CommonLineGeometryResolver.Resolve(Placement(parts, new[] { edge }, gap))
        .Clusters.Should().ContainSingle();
    }

    [Fact]
    public void MetricAndImperialDefaultsRepresentTheSamePhysicalGap()
    {
      CommonLineDefaults.DefaultGapMillimeters.Should().Be(0.254D);
      CommonLineDefaults.DefaultGeometryToleranceMillimeters.Should().Be(0.00254D);
    }

    [Fact]
    public void MetricGeometryUsesTheConvertedGapAndIndependentTolerance()
    {
      double metricGap = CommonLineDefaults.DefaultGapMillimeters;
      double metricTolerance = CommonLineDefaults.DefaultGeometryToleranceMillimeters;
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(Rectangle(254D, 127D)) { Id = 0, X = 0D, Y = 0D },
        new PartPlacement(Rectangle(254D, 127D)) { Id = 1, X = 254D + metricGap, Y = 0D },
      };
      CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(
        parts[0],
        1,
        parts[1],
        3,
        metricGap,
        metricTolerance);

      var placement = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 1000, 1000),
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { edge },
        commonLineGap: metricGap,
        geometryTolerance: metricTolerance);

      CommonLineSegment seam = CommonLineGeometryResolver.Resolve(placement)
        .Clusters.Single().CommonLines.Single();
      seam.Start.X.Should().BeApproximately(254D + (metricGap / 2D), metricTolerance);
    }

    [Fact]
    public void UnequalFullEdgesAreRejectedByV1()
    {
      var a = new PartPlacement(Rectangle(10D, 5D)) { Id = 0 };
      var b = new PartPlacement(Rectangle(10D, 4D)) { Id = 1, X = 10D + Gap };

      Action act = () => CommonLineGeometryResolver.CreateCommonEdge(a, 1, b, 3, Gap, Tolerance);

      act.Should().Throw<CommonLineValidationException>()
        .WithMessage("*same usable length*");
    }

    [Fact]
    public void CurvedOutsideClusterGeometryIsRejectedInsteadOfChorded()
    {
      NoFitPolygon curved = Rectangle(10D, 5D);
      curved.CurvedSegments = new[] { true, false, false, false };
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(curved) { Id = 0 },
        new PartPlacement(Rectangle(10D, 5D)) { Id = 1, X = 10D + Gap },
      };
      CommonEdge straightRelationship = CommonLineGeometryResolver.CreateCommonEdge(
        parts[0],
        1,
        parts[1],
        3,
        Gap,
        Tolerance);

      Action act = () => CommonLineGeometryResolver.Resolve(Placement(parts, new[] { straightRelationship }));

      act.Should().Throw<CommonLineValidationException>()
        .WithMessage("*curved outside geometry*without distortion*");
    }

    [Fact]
    public void CommonEdgesAndDistancesSurviveJsonRoundTrip()
    {
      List<IPartPlacement> parts = Row(2, 10D, 5D);
      SheetPlacement expected = Placement(parts, Edges(parts, (0, 1, 1, 3)));

      SheetPlacement actual = SheetPlacement.FromJson(expected.ToJson());

      actual.CommonLineGap.Should().Be(Gap);
      actual.GeometryTolerance.Should().Be(Tolerance);
      actual.CommonEdges.Should().ContainSingle();
      CommonLineGeometryResolver.Resolve(actual).Clusters.Should().ContainSingle();
    }

    [Fact]
    public void FractionalCommonEdgeCoordinatesKeepFullPrecisionThroughJsonRoundTrip()
    {
      const double fractionalGap = 0.01012345D;
      const double fractionalOffset = 0.12345678D;
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(Rectangle(10D, 5D)) { Id = 0, X = fractionalOffset, Y = fractionalOffset },
        new PartPlacement(Rectangle(10D, 5D))
        {
          Id = 1,
          X = fractionalOffset + 10D + fractionalGap,
          Y = fractionalOffset,
        },
      };
      CommonEdge expectedEdge = CommonLineGeometryResolver.CreateCommonEdge(
        parts[0],
        1,
        parts[1],
        3,
        fractionalGap,
        Tolerance);
      SheetPlacement expected = Placement(parts, new[] { expectedEdge }, fractionalGap);

      SheetPlacement actual = SheetPlacement.FromJson(expected.ToJson());
      CommonEdge actualEdge = actual.CommonEdges.Should().ContainSingle().Subject;

      actualEdge.Gap.Should().Be(expectedEdge.Gap);
      actualEdge.Start.X.Should().Be(expectedEdge.Start.X);
      actualEdge.Start.Y.Should().Be(expectedEdge.Start.Y);
      actualEdge.End.X.Should().Be(expectedEdge.End.X);
      actualEdge.End.Y.Should().Be(expectedEdge.End.Y);
      CommonLineGeometryResolver.Resolve(actual).Clusters.Should().ContainSingle(
        "quantization of legacy part coordinates must remain within the independent geometry tolerance");
    }

    [Fact]
    public void StaleRelationshipRejectsTheNestInsteadOfFallingBack()
    {
      List<IPartPlacement> parts = Row(2, 10D, 5D);
      CommonEdge edge = Edges(parts, (0, 1, 1, 3)).Single();
      parts[1].X += 0.005D;

      Action act = () => CommonLineGeometryResolver.Resolve(Placement(parts, new[] { edge }));

      act.Should().Throw<CommonLineValidationException>()
        .WithMessage("*separation*");
    }

    [Fact]
    public void ThirdPartInsideTheGapRejectsTheCluster()
    {
      List<IPartPlacement> parts = Row(2, 10D, 5D);
      CommonEdge edge = Edges(parts, (0, 1, 1, 3)).Single();
      parts.Add(new PartPlacement(Rectangle(Gap / 4D, 1D))
      {
        Id = 2,
        X = 10D + ((Gap * 3D) / 8D),
        Y = 2D,
      });

      Action act = () => CommonLineGeometryResolver.Resolve(Placement(parts, new[] { edge }));

      act.Should().Throw<CommonLineValidationException>()
        .WithMessage("*COMMON_LINE*material*");
    }

    private static List<IPartPlacement> Row(int count, double width, double height, double gap = Gap)
      => Grid(count, 1, width, height, gap);

    private static List<IPartPlacement> Grid(
      int columns,
      int rows,
      double width,
      double height,
      double gap = Gap)
    {
      var result = new List<IPartPlacement>();
      for (int row = 0; row < rows; row++)
      {
        for (int column = 0; column < columns; column++)
        {
          int id = (row * columns) + column;
          result.Add(new PartPlacement(Rectangle(width, height))
          {
            Id = id,
            X = column * (width + gap),
            Y = row * (height + gap),
          });
        }
      }

      return result;
    }

    private static NoFitPolygon Rectangle(double width, double height)
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0D, 0D),
        new SvgPoint(width, 0D),
        new SvgPoint(width, height),
        new SvgPoint(0D, height),
      });

    private static IReadOnlyList<CommonEdge> Edges(
      IReadOnlyList<IPartPlacement> parts,
      params (int A, int EdgeA, int B, int EdgeB)[] links)
      => links.Select(link => CommonLineGeometryResolver.CreateCommonEdge(
        parts[link.A],
        link.EdgeA,
        parts[link.B],
        link.EdgeB,
        Gap,
        Tolerance)).ToList();

    private static SheetPlacement Placement(
      IReadOnlyList<IPartPlacement> parts,
      IReadOnlyList<CommonEdge> edges,
      double gap = Gap)
      => new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 1000, 1000),
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: edges,
        commonLineGap: gap,
        geometryTolerance: Tolerance);

    private static double Perimeter(CommonLinePath path)
      => Enumerable.Range(0, path.Points.Count).Sum(i => Distance(
        path.Points[i],
        path.Points[(i + 1) % path.Points.Count]));

    private static double Distance(CommonLinePoint a, CommonLinePoint b)
      => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static string PointKey(CommonLinePoint point) => $"{point.X:F6},{point.Y:F6}";

    private static string UndirectedKey(CommonLinePoint a, CommonLinePoint b)
    {
      string first = PointKey(a);
      string second = PointKey(b);
      return string.CompareOrdinal(first, second) <= 0 ? $"{first}|{second}" : $"{second}|{first}";
    }
  }
}
