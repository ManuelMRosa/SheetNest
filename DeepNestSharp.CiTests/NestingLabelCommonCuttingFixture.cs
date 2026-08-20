namespace DeepNestSharp.CiTests
{
  using DeepNestLib.NestProject;
  using DeepNestSharp.Domain.Models;
  using DeepNestSharp.Domain.ViewModels;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The part card is where the operator reads back what a part is set to. It has to describe what will
  /// actually happen, not what the file happens to say.
  /// </summary>
  public class NestingLabelCommonCuttingFixture
  {
    [Fact]
    public void APlainDxfDoesNotClaimAnyCommonLine()
    {
      var sut = new ObservableDetailLoadInfo(new DetailLoadInfo
      {
        Path = "a.dxf",
        CommonCutting = CommonCuttingMode.Unrestricted, // as a 1.1.7 project would bring it back
      });

      sut.NestingLabel.Should().NotContain("common line", "nothing is going to share a cut on a part with no kerf");
    }

    [Theory]
    [InlineData(CommonCuttingMode.Unrestricted, "common line · ")]
    [InlineData(CommonCuttingMode.SamePart, "common line (same part) · ")]
    public void APartFromANestStillSaysWhatItShares(CommonCuttingMode stored, string expected)
    {
      var sut = new ObservableDetailLoadInfo(new DetailLoadInfo
      {
        Path = "a.dxf",
        CommonCutting = stored,
        NestSourcePath = @"C:\Jobs\job.nest",
        NestPartName = "Bracket",
      });

      sut.NestingLabel.Should().StartWith(expected);
    }

    /// <summary>
    /// The whole feature now hangs off this one field, and this is the only place that ever fills it in.
    /// Drop it and every shared cut in a SheetCam job quietly stops happening, with nothing on screen to
    /// say why — so say out loud that the import still marks its parts.
    /// </summary>
    [Fact]
    public void ImportingANestMarksItsPartsAsComingFromOne()
    {
      var imported = NestProjectViewModel.PartFrom(new NestProjectViewModel.NestPartInfo(
        @"C:\Temp\part.dxf", @"C:\Jobs\job.nest", "Bracket", true, 4, CommonCuttingMode.Unrestricted));

      imported.NestSourcePath.Should().Be(@"C:\Jobs\job.nest");
      imported.EffectiveCommonCutting.Should().Be(CommonCuttingMode.Unrestricted, "this is what makes a SheetCam job able to share cuts at all");
    }
  }
}
