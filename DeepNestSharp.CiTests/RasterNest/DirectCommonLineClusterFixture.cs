namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  public class DirectCommonLineClusterFixture
  {
    [Fact]
    public void CompatibleBaselineKeepsNormalSpacingUntilAConcreteEdgeIsSelected()
    {
      var outline = Square(10);
      var baseline = new List<CompactItem>
      {
        Compact(outline, 0, 1),
        Compact(outline, 1, 25),
      };

      RasterCompact.Compact(baseline, 40, 20, 0, commonLineGap: 0.05);
      Gap(baseline[0], baseline[1]).Should().BeGreaterThanOrEqualTo(0.30 - 0.001,
        "matching modes and domains are only eligibility, not an intentional common edge");

      var selected = new List<CompactItem>
      {
        Compact(outline, 0, 1, 1),
        Compact(outline, 1, 25, 0),
      };

      RasterCompact.Compact(selected, 40, 20, 0, commonLineGap: 0.05);
      Gap(selected[0], selected[1]).Should().BeApproximately(0.05, 1e-5);
    }

    [Fact]
    public void ARecordWaivesClearanceOnlyAtItsIndexedOutsideEdge()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      var pair = Pair(Square(10), gap);
      pair[0].Id = 1;
      pair[1].Id = 2;
      var edges = new[] { new CommonEdgeClearance.EdgeReference(pair[0], 1, pair[1], 3) };

      CommonEdgeClearance.TooClose(
        pair[0], pair[1], 0.30, PlacementCollision.PlacementNoise,
        Array.Empty<CommonEdgeClearance.EdgeReference>(), gap, tolerance).Should().BeTrue(
          "compatibility without a record keeps the normal part gap");
      CommonEdgeClearance.TooClose(
        pair[0], pair[1], 0.30, PlacementCollision.PlacementNoise,
        edges, gap, tolerance).Should().BeFalse(
          "the complete recorded side is the only intentional clearance exception");
    }

    [Fact]
    public void PartialRecordDoesNotWaiveTheUnrecordedRemainderOfTheSameEdges()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      var pair = Pair(Square(10), gap);
      pair[0].Id = 1;
      pair[1].Id = 2;
      var partial = new[]
      {
        new CommonEdgeClearance.EdgeReference(
          pair[0],
          1,
          pair[1],
          3,
          edgeAStart: 0.25,
          edgeAEnd: 0.75,
          edgeBStart: 0.25,
          edgeBEnd: 0.75),
      };

      CommonEdgeClearance.TooClose(
        pair[0], pair[1], 0.30, PlacementCollision.PlacementNoise,
        partial, gap, tolerance).Should().BeTrue(
          "the two unrecorded quarter-spans remain at CommonLineGap and must keep normal clearance");
    }

    [Fact]
    public void CollinearNextEdgesMustRecoverNormalSpacingUnlessTheyAreAlsoRecorded()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      var aShape = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(1, 0),
        new SvgPoint(1, 0.5),
        new SvgPoint(1, 1),
        new SvgPoint(0, 1),
      });
      var bShape = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(1, 0),
        new SvgPoint(1, 1),
        new SvgPoint(0, 1),
        new SvgPoint(0, 0.5),
      });
      IPartPlacement a = Place(aShape, 0, 0, 0);
      IPartPlacement b = Place(bShape, 1, 1 + gap, 0);
      a.Id = 1;
      b.Id = 2;
      var lowerSeamOnly = new[]
      {
        new CommonEdgeClearance.EdgeReference(a, 1, b, 4),
      };

      CommonLineGeometryResolver.TryCreateCommonEdge(a, 1, b, 4, gap, tolerance, out _)
        .Should().BeTrue();
      CommonEdgeClearance.TooClose(
        a,
        b,
        0.30,
        PlacementCollision.PlacementNoise,
        lowerSeamOnly,
        gap,
        tolerance).Should().BeTrue(
          "the upper collinear sides remain ordinary geometry and cannot inherit the endpoint cap");
    }

    [Fact]
    public void AConcavePeerWithAnotherLobeInsideNormalSpacingIsRejected()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      var (a, b) = LobedPair(gap);
      a.Id = 1;
      b.Id = 2;
      CommonLineGeometryResolver.TryCreateCommonEdge(a, 1, b, 7, gap, tolerance, out _).Should().BeTrue();

      CommonEdgeClearance.TooClose(
        a,
        b,
        0.30,
        PlacementCollision.PlacementNoise,
        new[] { new CommonEdgeClearance.EdgeReference(a, 1, b, 7) },
        gap,
        tolerance).Should().BeTrue(
          "the lobes remain only 0.21 apart near the middle, outside the endpoint caps");
    }

    [Fact]
    public void AcuteStraightBranchesCanRecoverNormalSpacingBeyondAFixedEndpointRadius()
    {
      const double gap = 0.01D;
      const double tolerance = 0.0001D;
      double opening = 10D * Math.Tan(5D * Math.PI / 180D);
      var left = new NoFitPolygon(new[]
      {
        new SvgPoint(-5D, 0D), new SvgPoint(0D, 0D), new SvgPoint(0D, 1D),
        new SvgPoint(-opening, 11D), new SvgPoint(-5D, 11D),
      });
      var right = new NoFitPolygon(new[]
      {
        new SvgPoint(0D, 0D), new SvgPoint(5D, 0D), new SvgPoint(5D, 11D),
        new SvgPoint(opening, 11D), new SvgPoint(0D, 1D),
      });
      IPartPlacement a = Place(left, 0, 5D, 1D);
      IPartPlacement b = Place(right, 1, 5D + gap, 1D);
      a.Id = 1;
      b.Id = 2;
      CommonLineGeometryResolver.TryCreateCommonEdge(a, 1, b, 4, gap, tolerance, out _)
        .Should().BeTrue();
      CommonEdgeClearance.TooClose(a, b, 0.30D, PlacementCollision.PlacementNoise,
        new[] { new CommonEdgeClearance.EdgeReference(a, 1, b, 4) }, gap, tolerance)
        .Should().BeFalse("the ten-degree opening recovers normal clearance monotonically beyond the old cap");
    }

    [Fact]
    public void AShortLobeInsideTheEndpointCapStillKeepsNormalSpacing()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      var (a, b) = ShortEndpointLobedPair(gap);
      a.Id = 1;
      b.Id = 2;
      CommonLineGeometryResolver.TryCreateCommonEdge(a, 1, b, 7, gap, tolerance, out _).Should().BeTrue();

      CommonEdgeClearance.TooClose(
        a,
        b,
        0.30,
        PlacementCollision.PlacementNoise,
        new[] { new CommonEdgeClearance.EdgeReference(a, 1, b, 7) },
        gap,
        tolerance).Should().BeTrue(
          "an unrelated concave lobe cannot inherit the seam exception merely because it fits inside the endpoint cap");
    }

    [Fact]
    public void FoldedTransitiveClusterDoesNotCreateAJunctionFromNearbyDistinctVertices()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      const double narrowWidth = 0.00015;
      IPartPlacement a = Place(Rectangle(1, 1), 0, 0, 0);
      IPartPlacement bridge = Place(Rectangle(narrowWidth, 1), 1, 1 + gap, 0);
      IPartPlacement c = Place(Rectangle(1, 1), 2, 1 + gap + narrowWidth + gap, 0);
      a.Id = 1;
      bridge.Id = 2;
      c.Id = 3;
      var edges = new[]
      {
        new CommonEdgeClearance.EdgeReference(a, 1, bridge, 3),
        new CommonEdgeClearance.EdgeReference(bridge, 1, c, 3),
      };

      CommonEdgeClearance.TooClose(
        a,
        bridge,
        0.30,
        PlacementCollision.PlacementNoise,
        edges,
        gap,
        tolerance).Should().BeFalse("the first direct seam is intentional");
      CommonEdgeClearance.TooClose(
        bridge,
        c,
        0.30,
        PlacementCollision.PlacementNoise,
        edges,
        gap,
        tolerance).Should().BeFalse("the second direct seam is intentional");
      CommonEdgeClearance.TooClose(
        a,
        c,
        0.30,
        PlacementCollision.PlacementNoise,
        edges,
        gap,
        tolerance).Should().BeTrue(
          "different bridge vertices that happen to lie within two slot widths are not one T/+ node");
    }

    [Fact]
    public void PlannerRollsBackWhenASelectedSideWouldPullAnotherLobeInsideNormalSpacing()
    {
      const double normalGap = 0.30;
      var (a, b) = LobedPair(normalGap);
      var all = new List<IPartPlacement> { a, b };
      double originalX = b.X;

      var result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        Rules(normalGap, all.Count),
        40,
        25,
        0,
        0.01,
        0.0001);

      result.Edges.Should().BeEmpty();
      result.GivenUp.Should().Be(1);
      b.X.Should().Be(originalX, "a failed exception must restore the ordinary-spacing candidate");
    }

    [Theory]
    [InlineData(0.010)]
    [InlineData(0.050)]
    public void FullOutsideEdgeMovesToTheExplicitGapAndProducesARecord(double gap)
    {
      var all = Pair(Square(10), 0.30);

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        Rules(0.30, all.Count),
        40,
        20,
        0,
        gap,
        0.0001);

      result.Edges.Should().ContainSingle();
      (all[1].PlacedPart.MinX - all[0].PlacedPart.MaxX).Should().BeApproximately(gap, 1e-8);
      CommonLineGeometryResolver.TryCreateCommonEdge(
        result.Edges[0].PartA,
        result.Edges[0].EdgeAIndex,
        result.Edges[0].PartB,
        result.Edges[0].EdgeBIndex,
        gap,
        0.0001,
        out CommonEdge record).Should().BeTrue();
      record.Gap.Should().Be(gap);
    }

    [Fact]
    public void SheetCamDomainStaysOnTheLegacyMeasuredKerfRoute()
    {
      var all = Pair(Square(10), 0.30);
      var rules = Rules(0.30, all.Count);
      rules[0] = (CommonCuttingMode.Unrestricted, 0, "sheetcam:C:\\jobs\\one.nest", false, 0.30);
      rules[1] = (CommonCuttingMode.Unrestricted, 1, "sheetcam:C:\\jobs\\one.nest", false, 0.30);

      var result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        rules,
        40,
        20,
        0,
        0.01,
        0.0001);

      result.Edges.Should().BeEmpty(
        "a .nest return does not encode nominal cluster boundaries or COMMON_LINE layers");
      (all[1].PlacedPart.MinX - all[0].PlacedPart.MaxX).Should().BeApproximately(0.30, 1e-9,
        "the legacy measured-kerf snap, not V1, owns SheetCam movement");
    }

    [Fact]
    public void UnequalStraightEdgesCreateOneExplicitPartialSpan()
    {
      var first = Place(Rectangle(10, 10), 0, 1, 1);
      var second = Place(Rectangle(10, 8), 1, 11.3, 1);
      var all = new List<IPartPlacement> { first, second };

      var result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        Rules(0.30, all.Count),
        40,
        20,
        0,
        0.01,
        0.0001);

      SparrowNestService.CommonEdgeIntent intent = result.Edges.Should().ContainSingle().Subject;
      using (new FluentAssertions.Execution.AssertionScope())
      {
        (second.PlacedPart.MinX - first.PlacedPart.MaxX).Should().BeApproximately(0.01, 1e-8);
        ((intent.EdgeAEnd - intent.EdgeAStart) * 10D).Should().BeApproximately(8D, 0.0001);
        (intent.EdgeBEnd - intent.EdgeBStart).Should().BeApproximately(1D, 1e-9);
      }

      CommonLineGeometryResolver.TryCreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        0.01,
        0.0001,
        out CommonEdge record).Should().BeTrue();
      record.SchemaVersion.Should().Be(2);
    }

    [Fact]
    public void QuarterTurnPlacementCanCreateACommonEdge()
    {
      INfp rotated = Rectangle(6, 10).Rotate(90);
      var first = Place(rotated, 0, 11, 1, 90);
      var second = Place(rotated, 1, 21.3, 1, 90);
      var all = new List<IPartPlacement> { first, second };

      var result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        Rules(0.30, all.Count),
        40,
        30,
        0,
        0.01,
        0.0001);

      result.Edges.Should().ContainSingle();
      (second.PlacedPart.MinX - first.PlacedPart.MaxX).Should().BeApproximately(0.01, 1e-8);
    }

    [Fact]
    public void EdgeAwareClearanceUsesTheBakedMirroredPlacedGeometryWithoutLosingIndexes()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      INfp mirrored = Rectangle(10, 6).MirrorX();
      var a = new PartPlacement(mirrored) { Id = 1, Source = 0, X = 10, Y = 1, IsMirrored = true };
      var b = new PartPlacement(mirrored) { Id = 2, Source = 1, X = 20 + gap, Y = 1, IsMirrored = true };
      CommonLineGeometryResolver.TryCreateCommonEdge(a, 3, b, 1, gap, tolerance, out _).Should().BeTrue();

      CommonEdgeClearance.TooClose(
        a,
        b,
        0.30,
        PlacementCollision.PlacementNoise,
        new[] { new CommonEdgeClearance.EdgeReference(a, 3, b, 1) },
        gap,
        tolerance).Should().BeFalse();
    }

    [Fact]
    public void NonQuarterTurnStraightEdgesCanCreateACommonLine()
    {
      const double angle = 37D;
      const double normalGap = 0.30D;
      const double width = 10D;
      const double height = 4D;
      double radians = angle * Math.PI / 180D;
      double normalX = Math.Sin(radians);
      double normalY = -Math.Cos(radians);
      INfp rotated = Rectangle(width, height).Rotate(angle);
      double firstX = 10D;
      double firstY = 10D;
      SvgPoint edgeTwoEnd = rotated.Points[3];
      var all = new List<IPartPlacement>
      {
        new PartPlacement(rotated) { Source = 0, X = firstX, Y = firstY, Rotation = angle },
        new PartPlacement(rotated)
        {
          Source = 1,
          X = firstX + (normalGap * normalX) - edgeTwoEnd.X,
          Y = firstY + (normalGap * normalY) - edgeTwoEnd.Y,
          Rotation = angle,
        },
      };

      var result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        Rules(0.30, all.Count),
        40,
        40,
        0,
        0.01,
        0.0001);

      SparrowNestService.CommonEdgeIntent intent = result.Edges.Should().ContainSingle().Subject;
      CommonEdge record = CommonLineGeometryResolver.CreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        0.01,
        0.0001);
      double seamX = record.End.X - record.Start.X;
      double seamY = record.End.Y - record.Start.Y;
      Math.Sqrt((seamX * seamX) + (seamY * seamY)).Should().BeApproximately(width, 0.0001);
    }

    [Fact]
    public void TwoByTwoGridRecordsAllFourEdgesIncludingTheCycle()
    {
      const double InitialGap = 0.30;
      const double Gap = 0.01;
      var square = Square(10);
      var all = new List<IPartPlacement>
      {
        Place(square, 0, 1, 1),
        Place(square, 1, 11 + InitialGap, 1),
        Place(square, 2, 1, 11 + InitialGap),
        Place(square, 3, 11 + InitialGap, 11 + InitialGap),
      };

      var result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        Rules(InitialGap, all.Count),
        40,
        40,
        0,
        Gap,
        0.0001);

      result.Edges.Should().HaveCount(4, "a cycle edge is real cutting geometry, not a redundant motion constraint");
      result.Groups.Distinct().Should().Equal(1);

      var records = result.Edges.Select(edge => CommonLineGeometryResolver.CreateCommonEdge(
        edge.PartA,
        edge.EdgeAIndex,
        edge.EdgeAStart,
        edge.EdgeAEnd,
        edge.PartB,
        edge.EdgeBIndex,
        edge.EdgeBStart,
        edge.EdgeBEnd,
        Gap,
        0.0001)).ToList();
      var sheet = new SheetPlacement(
        placementType: PlacementTypeEnum.BoundingBox,
        sheet: Sheet.NewSheet(1, 40, 40),
        partPlacements: all,
        mergedLength: 0,
        requiresMergedLines: true,
        commonEdges: records,
        commonLineGap: Gap,
        geometryTolerance: 0.0001);

      CommonLineGeometryResolver.Resolve(sheet).Clusters.Should().ContainSingle();
    }

    [Fact]
    public void NonLinearLChainKeepsBothIntentionalNinetyDegreeEdges()
    {
      const double initialGap = 0.30;
      var square = Square(10);
      var all = new List<IPartPlacement>
      {
        Place(square, 0, 1, 1),
        Place(square, 1, 11 + initialGap, 1),
        Place(square, 2, 1, 11 + initialGap),
      };
      var result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        Rules(initialGap, all.Count),
        40,
        40,
        0,
        0.01,
        0.0001);

      result.Edges.Should().HaveCount(2, "a V1 chain may turn 90 degrees at an intentional junction");
      var records = result.Edges.Select(edge => CommonLineGeometryResolver.CreateCommonEdge(
        edge.PartA,
        edge.EdgeAIndex,
        edge.EdgeAStart,
        edge.EdgeAEnd,
        edge.PartB,
        edge.EdgeBIndex,
        edge.EdgeBStart,
        edge.EdgeBEnd,
        0.01,
        0.0001)).ToList();
      var sheet = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(1, 40, 40),
        all,
        0,
        commonEdges: records,
        commonLineGap: 0.01,
        geometryTolerance: 0.0001);
      CommonLineGeometryResolver.Resolve(sheet).Clusters.Should().ContainSingle();
    }

    private static List<IPartPlacement> Pair(INfp part, double gap)
      => new List<IPartPlacement>
      {
        Place(part, 0, 1, 1),
        Place(part, 1, 1 + (part.MaxX - part.MinX) + gap, 1),
      };

    private static Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> Rules(
      double spacing,
      int count)
    {
      var result = new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>();
      for (int i = 0; i < count; i++)
      {
        result[i] = (CommonCuttingMode.Unrestricted, i, "direct", true, spacing);
      }

      return result;
    }

    private static IPartPlacement Place(INfp part, int source, double x, double y, double rotation = 0)
      => new PartPlacement(part) { Source = source, X = x, Y = y, Rotation = rotation };

    private static (IPartPlacement A, IPartPlacement B) LobedPair(double seamGap)
    {
      var a = Place(new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10), // indexed common side ends here
        new SvgPoint(8, 10),
        new SvgPoint(8, 12),
        new SvgPoint(9.9, 12),
        new SvgPoint(9.9, 14),
        new SvgPoint(0, 14),
      }), 0, 1, 1);
      var b = Place(new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 14),
        new SvgPoint(0.1, 14),
        new SvgPoint(0.1, 12),
        new SvgPoint(2, 12),
        new SvgPoint(2, 10),
        new SvgPoint(0, 10), // edge 7 returns to (0, 0), opposite the first common side
      }), 1, 11 + seamGap, 1);
      return (a, b);
    }

    private static (IPartPlacement A, IPartPlacement B) ShortEndpointLobedPair(double seamGap)
    {
      var a = Place(new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(9.9, 10),
        new SvgPoint(9.9, 10.4),
        new SvgPoint(9.95, 10.4),
        new SvgPoint(9.95, 10.8),
        new SvgPoint(0, 10.8),
      }), 0, 1, 1);
      var b = Place(new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10.8),
        new SvgPoint(0.05, 10.8),
        new SvgPoint(0.05, 10.4),
        new SvgPoint(0.1, 10.4),
        new SvgPoint(0.1, 10),
        new SvgPoint(0, 10),
      }), 1, 11 + seamGap, 1);
      return (a, b);
    }

    private static CompactItem Compact(INfp part, int id, double x, params int[] peers)
      => new CompactItem
      {
        Poly = part,
        X = x,
        Y = 1,
        Spacing = 0.30,
        Cc = CommonCuttingMode.Unrestricted,
        ShareKey = id,
        CommonCuttingDomain = "direct",
        PlacementId = id,
        CommonEdgePeers = new HashSet<int>(peers),
      };

    private static double Gap(CompactItem a, CompactItem b)
    {
      double dx = Math.Max(0, Math.Max(
        a.X + a.Poly.MinX - (b.X + b.Poly.MaxX),
        b.X + b.Poly.MinX - (a.X + a.Poly.MaxX)));
      double dy = Math.Max(0, Math.Max(
        a.Y + a.Poly.MinY - (b.Y + b.Poly.MaxY),
        b.Y + b.Poly.MinY - (a.Y + a.Poly.MaxY)));
      return dx > 0 && dy > 0 ? Math.Sqrt((dx * dx) + (dy * dy)) : Math.Max(dx, dy);
    }

    private static INfp Square(double side) => Rectangle(side, side);

    private static INfp Rectangle(double width, double height)
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(width, 0),
        new SvgPoint(width, height),
        new SvgPoint(0, height),
      });
  }
}
