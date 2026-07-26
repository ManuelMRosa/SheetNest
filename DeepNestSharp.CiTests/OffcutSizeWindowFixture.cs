namespace DeepNestSharp.CiTests
{
  using DeepNestSharp.RasterNest;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The size a remnant has to come out at to be worth cutting off — SheetCam's remnant cutoff, asked for
  /// as "vamos a ponerle estos settings desde minimum x size para abajo": a minimum and a maximum on each
  /// axis, plus how far the cut runs past the sheet edge.
  /// <para>A 120 x 60 sheet throughout, which is the one in the screenshot, so the numbers under test are
  /// the ones Manuel actually types.</para>
  /// </summary>
  public class OffcutSizeWindowFixture
  {
    private const double W = 120;
    private const double H = 60;

    /// <summary>
    /// Nothing set must behave exactly as it did before any of this existed: cut where the pack ends, and
    /// only when the free strip is worth at least 5% of the side. This is the test that protects everyone
    /// who updates without ever opening the dialog.
    /// </summary>
    [Theory]
    [InlineData(30, 30)]      // 90 free
    [InlineData(90, 90)]      // 30 free
    [InlineData(114, 114)]    // exactly 6 free = the 5% rule's floor
    [InlineData(114.5, null)] // 5.5 free - under the floor, so no cut, exactly as before
    public void WithNothingSetTheAnswerIsTheOldOne(double extentX, double? expectedCut)
    {
      var (cutX, cutY) = OffcutGeometry.CutPositionsCore(extentX, H, W, H, 0, OffcutDirection.End);

      cutX.Should().Be(expectedCut);
      cutY.Should().BeNull("End on a wide sheet cuts across the long axis only");
    }

    /// <summary>A remnant narrower than the minimum is not worth handling, so the sheet is left uncut. The
    /// second half is what proves the number is being used rather than ignored.</summary>
    [Fact]
    public void ARemnantUnderTheMinimumIsNotCutOff()
    {
      var withMin = OffcutGeometry.CutPositionsCore(110, H, W, H, 0, OffcutDirection.End, new OffcutLimits(20, 0, 0, 0));
      var without = OffcutGeometry.CutPositionsCore(110, H, W, H, 0, OffcutDirection.End);

      withMin.CutX.Should().BeNull("the remnant would only be 10 wide");
      without.CutX.Should().Be(110, "and with no minimum set the very same sheet is cut");
    }

    /// <summary>
    /// Past the maximum the leftover is not a remnant, it is very nearly a sheet — Manuel's call: do not
    /// cut, keep the sheet whole.
    /// </summary>
    [Fact]
    public void ARemnantOverTheMaximumLeavesTheSheetWhole()
    {
      var withMax = OffcutGeometry.CutPositionsCore(30, H, W, H, 0, OffcutDirection.End, new OffcutLimits(0, 0, 60, 0));
      var without = OffcutGeometry.CutPositionsCore(30, H, W, H, 0, OffcutDirection.End);

      withMax.CutX.Should().BeNull("a 90 wide leftover is past the 60 maximum");
      without.CutX.Should().Be(30);
    }

    /// <summary>The numbers off the screenshot on the sheet they came from: a 30 x 60 remnant sits inside
    /// 20..120 by 20..60, so it is cut.</summary>
    [Fact]
    public void ARemnantInsideTheWindowIsCutOff()
    {
      var (cutX, _) = OffcutGeometry.CutPositionsCore(90, H, W, H, 0, OffcutDirection.End, new OffcutLimits(20, 20, 120, 60));

      cutX.Should().Be(90);
    }

    /// <summary>
    /// The case that decides the whole shape of the code. In Both mode the two cuts are COUPLED: the second
    /// remnant stops at the first. So when the X remnant is rejected, the Y remnant does not merely survive
    /// — it gets WIDER, because nothing is taking a bite out of it any more. Judging the axes one at a time
    /// could never produce this answer.
    /// </summary>
    [Fact]
    public void RejectingOneRemnantReshapesTheOther()
    {
      var limits = new OffcutLimits(40, 0, 0, 0); // the X remnant would only be 30 wide
      var (cutX, cutY) = OffcutGeometry.CutPositionsCore(90, 40, W, H, 0, OffcutDirection.Both, limits);

      cutX.Should().BeNull();
      cutY.Should().Be(40);

      var rects = OffcutGeometry.RemnantRects(cutX, cutY, W, H);
      rects.Should().HaveCount(1);
      rects[0].W.Should().Be(W, "with no X cut the strip runs the full width, not just up to it");
      rects[0].H.Should().Be(20);
    }

    /// <summary>And with room for both, both are still taken — the pair frees the most material, which is
    /// how Auto has always chosen.</summary>
    [Fact]
    public void BothCutsAreStillTakenWhenBothFit()
    {
      var (cutX, cutY) = OffcutGeometry.CutPositionsCore(90, 40, W, H, 0, OffcutDirection.Both);

      cutX.Should().Be(90);
      cutY.Should().Be(40);
    }

    /// <summary>Edge overlap lengthens the cut so the remnant comes away instead of hanging on at the
    /// corners. It must not MOVE the cut: the midpoint is the assertion that separates the two.</summary>
    [Fact]
    public void EdgeOverlapLengthensTheCutWithoutMovingIt()
    {
      var flush = OffcutGeometry.RemnantRects(90, null, W, H)[0].Cut;
      var over = OffcutGeometry.RemnantRects(90, null, W, H, 0.1969)[0].Cut;

      (over.Y2 - over.Y1).Should().BeApproximately((flush.Y2 - flush.Y1) + (2 * 0.1969), 1e-9);
      ((over.Y1 + over.Y2) / 2).Should().BeApproximately((flush.Y1 + flush.Y2) / 2, 1e-9);
      over.X1.Should().Be(90);
      over.X2.Should().Be(90);
    }

    /// <summary>The overlap runs along whichever way the cut goes, so the horizontal one grows in X.</summary>
    [Fact]
    public void TheOverlapFollowsTheCutsOwnDirection()
    {
      var over = OffcutGeometry.RemnantRects(null, 40, W, H, 0.25)[0].Cut;

      over.X1.Should().Be(-0.25);
      over.X2.Should().Be(W + 0.25);
      over.Y1.Should().Be(40);
      over.Y2.Should().Be(40);
    }

    /// <summary>
    /// The overlay draws the cut, so the flip from sheet coordinates (Y-up) to canvas (Y-down) has to keep
    /// the overlap that runs past the sheet. The pair of assertions is what proves it reaches the screen:
    /// the line has to poke out beyond BOTH ends of a 60 tall sheet.
    /// </summary>
    [Fact]
    public void TheCutReachesTheCanvasWithItsOverlapIntact()
    {
      var cut = OffcutGeometry.RemnantRects(90, null, W, H, 0.1969)[0].Cut;

      var (a, b) = DxfViewer.CutToCanvas(cut, H);

      a.X.Should().Be(90, "a vertical cut keeps its X");
      b.X.Should().Be(90);
      a.Y.Should().BeApproximately(H + 0.1969, 1e-9, "it starts past the bottom edge");
      b.Y.Should().BeApproximately(-0.1969, 1e-9, "and ends past the top one");
    }

    [Fact]
    public void AHorizontalCutKeepsItsOwnDirectionOnTheCanvas()
    {
      var cut = OffcutGeometry.RemnantRects(null, 40, W, H, 0.25)[0].Cut;

      var (a, b) = DxfViewer.CutToCanvas(cut, H);

      a.X.Should().Be(-0.25);
      b.X.Should().Be(W + 0.25);
      a.Y.Should().Be(H - 40);
      b.Y.Should().Be(H - 40, "it is level, so the flip must give both ends the same Y");
    }

    /// <summary>The recommended figures, agreed with Manuel: a foot square is worth racking, and 5mm of
    /// overrun is what a shop puts on a cutoff.</summary>
    [Fact]
    public void TheRecommendedFiguresAreTheOnesAgreed()
    {
      var (inches, inchSpacing, inchOverlap) = OffcutOptions.Defaults(unitsMm: false);
      var (mm, mmSpacing, mmOverlap) = OffcutOptions.Defaults(unitsMm: true);

      (inches.MinX, inches.MinY, inches.MaxX, inches.MaxY).Should().Be((12d, 12d, 240d, 240d));
      (inchSpacing, inchOverlap).Should().Be((0.1969, 0.1969));
      (mm.MinX, mm.MinY, mm.MaxX, mm.MaxY).Should().Be((300d, 300d, 6000d, 6000d));
      (mmSpacing, mmOverlap).Should().Be((5d, 5d));
    }

    /// <summary>
    /// The two gaps are the SAME PHYSICAL DISTANCE on purpose. 5mm of clearance is 5mm whichever units the
    /// drawing is in, so the imperial figure is the exact conversion rather than a tidy 0.2 — the rounding I
    /// reached for first, and which Manuel corrected. This is here so nobody tidies it again.
    /// </summary>
    [Fact]
    public void TheTwoGapsAreTheSameDistanceInBothSystems()
    {
      var (_, inchSpacing, inchOverlap) = OffcutOptions.Defaults(unitsMm: false);
      var (_, mmSpacing, mmOverlap) = OffcutOptions.Defaults(unitsMm: true);

      (inchSpacing * 25.4).Should().BeApproximately(mmSpacing, 0.01);
      (inchOverlap * 25.4).Should().BeApproximately(mmOverlap, 0.01);
    }

    /// <summary>
    /// And the sizes are NOT: a foot and 300mm are each the round "this goes on the rack" figure of their own
    /// system. The guard that would have caught the bug this project has already had — a metric default that
    /// is the imperial number reused, i.e. a 12 mm remnant nobody would keep.
    /// </summary>
    [Fact]
    public void TheMetricSizesAreNotTheImperialOnesReused()
    {
      var (inches, _, _) = OffcutOptions.Defaults(unitsMm: false);
      var (mm, _, _) = OffcutOptions.Defaults(unitsMm: true);

      mm.MinX.Should().BeGreaterThan(10 * inches.MinX);
      mm.MaxX.Should().BeGreaterThan(10 * inches.MaxX);
    }

    /// <summary>Out of the box, an ordinary sheet still gets its remnant cut. Defaults that quietly stopped
    /// the feature working would be worse than no defaults at all.</summary>
    [Fact]
    public void TheDefaultsStillCutAnOrdinarySheet()
    {
      var (cutX, _) = OffcutGeometry.CutPositionsCore(90, H, W, H, 0, OffcutDirection.End, OffcutOptions.Defaults(false).Limits);

      cutX.Should().Be(90);
    }

    /// <summary>
    /// And the reason the default maximum is 240 rather than the 120x60 of a standard sheet. The window is
    /// measured against the remnant RECTANGLE, which in End mode spans the sheet's whole height — so a
    /// maximum of 60 would stop cutting on any sheet taller than 60, with nothing on screen to explain it.
    /// This is that sheet, stood on its end.
    /// </summary>
    [Fact]
    public void TheDefaultMaximumDoesNotBlockATallSheet()
    {
      var (_, cutY) = OffcutGeometry.CutPositionsCore(H, 90, H, W, 0, OffcutDirection.End, OffcutOptions.Defaults(false).Limits);

      cutY.Should().Be(90, "a 60 wide by 120 tall sheet is ordinary, and its remnant must still come off");
    }

    /// <summary>The minimum still does its job with the defaults in place: a 10 wide strip is not worth it.</summary>
    [Fact]
    public void TheDefaultMinimumStillRefusesAScrapOfAStrip()
    {
      var (cutX, _) = OffcutGeometry.CutPositionsCore(110, H, W, H, 0, OffcutDirection.End, OffcutOptions.Defaults(false).Limits);

      cutX.Should().BeNull();
    }

    /// <summary>An unset bound constrains nothing, so a window of all zeros leaves the answer alone.</summary>
    [Fact]
    public void AnEmptyWindowChangesNothing()
    {
      var plain = OffcutGeometry.CutPositionsCore(90, 40, W, H, 0, OffcutDirection.Both);
      var zeroed = OffcutGeometry.CutPositionsCore(90, 40, W, H, 0, OffcutDirection.Both, new OffcutLimits(0, 0, 0, 0));

      zeroed.Should().Be(plain);
    }
  }
}
