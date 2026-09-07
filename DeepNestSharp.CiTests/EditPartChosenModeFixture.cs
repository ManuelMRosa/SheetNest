namespace DeepNestSharp.CiTests
{
  using DeepNestLib.NestProject;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The selector is shown only where the output contract can represent one shared cut. Its initial
  /// effective value is not itself a user choice; direct DXFs opt in only when the operator changes it.
  /// </summary>
  public class EditPartChosenModeFixture
  {
    /// <summary>
    /// Invalid/empty selection still preserves the raw value rather than erasing project data.
    /// </summary>
    [Theory]
    [InlineData(CommonCuttingMode.Unrestricted)]
    [InlineData(CommonCuttingMode.SamePart)]
    [InlineData(CommonCuttingMode.None)]
    public void AnInvalidOrEmptySelectionChangesNothing(CommonCuttingMode current)
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

    [Theory]
    [InlineData(CommonCuttingMode.Unrestricted)]
    [InlineData(CommonCuttingMode.SamePart)]
    public void OkWithoutChangingAnUnconfirmedLegacySelectorPreservesItsRawMode(CommonCuttingMode raw)
    {
      EditPartWindow.ChosenCommonCutting(0, false, raw, false)
        .Should().Be((raw, false));
    }

    [Theory]
    [InlineData(1, CommonCuttingMode.Unrestricted)]
    [InlineData(2, CommonCuttingMode.SamePart)]
    public void ChoosingANonNoneModeConfirmsADirectPart(int selectedIndex, CommonCuttingMode expected)
    {
      EditPartWindow.ChosenCommonCutting(selectedIndex, true, CommonCuttingMode.None, false)
        .Should().Be((expected, true));
    }

    [Fact]
    public void ChoosingNoneDisablesADirectPart()
    {
      EditPartWindow.ChosenCommonCutting(0, true, CommonCuttingMode.SamePart, true)
        .Should().Be((CommonCuttingMode.None, false));
    }

    [Fact]
    public void DirectSvgDoesNotShowTheSelectorOrAcceptAForcedChoice()
    {
      var svg = new DetailLoadInfo
      {
        Path = "part.svg",
        CommonCutting = CommonCuttingMode.None,
      };

      EditPartWindow.ShowsCommonCuttingSelector(svg).Should().BeFalse();
      EditPartWindow.ChosenCommonCutting(
          1,
          selectionChanged: true,
          CommonCuttingMode.None,
          directEnabled: false,
          supportsCommonCutting: svg.SupportsCommonCutting)
        .Should().Be((CommonCuttingMode.None, false));

      EditPartWindow.ChosenCommonCutting(
          -1,
          selectionChanged: false,
          CommonCuttingMode.Unrestricted,
          directEnabled: true,
          supportsCommonCutting: svg.SupportsCommonCutting)
        .Should().Be((CommonCuttingMode.Unrestricted, true),
          "an invisible control must not rewrite raw project data; the model still keeps it ineffective");
    }

  }
}
