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
    public void APairThatNeverAskedForMergedOutputRaisesNoFallbackNotice()
    {
      INfp outline = ArcTopped("legacy-outline.dxf");
      var all = new List<IPartPlacement>
      {
        new PartPlacement(outline) { Source = 0, X = 12, Y = 2 },
        new PartPlacement(outline) { Source = 1, X = 12 + outline.WidthCalculated + 0.30, Y = 2 },
      };
      var rules = DirectRules(0.30);
      rules[0] = (CommonCuttingMode.Unrestricted, 0, "other", false, 0.30);
      rules[1] = (CommonCuttingMode.Unrestricted, 1, "other", false, 0.30);

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        rules,
        60,
        30,
        0,
        0.01,
        0.0001);

      result.Fallbacks.Should().BeEmpty(
        "a notice explains why a pair that wanted merged output did not get it, and this pair never wanted it");
    }

    [Fact]
    public void InvalidLongClusterDoesNotHideALaterValidRelationship()
    {
      const double normalGap = 0.30;
      INfp curved = ArcTopped("curved-outline.dxf");
      INfp rectangle = Rectangle(5, 4);
      var curvedA = new PartPlacement(curved) { Source = 0, X = 12, Y = 2 };
      var curvedB = new PartPlacement(curved)
      {
        Source = 1,
        X = curvedA.MaxX + normalGap - curved.MinX,
        Y = 2,
      };
      var straightA = new PartPlacement(rectangle) { Source = 2, X = 60, Y = 2 };
      var straightB = new PartPlacement(rectangle) { Source = 3, X = 65 + normalGap, Y = 2 };
      var all = new List<IPartPlacement> { curvedA, curvedB, straightA, straightB };
      Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> rules =
        DirectRules(normalGap);
      rules[2] = (CommonCuttingMode.Unrestricted, 2, "direct", true, normalGap);
      rules[3] = (CommonCuttingMode.Unrestricted, 3, "direct", true, normalGap);

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        all,
        rules,
        100,
        40,
        0,
        0.01,
        0.0001);

      result.Edges.Should().ContainSingle(edge =>
        (ReferenceEquals(edge.PartA, straightA) && ReferenceEquals(edge.PartB, straightB))
        || (ReferenceEquals(edge.PartA, straightB) && ReferenceEquals(edge.PartB, straightA)));
      result.Fallbacks.Should().ContainSingle(notice =>
        notice.Drawing == "curved-outline"
        && notice.Reason == CommonLineFallbackNotice.ContourFormatReason);
      straightB.MinX.Should().BeApproximately(straightA.MaxX + 0.01, 1e-8);
      curvedB.MinX.Should().BeApproximately(curvedA.MaxX + normalGap, 1e-8,
        "the rejected curved pair must return to its ordinary clearance");
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

    private static INfp Rectangle(double width, double height)
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(width, 0),
        new SvgPoint(width, height),
        new SvgPoint(0, height),
      });
  }
}
