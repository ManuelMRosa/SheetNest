namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Manual editing may now leave a part on top of a neighbour or off the sheet — that is what makes
  /// rearranging a full sheet possible — so the check that used to refuse the edit lives at the exit
  /// instead: nothing is exported while a layout is still wrong. This is that check.
  /// </summary>
  public class UnfitLayoutFixture
  {
    private const double SheetW = 100;
    private const double SheetH = 50;
    private const double NoMargin = 0;

    /// <summary>The clearance rule, standing in for the viewer's (spacingA + spacingB) / 2.</summary>
    private static readonly Func<IPartPlacement, IPartPlacement, double> Clear = (a, b) => 0.25;

    [Fact]
    public void ACleanSheetIsFitToCut()
    {
      var parts = new List<IPartPlacement> { Square(0, 0), Square(20, 0), Square(40, 0) };

      PlacementAcceptance.CountUnfit(parts, SheetW, SheetH, NoMargin, Clear).Should().Be(0);
    }

    [Fact]
    public void BothSidesOfAnOverlapCount()
    {
      // The same two parts both turn red on screen, so both are counted here.
      var parts = new List<IPartPlacement> { Square(0, 0), Square(5, 0), Square(40, 0) };

      PlacementAcceptance.CountUnfit(parts, SheetW, SheetH, NoMargin, Clear).Should().Be(2);
    }

    [Fact]
    public void APartHangingOffTheSheetCounts()
    {
      var parts = new List<IPartPlacement> { Square(0, 0), Square(95, 0) };

      PlacementAcceptance.CountUnfit(parts, SheetW, SheetH, NoMargin, Clear).Should().Be(1);
    }

    /// <summary>
    /// The trap this check exists to avoid: the viewer's own bounds test measures against the sheet being
    /// LOOKED AT. Validating a whole job that way would judge every layout by the visible one's size. The
    /// same parts must come out fit or unfit purely by the size of THEIR sheet.
    /// </summary>
    [Fact]
    public void APartIsJudgedBySheetItSitsOnNotAnotherOne()
    {
      var parts = new List<IPartPlacement> { Square(0, 0), Square(95, 0) };

      PlacementAcceptance.CountUnfit(parts, 100, SheetH, NoMargin, Clear).Should().Be(1, "it runs off a 100 wide sheet");
      PlacementAcceptance.CountUnfit(parts, 200, SheetH, NoMargin, Clear).Should().Be(0, "the very same parts fit a 200 wide one");
    }

    /// <summary>
    /// The engine keeps every part this far from the sheet edge, and editing by hand must not undo that.
    /// The pair of assertions is what proves the margin is applied rather than quietly ignored.
    /// </summary>
    [Fact]
    public void APartStandingInTheEdgeMarginIsNotFitToCut()
    {
      var parts = new List<IPartPlacement> { Square(1, 1) };

      PlacementAcceptance.CountUnfit(parts, SheetW, SheetH, 2, Clear).Should().Be(1, "a margin of 2 leaves it inside the band");
      PlacementAcceptance.CountUnfit(parts, SheetW, SheetH, NoMargin, Clear).Should().Be(0, "with no margin the same part is fine");
    }

    /// <summary>The engine anchors its pack exactly at the margin, so a part resting right on it is the
    /// ordinary case and must not read as a violation.</summary>
    [Fact]
    public void APartRestingExactlyOnTheMarginIsFit()
    {
      var parts = new List<IPartPlacement> { Square(2, 2) };

      PlacementAcceptance.CountUnfit(parts, SheetW, SheetH, 2, Clear).Should().Be(0);
    }

    /// <summary>Common-line parts nest touching by design, so a near-zero clearance must not read as an
    /// overlap — otherwise every common-line job would refuse to export.</summary>
    [Fact]
    public void PartsAlmostTouchingAtZeroClearanceAreNotAnOverlap()
    {
      var parts = new List<IPartPlacement> { Square(0, 0), Square(10.001, 0) };

      PlacementAcceptance.CountUnfit(parts, SheetW, SheetH, NoMargin, (a, b) => 0).Should().Be(0);
    }

    /// <summary>
    /// The usable area over plain numbers — the form the drag's cached bounds are judged by. The rule was
    /// written out twice and the copies drifted: the cached path went on testing the raw sheet edge after
    /// the margin arrived, so dragging and dropping the same part disagreed about where red begins.
    /// </summary>
    [Fact]
    public void CachedBoundsInsideTheEdgeMarginAreOutsideTheUsableArea()
    {
      PlacementAcceptance.IsOutsideUsableArea(1, 1, 11, 11, SheetW, SheetH, 2)
        .Should().BeTrue("a margin of 2 leaves those bounds inside the band");
      PlacementAcceptance.IsOutsideUsableArea(1, 1, 11, 11, SheetW, SheetH, NoMargin)
        .Should().BeFalse("the very same bounds are fine with no margin");
    }

    /// <summary>The two faults are not the same job. An overlap gets moved by hand; a part in the margin
    /// usually means the margin wants lowering and the nest re-running. So the refusal has to tell them
    /// apart instead of putting both in one sentence.</summary>
    [Fact]
    public void TheTwoFaultsAreToldApart()
    {
      var overlapping = PlacementAcceptance.FindUnfit(new List<IPartPlacement> { Square(0, 0), Square(5, 0) }, SheetW, SheetH, NoMargin, Clear);
      overlapping.Overlapping.Count.Should().Be(2);
      overlapping.OutsideMargin.Should().BeEmpty();

      var inMargin = PlacementAcceptance.FindUnfit(new List<IPartPlacement> { Square(1, 1) }, SheetW, SheetH, 2, Clear);
      inMargin.OutsideMargin.Count.Should().Be(1);
      inMargin.Overlapping.Should().BeEmpty();
    }

    /// <summary>A part can be both at once, and the total is people, not faults: it turns red once and it
    /// is one part to go and fix.</summary>
    [Fact]
    public void APartWithBothFaultsIsStillOnePart()
    {
      var found = PlacementAcceptance.FindUnfit(new List<IPartPlacement> { Square(1, 1), Square(6, 1) }, SheetW, SheetH, 2, Clear);

      found.Overlapping.Count.Should().Be(2);
      found.OutsideMargin.Count.Should().Be(2);
      found.All.Count.Should().Be(2, "the same two parts, not four");
    }

    [Fact]
    public void AnEmptySheetIsFit()
    {
      PlacementAcceptance.CountUnfit(new List<IPartPlacement>(), SheetW, SheetH, NoMargin, Clear).Should().Be(0);
    }

    private static IPartPlacement Square(double x, double y, double side = 10)
    {
      var poly = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(side, 0),
        new SvgPoint(side, side),
        new SvgPoint(0, side),
      });

      return new PartPlacement(poly) { X = x, Y = y };
    }
  }
}
