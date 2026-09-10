namespace DeepNestSharp.CiTests
{
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Nest report's page is zoomed, panned and fitted the way the nest viewer does it, so the two feel the same.
  /// These pin that arithmetic as invariants: what a zoom keeps still, where zooming stops, and what a fit shows.
  /// </summary>
  public class ReportPageViewFixture
  {
    /// <summary>The point of the page under the cursor is the point under the cursor after the zoom too, in or out.</summary>
    [Theory]
    [InlineData(120)]
    [InlineData(-120)]
    public void ZoomingKeepsThePointUnderTheCursorWhereItIs(int wheelDelta)
    {
      const double scale = 1.5, x = 40, y = -25, cursorX = 300, cursorY = 180;
      double pageX = (cursorX - x) / scale; // screen = page * scale + translate
      double pageY = (cursorY - y) / scale;

      var (newScale, newX, newY) = ReportPageView.ZoomAt(scale, x, y, cursorX, cursorY, wheelDelta);

      newScale.Should().BeApproximately(wheelDelta > 0 ? scale * 1.2 : scale / 1.2, 1e-12, "one notch is 1.2, as in the viewer");
      ((pageX * newScale) + newX).Should().BeApproximately(cursorX, 1e-9, "the page point under the cursor stays under it");
      ((pageY * newScale) + newY).Should().BeApproximately(cursorY, 1e-9, "the page point under the cursor stays under it");
    }

    [Fact]
    public void ZoomStopsAtTheViewersLimits()
    {
      ReportPageView.ZoomAt(399, 0, 0, 10, 10, 120).Scale.Should().Be(400, "the viewer zooms in no further than 400");
      ReportPageView.ZoomAt(0.021, 0, 0, 10, 10, -120).Scale.Should().Be(0.02, "and out no further than 0.02");
    }

    /// <summary>A fit shows the whole page, fills 92 % of the tighter direction and leaves equal margins on both sides.</summary>
    [Theory]
    [InlineData(1600, 700)] // a wide room: the height decides
    [InlineData(700, 1600)] // a tall room: the width decides
    public void FittingShowsTheWholePageCentredAtNinetyTwoPercentOfTheRoom(double roomW, double roomH)
    {
      const double pageW = 792, pageH = 612;

      var (scale, x, y) = ReportPageView.Fit(pageW, pageH, roomW, roomH);

      double shownW = pageW * scale;
      double shownH = pageH * scale;
      shownW.Should().BeLessOrEqualTo(roomW);
      shownH.Should().BeLessOrEqualTo(roomH);
      System.Math.Max(shownW / roomW, shownH / roomH).Should().BeApproximately(0.92, 1e-12, "the tighter direction is filled to 92 %");
      x.Should().BeApproximately(roomW - (x + shownW), 1e-9, "as much room left as right");
      y.Should().BeApproximately(roomH - (y + shownH), 1e-9, "as much room above as below");
    }
  }
}
