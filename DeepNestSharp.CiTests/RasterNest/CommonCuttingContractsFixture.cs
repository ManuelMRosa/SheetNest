namespace DeepNestSharp.CiTests.RasterNest
{
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  public class CommonCuttingContractsFixture
  {
    [Fact]
    public void SharingRequiresTheSameNonEmptyDomainBeforeModesAreConsidered()
    {
      var first = Item(CommonCuttingMode.Unrestricted, "direct", 1);

      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, null, 1)).Should().BeFalse();
      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, string.Empty, 1)).Should().BeFalse();
      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, "sheetcam:job.nest", 1)).Should().BeFalse();
      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, "DIRECT", 99)).Should().BeTrue(
        "Windows/job domains compare case-insensitively and Unrestricted ignores part identity");
      RasterCompact.MayShare(first, Item(CommonCuttingMode.None, "direct", 1)).Should().BeFalse();
    }

    [Fact]
    public void SamePartStillRequiresTheSameShareKeyInsideTheDomain()
    {
      var first = Item(CommonCuttingMode.SamePart, "sheetcam:C:\\jobs\\one.nest", 7);

      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, "sheetcam:C:\\jobs\\one.nest", 7))
        .Should().BeTrue();
      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, "sheetcam:C:\\jobs\\one.nest", 8))
        .Should().BeFalse();
      RasterCompact.MayShare(first, Item(CommonCuttingMode.SamePart, "sheetcam:C:\\jobs\\two.nest", 7))
        .Should().BeFalse("a matching drawing key cannot cross SheetCam jobs");
    }

    [Fact]
    public void FinalAcceptanceKeepsSheetCamAtMeasuredKerfEvenWhenNormalSpacingIsLarger()
    {
      SparrowNestService.AcceptanceClearanceFor(
        0.30, "sheetcam:C:\\jobs\\one.nest", CommonCuttingMode.Unrestricted, 1, 0.04,
        0.30, "SHEETCAM:c:\\JOBS\\ONE.NEST", CommonCuttingMode.Unrestricted, 2, 0.03)
        .Should().Be(0.04);

      SparrowNestService.AcceptanceClearanceFor(
        0.30, "direct", CommonCuttingMode.Unrestricted, 1, 0.04,
        0.30, "direct", CommonCuttingMode.Unrestricted, 2, 0.03)
        .Should().Be(0.30, "direct parts need a concrete V1 edge before any clearance exception");
    }

    [Fact]
    public void ExactKerfSnapCannotCrossDomains()
    {
      const double Kerf = 0.3;
      var outline = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(2, 0), new SvgPoint(2, 10), new SvgPoint(0, 10),
      });
      var first = new PartPlacement(outline) { Source = 0, X = 0, Y = 0 };
      var second = new PartPlacement(outline) { Source = 1, X = 2.35, Y = 0.2 };
      var tooling = SparrowNestService.ToolingFootprint(outline, null, Kerf, 0);

      SparrowNestService.SnapCommonLineEdges(
        new List<IPartPlacement> { first, second },
        new Dictionary<int, double> { { 0, Kerf }, { 1, Kerf } },
        new Dictionary<int, INfp> { { 0, tooling }, { 1, tooling } },
        new Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain)>
        {
          { 0, (CommonCuttingMode.Unrestricted, 0, "direct") },
          { 1, (CommonCuttingMode.Unrestricted, 1, "sheetcam:test") },
        });

      second.X.Should().Be(2.35);
      second.Y.Should().Be(0.2, "equal modes are insufficient when their geometry/output contracts differ");
    }

    [Fact]
    public void DirectSeamsCountCompatiblePairsWithPositiveStraightOverlapOnce()
    {
      var placements = new List<IPartPlacement>
      {
        Place(Square(), 0, 0, 0),
        Place(SquareWithSplitLeftEdge(), 1, 10, 0),
        Place(Square(), 2, 10, 10),
      };
      var rules = new Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines)>
      {
        { 0, (CommonCuttingMode.Unrestricted, 0, "direct", true) },
        { 1, (CommonCuttingMode.Unrestricted, 1, "direct", true) },
        { 2, (CommonCuttingMode.Unrestricted, 2, "direct", true) },
      };

      SparrowNestService.CountDirectCommonLineSeams(placements, rules).Should().Be(2,
        "A-B and B-C share real edges; A-C only meet at one point, and B's split edge is one pair/seam");

      rules[2] = (CommonCuttingMode.Unrestricted, 2, "sheetcam:other.nest", true);
      SparrowNestService.CountDirectCommonLineSeams(placements, rules).Should().Be(1,
        "the geometrically coincident B-C edge cannot cross output domains");
    }

    [Fact]
    public void DirectSeamCountIgnoresGeometryThatDoesNotRequireMergedOutput()
    {
      var placements = new List<IPartPlacement>
      {
        Place(Square(), 0, 0, 0),
        Place(Square(), 1, 10, 0),
      };
      var rules = new Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines)>
      {
        { 0, (CommonCuttingMode.Unrestricted, 0, "sheetcam:test", false) },
        { 1, (CommonCuttingMode.Unrestricted, 1, "sheetcam:test", false) },
      };

      SparrowNestService.CountDirectCommonLineSeams(placements, rules).Should().Be(0,
        "SheetCam's already-offset contract is counted by its kerf snap, not by coincident direct contours");
    }

    [Fact]
    public void SheetWithoutAnIntentionalSeamDoesNotRequireMergedLines()
    {
      var ordinary = Place(Square(), 0, 0, 0);
      var sheet = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(1, 20, 20),
        new List<IPartPlacement> { ordinary },
        0,
        requiresMergedLines: false,
        commonEdges: null);

      sheet.RequiresMergedLines.Should().BeFalse(
        "opting a source into common cutting must not force legacy line merging when no edge was produced");
    }

    [Fact]
    public void EntryLeadWarningsAreOptInAndNameOnlyValidatedSources()
    {
      var placements = new List<IPartPlacement>
      {
        Place(Square(), 0, 0, 0),
        Place(Square(), 1, 10, 0),
      };
      var entry = new Dictionary<int, (bool HasEntryLead, bool ValidateEntryLead, string Drawing)>
      {
        { 0, (false, false, "direct-dxf") },
        { 1, (false, true, "sheetcam-part") },
      };

      SparrowNestService.FindNoEntryDrawings(placements, new List<List<int>> { new List<int> { 0, 1 } }, entry)
        .Should().Equal("sheetcam-part");

      entry[0] = (true, false, "direct-dxf");
      SparrowNestService.FindNoEntryDrawings(placements, new List<List<int>> { new List<int> { 0, 1 } }, entry)
        .Should().BeEmpty("one exterior lead anywhere in a validated cut chain is sufficient");
    }

    [Fact]
    public void StandaloneDirectPartWithoutToolingDoesNotProduceNoEntryWarning()
    {
      var placements = new List<IPartPlacement> { Place(Square(), 0, 0, 0) };
      var entry = new Dictionary<int, (bool HasEntryLead, bool ValidateEntryLead, string Drawing)>
      {
        { 0, (false, false, "plain.dxf") },
      };

      SparrowNestService.FindNoEntryDrawings(placements, null, entry).Should().BeEmpty();

      entry[0] = (false, true, "sheetcam-part");
      SparrowNestService.FindNoEntryDrawings(placements, null, entry).Should().Equal("sheetcam-part");
    }

    private static CompactItem Item(CommonCuttingMode mode, string domain, int shareKey)
      => new CompactItem { Cc = mode, CommonCuttingDomain = domain, ShareKey = shareKey };

    private static IPartPlacement Place(INfp part, int source, double x, double y)
      => new PartPlacement(part) { Source = source, X = x, Y = y };

    private static INfp Square()
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(0, 10),
      });

    private static INfp SquareWithSplitLeftEdge()
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(0, 10),
        new SvgPoint(0, 5),
      });
  }
}
