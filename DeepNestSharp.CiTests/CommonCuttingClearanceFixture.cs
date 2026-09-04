namespace DeepNestSharp.CiTests
{
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Hand editing has to judge a pair the same way the nester did, or the operator opens a nest the engine
  /// built correctly and finds it painted red with the exports refused.
  /// <para>
  /// This is the half that used to be missing. A common-line part reached the viewer with its spacing
  /// already flattened to 0, so "may these touch" never had to be asked. Now the part keeps its real
  /// spacing and the mode answers instead, which means the viewer needs the mode too.
  /// </para>
  /// </summary>
  public class CommonCuttingClearanceFixture
  {
    private const string A = @"C:\parts\bracket.dxf";
    private const string B = @"C:\parts\plate.dxf";
    private const string Direct = "direct";

    [Fact]
    public void UnrestrictedSharesWithAnyOtherSharingPart()
    {
      DxfViewer.MayShare(CommonCuttingMode.Unrestricted, A, false, Direct, CommonCuttingMode.Unrestricted, B, false, Direct)
        .Should().BeTrue("unrestricted means any part that also allows it");
    }

    [Fact]
    public void NothingSharesWithAPartSetToNone()
    {
      DxfViewer.MayShare(CommonCuttingMode.Unrestricted, A, false, Direct, CommonCuttingMode.None, A, false, Direct)
        .Should().BeFalse("a part set to None keeps its clearance even from its own copies");
    }

    [Fact]
    public void SamePartSharesOnlyWithItsOwnDrawing()
    {
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, false, Direct, CommonCuttingMode.SamePart, A, false, Direct)
        .Should().BeTrue();
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, false, Direct, CommonCuttingMode.SamePart, B, false, Direct)
        .Should().BeFalse("a different drawing is a different part");
    }

    /// <summary>The stricter mode wins, whichever side it is on. Unrestricted does not get to drag a
    /// Same-part neighbour into a shared cut just by being permissive.</summary>
    [Fact]
    public void TheStricterModeDecidesWhicheverSideItIsOn()
    {
      DxfViewer.MayShare(CommonCuttingMode.Unrestricted, A, false, Direct, CommonCuttingMode.SamePart, B, false, Direct)
        .Should().BeFalse();
      DxfViewer.MayShare(CommonCuttingMode.SamePart, B, false, Direct, CommonCuttingMode.Unrestricted, A, false, Direct)
        .Should().BeFalse("and it must not depend on the argument order");
    }

    /// <summary>A mirrored copy is the other hand: same file, different part. Matches how the nester keys
    /// its share groups, on (path, mirrored).</summary>
    [Fact]
    public void AMirroredCopyIsADifferentPart()
    {
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, false, Direct, CommonCuttingMode.SamePart, A, true, Direct)
        .Should().BeFalse();
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, true, Direct, CommonCuttingMode.SamePart, A, true, Direct)
        .Should().BeTrue("two mirrored copies are each other's own kind");
    }

    [Fact]
    public void PathsAreComparedCaseInsensitively()
    {
      DxfViewer.MayShare(CommonCuttingMode.SamePart, @"C:\Parts\Bracket.DXF", false, Direct, CommonCuttingMode.SamePart, A, false, Direct)
        .Should().BeTrue("Windows paths are the same file in either case");
    }

    [Theory]
    [InlineData(null, "direct")]
    [InlineData("", "direct")]
    [InlineData("   ", "direct")]
    [InlineData("direct", "sheetcam:job.nest")]
    public void SharingRequiresTheSameNonEmptyContract(string domainA, string domainB)
    {
      DxfViewer.MayShare(
        CommonCuttingMode.Unrestricted, A, false, domainA,
        CommonCuttingMode.Unrestricted, B, false, domainB).Should().BeFalse();
    }

    [Fact]
    public void ContractComparisonIsCaseInsensitive()
    {
      DxfViewer.MayShare(
        CommonCuttingMode.Unrestricted, A, false, "SheetCam:C:\\Jobs\\A.nest",
        CommonCuttingMode.Unrestricted, B, false, "sheetcam:c:\\jobs\\a.NEST").Should().BeTrue();
    }

    /// <summary>
    /// The wiring, and the reason this commit cannot ship apart from the one before it. Two copies the
    /// nester deliberately placed touching must be asked for NO clearance; the same two parts without the
    /// mode must be asked for the full 0.25, which is what would have marked the whole layout red.
    /// </summary>
    [Fact]
    public void OnlyIntentionalPairsUseTheConfiguredCommonLineGap()
    {
      DxfViewer.ClearanceFor(0.25, 0.25, intentionalCommonEdge: true)
        .Should().Be(CommonLineDefaults.DefaultGapInches);
      DxfViewer.ClearanceFor(0.25, 0.25, intentionalCommonEdge: true, commonLineGap: 0.015)
        .Should().Be(0.015);
      DxfViewer.ClearanceFor(0.25, 0.25, intentionalCommonEdge: false).Should().BeApproximately(0.25, 1e-9);
      DxfViewer.ClearanceFor(0.5, 0.1, intentionalCommonEdge: false).Should().BeApproximately(0.3, 1e-9, "still the average of the two");
    }

    [Fact]
    public void OnlySameJobSheetCamPairsKeepTheLegacyMeasuredKerfClearance()
    {
      DxfViewer.TryLegacySheetCamClearance(
        CommonCuttingMode.Unrestricted, A, false, "sheetcam:C:\\jobs\\one.nest", 0.03,
        CommonCuttingMode.Unrestricted, B, false, "SHEETCAM:c:\\JOBS\\ONE.NEST", 0.04,
        out double clearance).Should().BeTrue();
      clearance.Should().Be(0.04);

      DxfViewer.TryLegacySheetCamClearance(
        CommonCuttingMode.Unrestricted, A, false, Direct, 0.03,
        CommonCuttingMode.Unrestricted, B, false, Direct, 0.04,
        out _).Should().BeFalse("direct nominal contours require an explicit CommonEdge record");
      DxfViewer.TryLegacySheetCamClearance(
        CommonCuttingMode.Unrestricted, A, false, "sheetcam:one.nest", 0.03,
        CommonCuttingMode.Unrestricted, B, false, "sheetcam:two.nest", 0.04,
        out _).Should().BeFalse("toolpaths from different jobs cannot share a return cut");
    }

    [Fact]
    public void CommonLineClusterMembersAreLockedButOrdinaryPlacementsRemainEditable()
    {
      var record = new CommonEdge(
        10, 11, 1, 3,
        new CommonLinePoint(10.005, 0),
        new CommonLinePoint(10.005, 10),
        0.01);

      DxfViewer.CanManuallyEdit(10, new[] { record }).Should().BeFalse();
      DxfViewer.CanManuallyEdit(11, new[] { record }).Should().BeFalse();
      DxfViewer.CanManuallyEdit(12, new[] { record }).Should().BeTrue();
      DxfViewer.CanManuallyEdit(10, null).Should().BeTrue();
    }

    [Fact]
    public void AGeometricallyPlausibleButAbsoluteStaleRecordDoesNotValidate()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      var square = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(0, 10),
      });
      var a = new PartPlacement(square) { Id = 1, X = 0, Y = 0 };
      var b = new PartPlacement(square) { Id = 2, X = 10 + gap, Y = 0 };
      CommonEdge rebuilt = CommonLineGeometryResolver.CreateCommonEdge(a, 1, b, 3, gap, tolerance);
      var stale = new CommonEdge(
        rebuilt.PartAId,
        rebuilt.PartBId,
        rebuilt.EdgeAIndex,
        rebuilt.EdgeBIndex,
        new CommonLinePoint(rebuilt.Start.X + 1, rebuilt.Start.Y),
        new CommonLinePoint(rebuilt.End.X + 1, rebuilt.End.Y),
        rebuilt.Gap);

      DxfViewer.SameUndirectedRecord(rebuilt, rebuilt, tolerance).Should().BeTrue();
      DxfViewer.SameUndirectedRecord(stale, rebuilt, tolerance).Should().BeFalse(
        "the indexed geometry alone is insufficient when the persisted absolute seam moved");

      var sheet = new SheetPlacement(
        PlacementTypeEnum.Gravity,
        Sheet.NewSheet(1, 40, 20),
        new IPartPlacement[] { a, b },
        0,
        requiresMergedLines: false,
        commonEdges: new[] { stale },
        commonLineGap: gap,
        geometryTolerance: tolerance);
      var valid = DxfViewer.ValidRecordedEdges(sheet, gap, tolerance);
      valid.Should().BeEmpty();
      var unfit = new PlacementAcceptance.Unfit();
      DxfViewer.MarkInvalidCommonEdgeRecords(sheet, valid.Count, unfit);
      unfit.All.Should().BeEquivalentTo(new IPartPlacement[] { a, b },
        "stale common-line metadata is itself unfit to cut even if ordinary spacing were zero");
    }
  }
}
