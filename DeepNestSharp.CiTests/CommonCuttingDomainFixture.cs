namespace DeepNestSharp.CiTests
{
  using System.Collections.Generic;
  using System.IO;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;

  public class CommonCuttingDomainFixture
  {
    [Fact]
    public void ConfirmedDirectDxfAndUnfoldedPartShareTheDirectContract()
    {
      var dxf = new DetailLoadInfo
      {
        Path = "part.dxf",
        CommonCutting = CommonCuttingMode.Unrestricted,
        DirectCommonCuttingEnabled = true,
      };
      var unfolded = new DetailLoadInfo
      {
        Path = "flat.dxf",
        SourceStepPath = "part.step",
        CommonCutting = CommonCuttingMode.SamePart,
        DirectCommonCuttingEnabled = true,
      };

      MainWindow.CommonCuttingDomainOf(dxf).Should().Be("direct");
      MainWindow.CommonCuttingDomainOf(unfolded).Should().Be("direct");
    }

    [Fact]
    public void InactiveOrUnconfirmedModeHasNoContract()
    {
      var off = new DetailLoadInfo { Path = "off.dxf", CommonCutting = CommonCuttingMode.None };
      var legacy = new DetailLoadInfo { Path = "legacy.dxf", CommonCutting = CommonCuttingMode.Unrestricted };

      MainWindow.CommonCuttingDomainOf(off).Should().BeEmpty();
      MainWindow.CommonCuttingDomainOf(legacy).Should().BeEmpty();
    }

    [Fact]
    public void ConfirmedDirectSvgHasNeitherADomainNorAMergeRequirement()
    {
      var svg = new DetailLoadInfo
      {
        Path = "part.svg",
        CommonCutting = CommonCuttingMode.Unrestricted,
        DirectCommonCuttingEnabled = true,
      };

      svg.EffectiveCommonCutting.Should().Be(CommonCuttingMode.None);
      MainWindow.CommonCuttingDomainOf(svg).Should().BeEmpty();
      MainWindow.RequiresMergedLinesForDirectCommonCutting(svg).Should().BeFalse();
    }

    [Fact]
    public void ConfirmedDirectDxfRequiresMergedOutput()
    {
      var dxf = new DetailLoadInfo
      {
        Path = "part.DXF",
        CommonCutting = CommonCuttingMode.Unrestricted,
        DirectCommonCuttingEnabled = true,
      };

      MainWindow.RequiresMergedLinesForDirectCommonCutting(dxf).Should().BeTrue();
    }

  }
}
