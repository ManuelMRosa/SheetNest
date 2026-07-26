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
  }
}
