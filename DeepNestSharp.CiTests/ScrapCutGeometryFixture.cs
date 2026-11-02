namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Where the scrap cuts go: straight lines across the skeleton at an even split near the ideal spacing,
  /// broken wherever they would come closer than the part gap to a part, kept out of the offcut, and run past
  /// the sheet edge only where the end really is the sheet's edge. Every part here is invented.
  /// </summary>
  public class ScrapCutGeometryFixture
  {
    private const double W = 120;
    private const double H = 60;
    private const double Gap = 0.02;
    private const double Overlap = 0.1969;

    /// <summary>How far a cut really stops from the nested outline: the gap plus the most an arc's chords sit
    /// inside the drawn arc.</summary>
    private const double Keep = Gap + DxfParser.CurveChordTolerance;

    [Fact]
    public void WithNothingInTheWayTheLinesSplitTheSheetEvenlyAndRunPastItsEdges()
    {
      var lines = ScrapCutGeometry.BuildLinesCore(new[] { Box(0, 0, 5, 5) }, W, H, null, null, Options(ScrapCutType.Both, 40, 30));

      lines.Should().BeEquivalentTo(new[]
      {
        new OffcutLine { X1 = 40, Y1 = -Overlap, X2 = 40, Y2 = H + Overlap },
        new OffcutLine { X1 = 80, Y1 = -Overlap, X2 = 80, Y2 = H + Overlap },
        new OffcutLine { X1 = -Overlap, Y1 = 30, X2 = W + Overlap, Y2 = 30 },
      });
    }

    [Fact]
    public void APartAcrossALineBreaksItAtThePartGap()
    {
      var lines = ScrapCutGeometry.BuildLinesCore(new[] { Box(55, 20, 65, 30) }, W, H, null, null, Options(ScrapCutType.Vertical, 60, 0));

      lines.Should().HaveCount(2);
      lines[0].Y1.Should().Be(-Overlap);
      lines[0].Y2.Should().BeInRange(20 - Keep - 1e-4, 20 - Keep);
      lines[1].Y1.Should().BeInRange(30 + Keep, 30 + Keep + 1e-4);
      lines[1].Y2.Should().Be(H + Overlap);
      lines.Should().OnlyContain(l => l.X1 == 60 && l.X2 == 60);
    }

    /// <summary>A packed sheet of tilted parts, a round one and a frame with a part in its window: no piece of
    /// any cut comes closer than the gap to any outline, and none runs inside one.</summary>
    [Fact]
    public void NoCutComesCloserThanThePartGapToAnyPart()
    {
      var frameOuter = Box(50, 10, 90, 50);
      var outlines = new List<IReadOnlyList<(double X, double Y)>>
      {
        Rotated(Box(5, 5, 35, 15), 30),
        Rotated(Box(10, 30, 30, 55), -20).Reverse().ToArray(), // drawn clockwise
        Circle(105, 20, 12),
        Triangle(95, 40, 118, 58),
        frameOuter,
        Box(60, 20, 80, 40), // nested in the frame's window
      };

      var lines = ScrapCutGeometry.BuildLinesCore(outlines, W, H, null, null, Options(ScrapCutType.Both, 7, 6));

      lines.Should().NotBeEmpty();
      foreach (var line in lines)
      {
        foreach (var outline in outlines)
        {
          Distance(line, outline).Should().BeGreaterThanOrEqualTo(Keep - 1e-6, "a cut keeps the gap from {0}", Describe(line));
          Inside(((line.X1 + line.X2) / 2, (line.Y1 + line.Y2) / 2), outline).Should().BeFalse("no cut runs inside a part: {0}", Describe(line));
        }
      }
    }

    [Fact]
    public void OnTheOffcutSheetTheLinesStayInTheSkeletonAndStopOnTheOffcutCut()
    {
      var sheet = Sheet(Box(0, 0, 20, 10), Box(60, 30, 80, 40));
      var offcut = new OffcutOptions { Direction = OffcutDirection.End, Spacing = 0 };

      var lines = ScrapCutGeometry.BuildLines(sheet, Options(ScrapCutType.Both, 40, 20), offcut);

      // End: the remnant is everything right of x = 80, so the skeleton is 80 x 60.
      lines.Should().BeEquivalentTo(new[]
      {
        new OffcutLine { X1 = 40, Y1 = -Overlap, X2 = 40, Y2 = H + Overlap },
        new OffcutLine { X1 = -Overlap, Y1 = 20, X2 = 80, Y2 = 20 },
        new OffcutLine { X1 = -Overlap, Y1 = 40, X2 = 60 - Keep, Y2 = 40 },
      }, options => options.Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, 1e-4)).WhenTypeIs<double>());
    }

    [Fact]
    public void WithAnOffcutOnBothAxesNoCutEntersEitherRemnant()
    {
      var sheet = Sheet(Box(0, 0, 20, 10), Box(60, 30, 80, 40));
      var offcut = new OffcutOptions { Direction = OffcutDirection.Both, Spacing = 0 };

      var lines = ScrapCutGeometry.BuildLines(sheet, Options(ScrapCutType.Both, 40, 20), offcut);

      // Both: x = 80 and y = 40 are cut, so the skeleton is 80 x 40.
      lines.Should().BeEquivalentTo(new[]
      {
        new OffcutLine { X1 = 40, Y1 = -Overlap, X2 = 40, Y2 = 40 },
        new OffcutLine { X1 = -Overlap, Y1 = 20, X2 = 80, Y2 = 20 },
      });
    }

    [Fact]
    public void WithASideOffcutTheLinesStopUnderIt()
    {
      var sheet = Sheet(Box(0, 0, 20, 10), Box(50, 30, 70, 40));
      var offcut = new OffcutOptions { Direction = OffcutDirection.Side, Spacing = 0 };

      var lines = ScrapCutGeometry.BuildLines(sheet, Options(ScrapCutType.Both, 40, 20), offcut);

      // Side: y = 40 is cut and the skeleton is the whole width, 40 high.
      lines.Should().BeEquivalentTo(new[]
      {
        new OffcutLine { X1 = 40, Y1 = -Overlap, X2 = 40, Y2 = 40 },
        new OffcutLine { X1 = 80, Y1 = -Overlap, X2 = 80, Y2 = 40 },
        new OffcutLine { X1 = -Overlap, Y1 = 20, X2 = W + Overlap, Y2 = 20 },
      });
    }

    [Theory]
    [InlineData((int)ScrapCutType.Vertical, 2, 0)]
    [InlineData((int)ScrapCutType.Horizontal, 0, 1)]
    [InlineData((int)ScrapCutType.Both, 2, 1)]
    public void TheCutTypePicksWhichLinesAreCut(int type, int vertical, int horizontal)
    {
      var lines = ScrapCutGeometry.BuildLinesCore(new[] { Box(0, 0, 5, 5) }, W, H, null, null, Options((ScrapCutType)type, 40, 30));

      lines.Count(l => l.X1 == l.X2).Should().Be(vertical);
      lines.Count(l => l.Y1 == l.Y2).Should().Be(horizontal);
    }

    [Theory]
    [InlineData(120, 100, new double[0])]
    [InlineData(120, 80, new double[] { 60 })]
    [InlineData(120, 45, new double[] { 40, 80 })]
    [InlineData(120, 0, new double[0])]
    public void ASideIsSplitIntoTheNumberOfPiecesNearestTheSpacing(double length, double spacing, double[] expected)
    {
      ScrapCutGeometry.Positions(length, spacing).Should().Equal(expected);
    }

    [Fact]
    public void WithTheFeatureOffThereAreNoScrapCuts()
    {
      var sheet = Sheet(Box(0, 0, 5, 5));

      ScrapCutGeometry.BuildLines(sheet, null).Should().BeNull();
      ScrapCutGeometry.BuildLines(sheet, Options(ScrapCutType.Both, 40, 30)).Should().NotBeNull("the same sheet has cuts when the feature is on");
    }

    private static ScrapCutOptions Options(ScrapCutType type, double spacingX, double spacingY)
      => new ScrapCutOptions { Type = type, SpacingX = spacingX, SpacingY = spacingY, PartGap = Gap, EdgeOverlap = Overlap };

    private static ISheetPlacement Sheet(params IReadOnlyList<(double X, double Y)>[] outlines)
    {
      var placements = new List<IPartPlacement>();
      foreach (var outline in outlines)
      {
        var part = new NoFitPolygon(outline.Select(p => new SvgPoint(p.X, p.Y)).ToArray());
        placements.Add(new PartPlacement(part) { X = 0, Y = 0 });
      }

      return new SheetPlacement(PlacementTypeEnum.Gravity, DeepNestLib.Sheet.NewSheet(1, (int)W, (int)H), placements, 0);
    }

    private static IReadOnlyList<(double X, double Y)> Box(double x1, double y1, double x2, double y2)
      => new[] { (x1, y1), (x2, y1), (x2, y2), (x1, y2) };

    private static IReadOnlyList<(double X, double Y)> Triangle(double x1, double y1, double x2, double y2)
      => new[] { (x1, y1), (x2, y1), (x2, y2) };

    private static IReadOnlyList<(double X, double Y)> Circle(double cx, double cy, double r)
      => Enumerable.Range(0, 48).Select(i => (cx + (r * Math.Cos(i * Math.PI / 24)), cy + (r * Math.Sin(i * Math.PI / 24)))).ToArray();

    private static IReadOnlyList<(double X, double Y)> Rotated(IReadOnlyList<(double X, double Y)> outline, double degrees)
    {
      double cx = outline.Average(p => p.X);
      double cy = outline.Average(p => p.Y);
      double a = degrees * Math.PI / 180;
      return outline.Select(p => (
        cx + ((p.X - cx) * Math.Cos(a)) - ((p.Y - cy) * Math.Sin(a)),
        cy + ((p.X - cx) * Math.Sin(a)) + ((p.Y - cy) * Math.Cos(a)))).ToArray();
    }

    private static double Distance(OffcutLine line, IReadOnlyList<(double X, double Y)> outline)
    {
      double best = double.MaxValue;
      for (int i = 0; i < outline.Count; i++)
      {
        var a = outline[i];
        var b = outline[(i + 1) % outline.Count];
        best = Math.Min(best, SegmentDistance((line.X1, line.Y1), (line.X2, line.Y2), a, b));
      }

      return best;
    }

    private static double SegmentDistance((double X, double Y) p1, (double X, double Y) p2, (double X, double Y) q1, (double X, double Y) q2)
    {
      if (Cross(p1, p2, q1) * Cross(p1, p2, q2) < 0 && Cross(q1, q2, p1) * Cross(q1, q2, p2) < 0)
      {
        return 0;
      }

      return new[] { PointDistance(p1, q1, q2), PointDistance(p2, q1, q2), PointDistance(q1, p1, p2), PointDistance(q2, p1, p2) }.Min();
    }

    private static double Cross((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
      => ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));

    private static double PointDistance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
      double dx = b.X - a.X;
      double dy = b.Y - a.Y;
      double t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
      double ex = a.X + (t * dx) - p.X;
      double ey = a.Y + (t * dy) - p.Y;
      return Math.Sqrt((ex * ex) + (ey * ey));
    }

    private static bool Inside((double X, double Y) p, IReadOnlyList<(double X, double Y)> outline)
    {
      bool inside = false;
      for (int i = 0, j = outline.Count - 1; i < outline.Count; j = i++)
      {
        if ((outline[i].Y > p.Y) != (outline[j].Y > p.Y)
          && p.X < ((outline[j].X - outline[i].X) * (p.Y - outline[i].Y) / (outline[j].Y - outline[i].Y)) + outline[i].X)
        {
          inside = !inside;
        }
      }

      return inside;
    }

    private static string Describe(OffcutLine line) => $"({line.X1:0.###}, {line.Y1:0.###}) to ({line.X2:0.###}, {line.Y2:0.###})";
  }
}
