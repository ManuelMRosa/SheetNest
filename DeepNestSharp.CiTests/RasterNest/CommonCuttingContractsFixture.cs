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
      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, "other", 1)).Should().BeFalse();
      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, "DIRECT", 99)).Should().BeTrue(
        "Windows/job domains compare case-insensitively and Unrestricted ignores part identity");
      RasterCompact.MayShare(first, Item(CommonCuttingMode.None, "direct", 1)).Should().BeFalse();
    }

    [Fact]
    public void SamePartStillRequiresTheSameShareKeyInsideTheDomain()
    {
      var first = Item(CommonCuttingMode.SamePart, "direct", 7);

      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, "direct", 7))
        .Should().BeTrue();
      RasterCompact.MayShare(first, Item(CommonCuttingMode.Unrestricted, "direct", 8))
        .Should().BeFalse();
      RasterCompact.MayShare(first, Item(CommonCuttingMode.SamePart, "other", 7))
        .Should().BeFalse("a matching part key cannot cross contracts");
    }

    /// <summary>
    /// Permission to share is not itself an exception. Two parts that may share a cut still keep the
    /// ordinary averaged spacing everywhere they do not own a recorded edge, so a pair the planner
    /// considered and rejected stays cuttable rather than quietly tightening.
    /// </summary>
    [Fact]
    public void FinalAcceptanceGivesNoDiscountWithoutARecordedEdge()
    {
      SparrowNestService.AcceptanceClearanceFor(0.30, 0.30)
        .Should().Be(0.30, "a pair needs a concrete edge record before any clearance exception");

      SparrowNestService.AcceptanceClearanceFor(0.20, 0.40)
        .Should().BeApproximately(0.30, 1e-12, "two spacings meet halfway, so neither part gets less than it asked for");
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

    /// <summary>
    /// Turning common cutting on is not permission to quietly restrict how a part may be turned. The
    /// planner works with whatever orientations the operator allowed; it does not narrow them to the
    /// ones that would make a seam easier to find.
    /// </summary>
    [Fact]
    public void CommonLineEligibilityNeverNarrowsTheRotationPolicy()
    {
      SparrowNestService.CommonLineRotationCode(8, requiresMergedLines: false).Should().Be(8);
      SparrowNestService.CommonLineRotationCode(8, requiresMergedLines: true).Should().Be(8,
        "common-line geometry must keep every orientation the user allowed");
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

  }
}
