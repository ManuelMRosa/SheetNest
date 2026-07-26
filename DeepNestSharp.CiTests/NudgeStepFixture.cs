namespace DeepNestSharp.CiTests
{
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// How far an arrow key moves a part. Reported in the offcut thread: "the movement increments are still
  /// very small, even when holding Shift" — the step was a single pair of numbers in DRAWING UNITS, fine as
  /// inches and a quarter of a millimetre in a metric job.
  /// </summary>
  public class NudgeStepFixture
  {
    /// <summary>An arrow step of 0.05 in with a 0.5 in magnet — the everyday case, and what tMax means.</summary>
    private const double TMax = 1 + (0.5 / 0.05);

    private const double Precision = 1e-4;

    [Fact]
    public void InchesKeepTheStepsThatAlreadyWorked()
    {
      DxfViewer.NudgeStep(unitsMm: false, shift: false).Should().Be(0.05);
      DxfViewer.NudgeStep(unitsMm: false, shift: true).Should().Be(0.25);
    }

    /// <summary>
    /// The guard that would have caught the bug: a metric step must be a metric NUMBER, not the imperial
    /// constant reused. 0.05 mm is 25x smaller than the 0.05 in it was copied from.
    /// </summary>
    [Fact]
    public void AMetricStepIsNotTheImperialNumberReused()
    {
      DxfViewer.NudgeStep(unitsMm: true, shift: false)
        .Should().BeGreaterThan(10 * DxfViewer.NudgeStep(unitsMm: false, shift: false));
      DxfViewer.NudgeStep(unitsMm: true, shift: true)
        .Should().BeGreaterThan(10 * DxfViewer.NudgeStep(unitsMm: false, shift: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShiftIsTheCoarserNudgeAndNeitherIsZero(bool unitsMm)
    {
      double fine = DxfViewer.NudgeStep(unitsMm, shift: false);
      double coarse = DxfViewer.NudgeStep(unitsMm, shift: true);

      fine.Should().BeGreaterThan(0, "an arrow press that moves nothing is a broken key");
      coarse.Should().BeGreaterThan(fine, "Shift is what the operator reaches for to move further");
    }

    /// <summary>The magnet is the same physical distance in either unit system, so a part settles the same
    /// way whichever units the job happens to be drawn in.</summary>
    [Fact]
    public void TheMagnetIsHalfAnInchInBothUnitSystems()
    {
      DxfViewer.SnapZone(unitsMm: false).Should().Be(0.5);
      DxfViewer.SnapZone(unitsMm: true).Should().BeApproximately(0.5 * 25.4, 1e-9);
    }

    /// <summary>Open space: the magnet must not touch a step that has nothing to settle against, or fine
    /// positioning would jump around for no reason.</summary>
    [Fact]
    public void AStepWithNothingWithinReachIsTheStepItself()
    {
      DxfViewer.TrySnap(_ => true, TMax, out double t).Should().BeTrue();

      t.Should().Be(1);
    }

    /// <summary>"cuando sea menos que se vaya automáticamente al part spacing": the step lands legally but
    /// short of the neighbour, so the part is drawn on until it rests exactly at the clearance.</summary>
    [Fact]
    public void AWallWithinReachAheadPullsThePartOnToIt()
    {
      DxfViewer.TrySnap(t => t < 1.3, TMax, out double landed).Should().BeTrue();

      landed.Should().BeGreaterThan(1, "the part moved further than the plain step to reach the wall");
      landed.Should().BeApproximately(1.3, Precision);
    }

    /// <summary>The other half of the same rule: the step would have gone through the neighbour, so the part
    /// settles just short instead. This is the case that used to be refused outright.</summary>
    [Fact]
    public void AStepThatWouldOvershootSettlesShortOfTheLine()
    {
      DxfViewer.TrySnap(t => t < 0.42, TMax, out double landed).Should().BeTrue();

      landed.Should().BeLessThan(1);
      landed.Should().BeApproximately(0.42, Precision);
    }

    /// <summary>
    /// A part that is ALREADY overlapping — the one Manuel reported as "se está quedando overlaping" — is
    /// carried out to the first legal position. It lands AT the clearance, not at the full step: the plain
    /// step (t=1) is legal here too, and settling for it would leave a wider gap than the job asked for.
    /// </summary>
    [Fact]
    public void APartThatStartedOverlappingIsCarriedOutToTheClearance()
    {
      DxfViewer.TrySnap(t => t >= 0.7, TMax, out double landed).Should().BeTrue();

      landed.Should().BeApproximately(0.7, Precision);
    }

    /// <summary>The one case an arrow may do nothing: buried deep enough that no legal spot lies within the
    /// magnet's reach. The caller reverts and flashes, and the part is freed with the mouse instead.</summary>
    [Fact]
    public void NowhereLegalOnTheLineIsARefusal()
    {
      DxfViewer.TrySnap(_ => false, TMax, out double landed).Should().BeFalse();

      landed.Should().Be(0);
    }

    /// <summary>The word "exacto" is the whole request, so it gets its own assertion: settling lands on the
    /// clearance to a ten-thousandth of a step, not merely on the nearest coarse sample (~1/48 of one).</summary>
    [Theory]
    [InlineData(0.13)]
    [InlineData(0.5)]
    [InlineData(0.999)]
    [InlineData(2.75)]
    public void SettlingLandsOnTheClearanceNotOnTheNearestSample(double boundary)
    {
      DxfViewer.TrySnap(t => t < boundary, TMax, out double landed).Should().BeTrue();

      landed.Should().BeLessThan(boundary, "the part rests on the legal side of the line, never across it");
      landed.Should().BeApproximately(boundary, 1e-6);
    }

    /// <summary>Open sheet: nothing to settle against, so the press is the plain step.</summary>
    [Fact]
    public void AWellPlacedPartInTheOpenTakesThePlainStep()
    {
      DxfViewer.TryNudgeLanding(_ => true, TMax, out double t).Should().BeTrue();

      t.Should().Be(1);
    }

    /// <summary>Something within reach: the press settles it there instead, short of the full step.</summary>
    [Fact]
    public void AWellPlacedPartSettlesAgainstWhatIsWithinReach()
    {
      DxfViewer.TryNudgeLanding(t => t < 0.42, TMax, out double landed).Should().BeTrue();

      landed.Should().BeApproximately(0.42, Precision);
    }

    /// <summary>
    /// "cuando lo muevo con los arrow keys, no estando en rojo, que no haga overlapping" — a properly placed
    /// part with its neighbour right there does not move at all. This is the half of the rule that must
    /// survive making the other half work.
    /// </summary>
    [Fact]
    public void AWellPlacedPartIsRefusedRatherThanPushedIntoANeighbour()
    {
      DxfViewer.TryNudgeLanding(t => t <= 0, TMax, out double _).Should().BeFalse();
    }

    /// <summary>
    /// "los arrow keys no están funcionando cuando está en rojo" — a part buried deeper than the magnet
    /// reaches had all four arrows dead, because every landing within half an inch was still illegal. It
    /// moves by the plain step instead and stays red, which is the only way the keyboard can walk it out.
    /// </summary>
    [Fact]
    public void AnOverlappingPartMovesEvenWhenNoLandingIsLegal()
    {
      DxfViewer.TryNudgeLanding(_ => false, TMax, out double t).Should().BeTrue();

      t.Should().Be(1);
    }

    /// <summary>And the moment a legal spot IS within reach it takes that instead — at the clearance exactly,
    /// not the full step, which would leave a wider gap than the job asked for.</summary>
    [Fact]
    public void AnOverlappingPartWithAWayOutLandsOnTheClearance()
    {
      DxfViewer.TryNudgeLanding(t => t >= 0.7, TMax, out double landed).Should().BeTrue();

      landed.Should().BeApproximately(0.7, Precision);
    }
  }
}
