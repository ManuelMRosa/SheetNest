namespace DeepNestSharp.CiTests
{
  using DeepNestLib.NestProject;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Common cutting is only offered for parts that came from a SheetCam nest file, so on everything else
  /// the combo is never filled in. What OK then writes back has to be nothing at all.
  /// </summary>
  public class EditPartChosenModeFixture
  {
    /// <summary>
    /// The one that matters. An empty ComboBox reads SelectedIndex -1; clamping that into range lands on
    /// None, so a dialog that never offered the setting would erase it on the way out, on exactly the
    /// parts whose owner has no control left to put it back with.
    /// </summary>
    [Theory]
    [InlineData(CommonCuttingMode.Unrestricted)]
    [InlineData(CommonCuttingMode.SamePart)]
    [InlineData(CommonCuttingMode.None)]
    public void AComboThatWasNeverShownChangesNothing(CommonCuttingMode current)
    {
      EditPartWindow.ChosenMode(-1, current).Should().Be(current);
    }

    [Theory]
    [InlineData(0, CommonCuttingMode.None)]
    [InlineData(1, CommonCuttingMode.Unrestricted)]
    [InlineData(2, CommonCuttingMode.SamePart)]
    public void AShownComboSaysWhatWasPicked(int selectedIndex, CommonCuttingMode expected)
    {
      EditPartWindow.ChosenMode(selectedIndex, CommonCuttingMode.SamePart).Should().Be(expected);
    }
  }
}
