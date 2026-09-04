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
    public void SeparateSheetCamJobsNeverShareAContract()
    {
      var first = SheetCamPart(Path.Combine("jobs", "first.nest"));
      var second = SheetCamPart(Path.Combine("jobs", "second.nest"));

      string firstDomain = MainWindow.CommonCuttingDomainOf(first);
      string secondDomain = MainWindow.CommonCuttingDomainOf(second);

      firstDomain.Should().StartWith("sheetcam:");
      secondDomain.Should().StartWith("sheetcam:");
      firstDomain.Should().NotBe(secondDomain);
      firstDomain.Should().NotBe("direct");
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

    [Fact]
    public void PlacementOnlySheetCamReturnRejectsResolvedCommonLineGeometry()
    {
      const double Gap = 0.01;
      const double Tolerance = 0.0001;
      var outline = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(0, 10),
      });
      var a = new PartPlacement(outline) { Id = 1, X = 1, Y = 1 };
      var b = new PartPlacement(outline) { Id = 2, X = 11 + Gap, Y = 1 };
      CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(a, 1, b, 3, Gap, Tolerance);
      var clustered = new SheetPlacement(
        PlacementTypeEnum.Gravity,
        Sheet.NewSheet(1, 40, 20),
        new IPartPlacement[] { a, b },
        0,
        commonEdges: new[] { edge },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);
      var normal = new SheetPlacement(
        PlacementTypeEnum.Gravity,
        Sheet.NewSheet(2, 40, 20),
        new IPartPlacement[] { a },
        0);

      MainWindow.HasResolvedCommonLineGeometry(new[] { normal }).Should().BeFalse();
      MainWindow.HasResolvedCommonLineGeometry(new[] { normal, clustered }).Should().BeTrue(
        "a .nest return cannot encode the generated closed cluster boundary and single seam");
    }

    private static DetailLoadInfo SheetCamPart(string source)
      => new DetailLoadInfo
      {
        Path = "temp.dxf",
        NestSourcePath = source,
        CommonCutting = CommonCuttingMode.Unrestricted,
      };
  }
}
