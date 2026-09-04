namespace DeepNestSharp.CiTests
{
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.Domain.Models;
  using DeepNestSharp.Domain.ViewModels;
  using FluentAssertions;
  using Xunit;

  public class SheetPlacementExportMetadataFixture
  {
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ExportMergesWhenEitherThePreferenceOrLayoutRequiresIt(
      bool configured,
      bool requiredByLayout,
      bool expected)
    {
      var placement = Placement(requiredByLayout);

      MainViewModel.MergeLinesForExport(configured, placement).Should().Be(expected);
    }

    [Fact]
    public void ObservablePlacementExposesTheWrappedLayoutsRequirement()
    {
      var placement = Placement(requiresMergedLines: true);

      new ObservableSheetPlacement(placement).RequiresMergedLines.Should().BeTrue();
    }

    private static SheetPlacement Placement(bool requiresMergedLines)
      => new SheetPlacement(
        PlacementTypeEnum.Gravity,
        Sheet.NewSheet(1, 100, 50),
        new List<IPartPlacement>(),
        0,
        requiresMergedLines);
  }
}
