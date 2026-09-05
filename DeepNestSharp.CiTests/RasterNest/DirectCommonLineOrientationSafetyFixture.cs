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
  using FluentAssertions.Execution;
  using Xunit;

  /// <summary>
  /// Safety contracts for the orientation-aware direct common-line planner. These tests deliberately
  /// exercise its internal overload rather than relying on sparrow to happen to return a useful pose.
  /// </summary>
  public sealed class DirectCommonLineOrientationSafetyFixture
  {
    private const double NormalGap = 0.30D;
    private const double CommonLineGap = 0.01D;
    private const double GeometryTolerance = 0.0001D;

    [Fact]
    public void FixedNinetyDegreeProfileDoesNotInventTheOppositePose()
    {
      INfp source = HodgsonOutline();
      INfp rotated = source.Rotate(90D);
      IPartPlacement lower = PlaceAt(rotated, 0, 90D, 3D, 2D);
      IPartPlacement upper = PlaceAt(rotated, 0, 90D, 3D, lower.MaxY + NormalGap);
      INfp lowerPart = lower.Part;
      INfp upperPart = upper.Part;
      var placements = new List<IPartPlacement> { lower, upper };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        Profiles(source, new[] { 90 }),
        sheetW: 15D,
        sheetH: 30D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      CommonEdge record = Record(result);
      using (new AssertionScope())
      {
        placements[0].Should().BeSameAs(lower);
        placements[1].Should().BeSameAs(upper);
        placements[0].Part.Should().BeSameAs(lowerPart);
        placements[1].Part.Should().BeSameAs(upperPart);
        placements.Should().OnlyContain(placement => Normalize(placement.Rotation) == 90D,
          "an angles {90} profile does not authorize current + 180");
        result.Edges.Should().ContainSingle();
        result.GivenUp.Should().Be(0);
        upper.MinY.Should().BeApproximately(lower.MaxY + CommonLineGap, 1e-8);
        Length(record).Should().BeApproximately(1.375D, GeometryTolerance,
          "without the forbidden flip only Hodgson's short lateral side can be shared here");
      }
    }

    [Fact]
    public void RejectedOppositePoseRestoresEveryOriginalPlacementFieldAndReference()
    {
      INfp source = OneLongEdgeBar();

      // Control: in the absence of the blocker this layout has one ten-unit seam and it requires one
      // of the identically oriented copies to take its explicitly allowed 180-degree pose.
      var control = VerticalLongEdgePair(source);
      SparrowNestService.DirectCommonLineSnapResult controlResult = SparrowNestService.SnapDirectCommonLineEdges(
        control,
        DirectRules(),
        Profiles(source, new[] { 0, 180 }),
        sheetW: 20D,
        sheetH: 12D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);
      controlResult.Edges.Should().ContainSingle();
      control.Select(placement => Normalize(placement.Rotation)).Should().Contain(180D);
      Length(Record(controlResult)).Should().BeApproximately(10D, GeometryTolerance);

      var placements = VerticalLongEdgePair(source);
      IPartPlacement upper = placements[0];
      IPartPlacement lower = placements[1];
      INfp blockerShape = Rectangle(0.5D, 0.5D);
      IPartPlacement blocker = PlaceAt(blockerShape, 1, 0D, 2.6D, 4.3D);
      placements.Add(blocker);
      var references = placements.ToArray();
      var parts = placements.Select(placement => placement.Part).ToArray();
      var rotations = placements.Select(placement => placement.Rotation).ToArray();
      var xs = placements.Select(placement => placement.X).ToArray();
      var ys = placements.Select(placement => placement.Y).ToArray();
      Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> rules = DirectRules();
      rules[1] = (CommonCuttingMode.None, 1, string.Empty, false, 0D);

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        rules,
        Profiles(source, new[] { 0, 180 }),
        sheetW: 20D,
        sheetH: 12D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      using (new AssertionScope())
      {
        result.Edges.Should().BeEmpty("the flipped lower part would cover the stationary blocker");
        result.GivenUp.Should().BeGreaterThan(0, "the otherwise-valid +180 candidate was tried and rejected");
        for (int i = 0; i < placements.Count; i++)
        {
          placements[i].Should().BeSameAs(references[i], $"placement {i} must regain its original identity");
          placements[i].Part.Should().BeSameAs(parts[i], $"placement {i} must regain its original geometry");
          placements[i].Rotation.Should().Be(rotations[i]);
          placements[i].X.Should().Be(xs[i]);
          placements[i].Y.Should().Be(ys[i]);
        }

        placements[0].Should().BeSameAs(upper);
        placements[1].Should().BeSameAs(lower);
        placements[2].Should().BeSameAs(blocker);
      }
    }

    [Fact]
    public void OffOriginOppositePoseTurnsAroundThePlacedBoundingBoxCentre()
    {
      INfp source = Offset(OneLongEdgeBar(), 100D, 50D);
      var placements = VerticalLongEdgePair(source);
      IPartPlacement lowerBefore = placements[1];
      double centreX = (lowerBefore.MinX + lowerBefore.MaxX) / 2D;

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        Profiles(source, new[] { 0, 180 }),
        sheetW: 20D,
        sheetH: 12D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      IPartPlacement turned = placements.Should().ContainSingle(placement => Normalize(placement.Rotation) == 180D).Subject;
      CommonEdge record = Record(result);
      using (new AssertionScope())
      {
        result.Edges.Should().ContainSingle();
        result.GivenUp.Should().Be(0);
        ((turned.MinX + turned.MaxX) / 2D).Should().BeApproximately(centreX, 1e-9,
          "the source's distant CAD origin must not throw the flipped pose across the sheet");
        turned.MinX.Should().BeApproximately(2D, 1e-9);
        turned.MaxX.Should().BeApproximately(12D, 1e-9);
        placements.Should().OnlyContain(placement => placement.MinX >= 0D && placement.MaxX <= 20D);
        Length(record).Should().BeApproximately(10D, GeometryTolerance);
      }
    }

    [Fact]
    public void RotatedCommonLineComponentIsTranslatedBackInsideTheSheetAsOneRigidUnit()
    {
      INfp horizontal = Rectangle(4D, 1D);
      INfp vertical = Rectangle(1D, 4D);
      var placements = new List<IPartPlacement>
      {
        // Rotating this part about its placed centre produces bounds y=-0.5..3.5. The common-line
        // relationship itself is valid; the complete pair only needs a rigid +0.5 Y translation.
        PlaceAt(horizontal, 0, 0D, 2D, 1D),
        PlaceAt(vertical, 1, 0D, 4.8D, 0D),
      };
      var rules = new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>
      {
        [0] = (CommonCuttingMode.Unrestricted, 0, "direct", true, NormalGap),
        [1] = (CommonCuttingMode.Unrestricted, 1, "direct", true, NormalGap),
      };
      var profiles = new Dictionary<int, SparrowNestService.DirectOrientationProfile>
      {
        [0] = new SparrowNestService.DirectOrientationProfile(horizontal, new[] { 90 }),
        [1] = new SparrowNestService.DirectOrientationProfile(vertical, new[] { 0 }),
      };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        rules,
        profiles,
        sheetW: 10D,
        sheetH: 6D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance,
        effort: NestEffort.Fast);

      CommonEdge record = Record(result);
      using (new AssertionScope())
      {
        result.GivenUp.Should().Be(0);
        Length(record).Should().BeApproximately(4D, GeometryTolerance);
        Normalize(placements.Single(part => part.Source == 0).Rotation).Should().Be(90D);
        placements.Should().OnlyContain(part => part.MinY >= -GeometryTolerance
          && part.MaxY <= 6D + GeometryTolerance);
        placements.Min(part => part.MinY).Should().BeApproximately(0D, GeometryTolerance,
          "the selected component should be translated, not rejected, when its centred pose crosses an edge");
      }
    }

    [Fact]
    public void OnePartCannotPromiseTwoLongEdgesThatRequireConflictingPoses()
    {
      INfp source = OneLongEdgeBar();
      var placements = new List<IPartPlacement>
      {
        PlaceAt(source, 0, 0D, 2D, 9.6D),
        PlaceAt(source, 0, 0D, 2D, 5.3D),
        PlaceAt(source, 0, 0D, 2D, 1D),
      };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        Profiles(source, new[] { 0, 180 }),
        sheetW: 20D,
        sheetH: 15D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      List<CommonEdge> records = result.Edges.Select(intent => CommonLineGeometryResolver.CreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        CommonLineGap,
        GeometryTolerance)).ToList();

      using (new AssertionScope())
      {
        records.Count(record => Math.Abs(Length(record) - 10D) <= GeometryTolerance).Should().Be(1,
          "the middle part needs pose 180 for its upper ten-unit seam and pose 0 for its lower one");
        result.Edges.Should().OnlyContain(intent => placements.Contains(intent.PartA) && placements.Contains(intent.PartB),
          "every accepted intent must reference the final committed pose objects");
        result.Groups.Where(group => group > 0).Distinct().Should().ContainSingle();
      }
    }

    [Fact]
    public void ArbitraryDiscreteAnglesCreateATriangleCommonLineWithoutCardinalClamping()
    {
      INfp source = Triangle(4D, 2D);
      List<IPartPlacement> placements = FacingBasePair(
        source,
        finalFirstRotation: 37D,
        finalSecondRotation: 217D,
        currentFirstRotation: 0D,
        currentSecondRotation: 0D);

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        Profiles(source, new[] { 0, 37, 217 }),
        sheetW: 40D,
        sheetH: 40D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      CommonEdge record = Record(result);
      using (new AssertionScope())
      {
        Length(record).Should().BeApproximately(4D, GeometryTolerance);
        placements.Select(placement => Normalize(placement.Rotation)).Should().BeEquivalentTo(new[] { 37D, 217D });
        result.Fallbacks.Should().BeEmpty();
      }
    }

    [Fact]
    public void ContinuousPolicyDerivesTheExactFacingAngleFromNeighbourEdges()
    {
      INfp source = Triangle(4D, 2D);
      List<IPartPlacement> placements = FacingBasePair(
        source,
        finalFirstRotation: 17D,
        finalSecondRotation: 197D,
        currentFirstRotation: 17D,
        currentSecondRotation: 43D);
      // Non-common anchors hold the sheet footprint constant so this test isolates the continuous-angle
      // derivation rather than the separate common-line-first finalist objective.
      placements.Add(PlaceAt(Rectangle(0.1D, 0.1D), 1, 0D, 0.5D, 0.5D));
      placements.Add(PlaceAt(Rectangle(0.1D, 0.1D), 1, 0D, 39D, 39D));
      var profiles = new Dictionary<int, SparrowNestService.DirectOrientationProfile>
      {
        [0] = new SparrowNestService.DirectOrientationProfile(
          source,
          RotationCodes.AnglesN(4),
          continuous: true),
      };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        profiles,
        sheetW: 40D,
        sheetH: 40D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      CommonEdge record = Record(result);
      double separation = Math.Abs(Normalize(placements[0].Rotation) - Normalize(placements[1].Rotation));
      separation = Math.Min(separation, 360D - separation);
      using (new AssertionScope())
      {
        separation.Should().BeApproximately(180D, 1e-6,
          "a continuous profile derives the exact opposing pose instead of rounding to 15-degree samples");
        Length(record).Should().BeApproximately(4D, GeometryTolerance);
        placements.Select(placement => Normalize(placement.Rotation)).Should().Contain(17D,
          "the already legal arbitrary Sparrow pose remains available");
      }
    }

    [Fact]
    public void LongerCommonLineWinsBeforeTheSmallerMaterialFootprint()
    {
      INfp source = Triangle(4D, 2D);
      List<IPartPlacement> placements = FacingBasePair(
        source,
        finalFirstRotation: 17D,
        finalSecondRotation: 197D,
        currentFirstRotation: 17D,
        currentSecondRotation: 43D);
      var profiles = new Dictionary<int, SparrowNestService.DirectOrientationProfile>
      {
        [0] = new SparrowNestService.DirectOrientationProfile(
          source,
          RotationCodes.AnglesN(4),
          continuous: true),
      };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        profiles,
        sheetW: 40D,
        sheetH: 40D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance,
        effort: NestEffort.Fast);

      CommonEdge record = Record(result);
      using (new AssertionScope())
      {
        placements.Select(placement => Normalize(placement.Rotation))
          .Should().BeEquivalentTo(new[] { 17D, 197D },
            "the four-unit common line outranks the smaller occupied material box");
        Length(record).Should().BeApproximately(4D, GeometryTolerance,
          "common-line length is the primary nesting objective after all parts fit on the sheet");
      }
    }

    [Fact]
    public void FastPairPruningRetainsAShortMaterialWinnerBeyondFourLongCandidates()
    {
      const double wideNormalGap = 6D;
      INfp bar = Rectangle(10D, 1D);
      var placements = new List<IPartPlacement>
      {
        PlaceAt(bar, 0, 0D, 1D, 1D),
        PlaceAt(bar, 0, 0D, 17D, 8D),
      };
      var rules = new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>
      {
        [0] = (CommonCuttingMode.Unrestricted, 0, "direct", true, wideNormalGap),
      };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        rules,
        Profiles(bar, new[] { 0, 180 }),
        sheetW: 50D,
        sheetH: 30D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance,
        effort: NestEffort.Fast);

      CommonEdge record = Record(result);
      double width = placements.Max(placement => placement.MaxX)
        - placements.Min(placement => placement.MinX);
      double height = placements.Max(placement => placement.MaxY)
        - placements.Min(placement => placement.MinY);
      using (new AssertionScope())
      {
        Length(record).Should().BeApproximately(1D, GeometryTolerance,
          "four longer pose variants must not prune the fifth material-superior side seam");
        (width * height).Should().BeApproximately(20.01D, 0.01D,
          "the short side-by-side seam occupies less material envelope than stacking on a long edge");
        result.GivenUp.Should().Be(0);
      }
    }

    [Fact]
    public void GlobalBeamPrefersTwoShortTriangleSpansOverOneLongerAlternative()
    {
      INfp triangle = Triangle(10D, 3D);
      INfp longBar = Rectangle(8D, 1D);
      INfp shortBar = Rectangle(4.4D, 1D);
      var placements = new List<IPartPlacement>
      {
        PlaceAt(triangle, 0, 0D, 5D, 5D),
        PlaceAt(longBar, 1, 0D, 6D, 3.7D),
        PlaceAt(shortBar, 2, 0D, 5D, 8.3D),
        PlaceAt(shortBar, 3, 0D, 10.6D, 8.3D),
      };
      var rules = new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>();
      for (int source = 0; source < placements.Count; source++)
      {
        rules[source] = (CommonCuttingMode.Unrestricted, source, "direct", true, NormalGap);
      }

      var profiles = new Dictionary<int, SparrowNestService.DirectOrientationProfile>
      {
        [0] = new SparrowNestService.DirectOrientationProfile(triangle, new[] { 0, 180 }),
        [1] = new SparrowNestService.DirectOrientationProfile(longBar, new[] { 0 }),
        [2] = new SparrowNestService.DirectOrientationProfile(shortBar, new[] { 0 }),
        [3] = new SparrowNestService.DirectOrientationProfile(shortBar, new[] { 0 }),
      };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        rules,
        profiles,
        sheetW: 25D,
        sheetH: 15D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance,
        effort: NestEffort.Fast);

      List<CommonEdge> records = result.Edges.Select(intent => CommonLineGeometryResolver.CreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        CommonLineGap,
        GeometryTolerance)).ToList();
      using (new AssertionScope())
      {
        result.Edges.Should().HaveCount(2);
        records.Sum(Length).Should().BeApproximately(8.8D, GeometryTolerance);
        Normalize(placements.Single(placement => placement.Source == 0).Rotation).Should().Be(180D);
        result.Edges.SelectMany(edge => new[] { edge.PartA.Source, edge.PartB.Source })
          .Should().NotContain(1, "the single eight-unit alternative saves less total cut length");
      }
    }

    [Fact]
    public void AcceptedShortAlternativeClearsTheRejectedLongPairsDiagnostics()
    {
      INfp source = Rectangle(0.5D, 0.4D);
      var placements = new List<IPartPlacement>
      {
        PlaceAt(source, 0, 0D, 1D, 1D),
        PlaceAt(source, 0, 0D, 1.8D, 1.7D),
        PlaceAt(Rectangle(0.1D, 0.1D), 1, 0D, 1.1D, 1.6D),
      };
      Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> rules = DirectRules();
      rules[1] = (CommonCuttingMode.None, 1, string.Empty, false, 0D);

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        rules,
        sheetW: 5D,
        sheetH: 5D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      using (new AssertionScope())
      {
        result.Edges.Should().ContainSingle(
          "the blocked 0.5-unit edge must not prevent the safe 0.4-unit edge from being selected");
        Length(Record(result)).Should().BeApproximately(0.4D, GeometryTolerance);
        result.GivenUp.Should().Be(0,
          "the same physical pair ultimately received a valid common edge");
        result.Fallbacks.Should().BeEmpty(
          "diagnostics from a losing candidate must not describe the accepted pair");
      }
    }

    [Fact]
    public void ManualClusterLimitIsHardWhileAutoAllowsTheRequiredTenByTenCapacity()
    {
      SparrowNestService.ResolveCommonLineMaxClusterParts(
        CommonLineDefaults.AutoMaxClusterParts,
        250).Should().Be(100);
      SparrowNestService.ResolveCommonLineMaxClusterParts(6, 250).Should().Be(6);

      INfp source = Rectangle(2D, 1D);
      var placements = Enumerable.Range(0, 6)
        .Select(column => PlaceAt(source, 0, 0D, 1D + (column * (2D + NormalGap)), 1D))
        .ToList();
      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        Profiles(source, new[] { 0 }),
        sheetW: 20D,
        sheetH: 5D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance,
        commonLineMaxClusterParts: 2);

      using (new AssertionScope())
      {
        result.Edges.Should().HaveCount(3);
        result.Groups.Where(group => group > 0)
          .GroupBy(group => group)
          .Should().OnlyContain(group => group.Count() <= 2,
            "a manual limit is enforced by every beam and rollback forest branch");
      }
    }

    [Fact]
    public void TenByTenOrientationAwareGridIsStableAndHasOneRecordPerPhysicalSeam()
    {
      GridRun first = RunGrid();
      GridRun second = RunGrid();

      using (new AssertionScope())
      {
        first.Result.Edges.Should().HaveCount(180,
          "a 10 by 10 grid has 90 horizontal and 90 vertical adjacencies");
        first.Result.Edges.Select(edge => UnorderedPair(first.Placements, edge))
          .Should().OnlyHaveUniqueItems("orientation variants must not duplicate one physical neighbour pair");
        first.Result.Groups.Distinct().Should().Equal(1);
        first.Result.GivenUp.Should().Be(0);
        Snapshot(first).Should().Be(Snapshot(second),
          "candidate ordering and pose selection must be deterministic across identical runs");
      }
    }

    private static GridRun RunGrid()
    {
      const int rows = 10;
      const int columns = 10;
      const double width = 2D;
      const double height = 1D;
      INfp source = Rectangle(width, height);
      var placements = new List<IPartPlacement>(rows * columns);
      for (int row = 0; row < rows; row++)
      {
        for (int column = 0; column < columns; column++)
        {
          placements.Add(PlaceAt(
            source,
            0,
            0D,
            1D + (column * (width + NormalGap)),
            1D + (row * (height + NormalGap))));
        }
      }

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        Profiles(source, new[] { 0, 180 }),
        sheetW: 30D,
        sheetH: 20D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance,
        commonLineMaxClusterParts: CommonLineDefaults.AutoMaxClusterParts);
      return new GridRun(placements, result);
    }

    private static string Snapshot(GridRun run)
    {
      string placements = string.Join(";", run.Placements.Select(placement => FormattableString.Invariant(
        $"{Normalize(placement.Rotation):F6},{placement.X:F6},{placement.Y:F6}")));
      string edges = string.Join(";", run.Result.Edges
        .Select(edge => EdgeSignature(run.Placements, edge))
        .OrderBy(value => value, StringComparer.Ordinal));
      return placements + "|" + edges;
    }

    private static string EdgeSignature(
      IReadOnlyList<IPartPlacement> placements,
      SparrowNestService.CommonEdgeIntent edge)
    {
      int a = IndexOfReference(placements, edge.PartA);
      int b = IndexOfReference(placements, edge.PartB);
      if (a <= b)
      {
        return FormattableString.Invariant($"{a}:{edge.EdgeAIndex}-{b}:{edge.EdgeBIndex}");
      }

      return FormattableString.Invariant($"{b}:{edge.EdgeBIndex}-{a}:{edge.EdgeAIndex}");
    }

    private static string UnorderedPair(
      IReadOnlyList<IPartPlacement> placements,
      SparrowNestService.CommonEdgeIntent edge)
    {
      int a = IndexOfReference(placements, edge.PartA);
      int b = IndexOfReference(placements, edge.PartB);
      return a <= b
        ? FormattableString.Invariant($"{a}-{b}")
        : FormattableString.Invariant($"{b}-{a}");
    }

    private static int IndexOfReference(IReadOnlyList<IPartPlacement> placements, IPartPlacement sought)
    {
      for (int i = 0; i < placements.Count; i++)
      {
        if (ReferenceEquals(placements[i], sought))
        {
          return i;
        }
      }

      return -1;
    }

    private static List<IPartPlacement> VerticalLongEdgePair(INfp source)
      => new List<IPartPlacement>
      {
        PlaceAt(source, 0, 0D, 2D, 5.3D),
        PlaceAt(source, 0, 0D, 2D, 1D),
      };

    private static Dictionary<int, SparrowNestService.DirectOrientationProfile> Profiles(
      INfp source,
      IEnumerable<int> angles)
      => new Dictionary<int, SparrowNestService.DirectOrientationProfile>
      {
        [0] = new SparrowNestService.DirectOrientationProfile(source, angles),
      };

    private static Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> DirectRules()
      => new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>
      {
        [0] = (CommonCuttingMode.Unrestricted, 0, "direct", true, NormalGap),
      };

    private static CommonEdge Record(SparrowNestService.DirectCommonLineSnapResult result)
    {
      SparrowNestService.CommonEdgeIntent intent = result.Edges.Should().ContainSingle().Subject;
      return CommonLineGeometryResolver.CreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        CommonLineGap,
        GeometryTolerance);
    }

    private static double Length(CommonEdge edge)
    {
      double dx = edge.End.X - edge.Start.X;
      double dy = edge.End.Y - edge.Start.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static IPartPlacement PlaceAt(
      INfp part,
      int source,
      double rotation,
      double minX,
      double minY)
      => new PartPlacement(part)
      {
        Source = source,
        Rotation = rotation,
        X = minX - part.MinX,
        Y = minY - part.MinY,
      };

    private static double Normalize(double angle)
    {
      double result = angle % 360D;
      return result < 0D ? result + 360D : result;
    }

    private static INfp Offset(INfp source, double x, double y)
      => new NoFitPolygon(source.Points.Select(point => new SvgPoint(point.X + x, point.Y + y)).ToArray());

    private static INfp OneLongEdgeBar()
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0D, 0D),
        new SvgPoint(10D, 0D),
        new SvgPoint(10D, 4D),
        new SvgPoint(2D, 4D),
        new SvgPoint(2D, 3D),
        new SvgPoint(0D, 3D),
      });

    private static INfp HodgsonOutline()
      => new NoFitPolygon(new[]
      {
        new SvgPoint(10D, 0D),
        new SvgPoint(9.625D, 0D),
        new SvgPoint(9.625D, 0.375D),
        new SvgPoint(8.375D, 0.375D),
        new SvgPoint(8.375D, 0D),
        new SvgPoint(1.625D, 0D),
        new SvgPoint(1.625D, 0.375D),
        new SvgPoint(0.375D, 0.375D),
        new SvgPoint(0.375D, 0D),
        new SvgPoint(0D, 0D),
        new SvgPoint(0D, 1.375D),
        new SvgPoint(10D, 1.375D),
      });

    private static List<IPartPlacement> FacingBasePair(
      INfp source,
      double finalFirstRotation,
      double finalSecondRotation,
      double currentFirstRotation,
      double currentSecondRotation)
    {
      INfp finalFirst = source.Rotate(finalFirstRotation);
      INfp finalSecond = source.Rotate(finalSecondRotation);
      const double firstX = 15D;
      const double firstY = 15D;
      SvgPoint aStart = finalFirst.Points[0];
      SvgPoint aEnd = finalFirst.Points[1];
      double edgeLength = Math.Sqrt(
        Math.Pow(aEnd.X - aStart.X, 2D)
        + Math.Pow(aEnd.Y - aStart.Y, 2D));
      double normalX = (aEnd.Y - aStart.Y) / edgeLength;
      double normalY = -(aEnd.X - aStart.X) / edgeLength;
      SvgPoint bEnd = finalSecond.Points[1];
      double secondX = firstX + aStart.X + (NormalGap * normalX) - bEnd.X;
      double secondY = firstY + aStart.Y + (NormalGap * normalY) - bEnd.Y;
      double firstCentreX = firstX + ((finalFirst.MinX + finalFirst.MaxX) / 2D);
      double firstCentreY = firstY + ((finalFirst.MinY + finalFirst.MaxY) / 2D);
      double secondCentreX = secondX + ((finalSecond.MinX + finalSecond.MaxX) / 2D);
      double secondCentreY = secondY + ((finalSecond.MinY + finalSecond.MaxY) / 2D);
      INfp currentFirst = source.Rotate(currentFirstRotation);
      INfp currentSecond = source.Rotate(currentSecondRotation);
      return new List<IPartPlacement>
      {
        PlaceAtCentre(currentFirst, 0, currentFirstRotation, firstCentreX, firstCentreY),
        PlaceAtCentre(currentSecond, 0, currentSecondRotation, secondCentreX, secondCentreY),
      };
    }

    private static IPartPlacement PlaceAtCentre(
      INfp part,
      int source,
      double rotation,
      double centreX,
      double centreY)
      => new PartPlacement(part)
      {
        Source = source,
        Rotation = rotation,
        X = centreX - ((part.MinX + part.MaxX) / 2D),
        Y = centreY - ((part.MinY + part.MaxY) / 2D),
      };

    private static INfp Triangle(double width, double height)
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0D, 0D),
        new SvgPoint(width, 0D),
        new SvgPoint(width * 0.4D, height),
      });

    private static INfp Rectangle(double width, double height)
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0D, 0D),
        new SvgPoint(width, 0D),
        new SvgPoint(width, height),
        new SvgPoint(0D, height),
      });

    private sealed class GridRun
    {
      public GridRun(List<IPartPlacement> placements, SparrowNestService.DirectCommonLineSnapResult result)
      {
        this.Placements = placements;
        this.Result = result;
      }

      public List<IPartPlacement> Placements { get; }

      public SparrowNestService.DirectCommonLineSnapResult Result { get; }
    }
  }
}
