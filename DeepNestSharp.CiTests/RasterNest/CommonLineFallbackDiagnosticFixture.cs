namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  public class CommonLineFallbackDiagnosticFixture
  {
    [Fact]
    public void CurvedOutsideContourKeepsNormalGapAndNamesTheDrawing()
    {
      const double normalGap = 0.30;
      INfp outline = ArcTopped("curved-outline.dxf");
      var a = new PartPlacement(outline) { Source = 0, X = 12, Y = 2 };
      var b = new PartPlacement(outline) { Source = 1, X = 12 + outline.WidthCalculated + normalGap, Y = 2 };
      var all = new List<IPartPlacement> { a, b };
      double originalX = b.X;

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        DirectRules(normalGap),
        60,
        30,
        0,
        0.01,
        0.0001);

      result.Edges.Should().BeEmpty();
      result.GivenUp.Should().Be(1);
      b.X.Should().Be(originalX, "an unsupported cluster must retain the normal-spacing placement");
      result.Fallbacks.Should().ContainSingle(notice =>
        notice.Drawing == "curved-outline"
        && notice.Reason == CommonLineFallbackNotice.ContourFormatReason);
    }

    [Fact]
    public void SheetCamLegacyEligibilityDoesNotCreateDirectFallbackNotices()
    {
      INfp outline = ArcTopped("legacy-outline.dxf");
      var all = new List<IPartPlacement>
      {
        new PartPlacement(outline) { Source = 0, X = 12, Y = 2 },
        new PartPlacement(outline) { Source = 1, X = 12 + outline.WidthCalculated + 0.30, Y = 2 },
      };
      var rules = DirectRules(0.30);
      rules[0] = (CommonCuttingMode.Unrestricted, 0, "sheetcam:job.nest", false, 0.30);
      rules[1] = (CommonCuttingMode.Unrestricted, 1, "sheetcam:job.nest", false, 0.30);

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        rules,
        60,
        30,
        0,
        0.01,
        0.0001);

      result.Fallbacks.Should().BeEmpty("legacy SheetCam common cutting does not use the direct cluster contract");
    }

    private static Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> DirectRules(double spacing)
      => new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>
      {
        [0] = (CommonCuttingMode.Unrestricted, 0, "direct", true, spacing),
        [1] = (CommonCuttingMode.Unrestricted, 1, "direct", true, spacing),
      };

    private static INfp ArcTopped(string name)
    {
      const double radius = 13.139;
      double x = radius * Math.Cos(39 * Math.PI / 180.0);
      double y = radius * Math.Sin(39 * Math.PI / 180.0);
      var entities = new DxfEntity[]
      {
        new DxfArc(new DxfPoint(0, 0, 0), radius, 39, 141),
        new DxfLine(new DxfPoint(-x, y, 0), new DxfPoint(-x, 0, 0)),
        new DxfLine(new DxfPoint(-x, 0, 0), new DxfPoint(x, 0, 0)),
        new DxfLine(new DxfPoint(x, 0, 0), new DxfPoint(x, y, 0)),
      };
      INfp result = DxfParser.ConvertDxfToRawDetail(name, entities).ToNfp();
      result.Name = name;
      return result;
    }
  }
}
