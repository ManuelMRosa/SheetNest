namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Text;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.Reports;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The report numbers every distinct part from 1 and prints that number on each of its copies in the
  /// sheet drawing, so an operator can read a number off a cut piece and find its row. The number is
  /// therefore only worth anything if it means the SAME part everywhere it appears.
  /// </summary>
  public class NestReportPartNumbersFixture
  {
    [Fact]
    public void NumbersRunFromOneAndFollowTheOrderTheTableIsPrintedIn()
    {
      var layouts = new List<(ISheetPlacement Sheet, int Count, string Name)>
      {
        (SheetOf(("few.dxf", 1), ("many.dxf", 3), ("some.dxf", 2)), 1, "A"),
      };

      var totals = NestReportPdf.NumberedPartTotals(layouts);

      totals.Select(t => t.Number).Should().Equal(new[] { 1, 2, 3 }, "the table reads 1, 2, 3 down the page");
      totals.Select(t => t.File).Should().Equal(
        new[] { "many.dxf", "some.dxf", "few.dxf" },
        "the rows stay ordered by quantity, so the numbers follow that order");
      totals.Select(t => t.Qty).Should().Equal(new[] { 3, 2, 1 });
    }

    /// <summary>
    /// The one that makes the feature work. A part cut on two different layouts is one part with one
    /// mark; numbering each page on its own would print a 1 on layout A and a 2 on layout B for the
    /// same drawing, and every mark made from the report would be wrong.
    /// </summary>
    [Fact]
    public void APartOnTwoLayoutsGetsOneNumberAndItsQuantityAddsUp()
    {
      var layouts = new List<(ISheetPlacement Sheet, int Count, string Name)>
      {
        (SheetOf(("bracket.dxf", 2)), 1, "A"),
        (SheetOf(("bracket.dxf", 1), ("plate.dxf", 1)), 1, "B"),
      };

      var totals = NestReportPdf.NumberedPartTotals(layouts);

      totals.Should().HaveCount(2, "two drawings, whatever they are spread across");
      var bracket = totals.Single(t => t.File == "bracket.dxf");
      bracket.Number.Should().Be(1);
      bracket.Qty.Should().Be(3, "two on the first layout and one on the second");
    }

    /// <summary>A layout cut several times produces its parts several times over.</summary>
    [Fact]
    public void CuttingALayoutMoreThanOnceMultipliesTheQuantityButNotTheNumbers()
    {
      var layouts = new List<(ISheetPlacement Sheet, int Count, string Name)>
      {
        (SheetOf(("bracket.dxf", 2)), 5, "A"),
      };

      var totals = NestReportPdf.NumberedPartTotals(layouts);

      totals.Should().ContainSingle().Which.Qty.Should().Be(10);
      totals[0].Number.Should().Be(1);
    }

    /// <summary>
    /// A mirrored copy is the other hand: a different physical part, cut from the same drawing, that
    /// takes its own mark. It has always been its own row in the report and so it earns its own number.
    /// </summary>
    [Fact]
    public void AMirroredCopyIsNumberedSeparatelyFromItsOriginal()
    {
      var placements = new List<IPartPlacement>
      {
        Part("bracket.dxf", 0, 0),
        Part("bracket.dxf", 20, 0),
        Mirrored(Part("bracket.dxf", 40, 0)),
      };
      var layouts = new List<(ISheetPlacement Sheet, int Count, string Name)>
      {
        (Sheet(placements), 1, "A"),
      };

      var totals = NestReportPdf.NumberedPartTotals(layouts);

      totals.Should().HaveCount(2);
      totals.Select(t => t.File).Should().BeEquivalentTo(new[] { "bracket.dxf", "bracket.dxf (mirrored)" });
      totals.Select(t => t.Number).Should().Equal(new[] { 1, 2 });
    }

    /// <summary>
    /// The number is anchored on the AREA CENTROID, not the middle of the bounding box. This L is the
    /// case that separates them: its box centre sits in the notch, off the material and on top of
    /// whatever is nested in there, while its centroid is on the part.
    /// </summary>
    [Fact]
    public void OnAnLTheNumberLandsOnTheMaterialAndNotInTheNotch()
    {
      INfp shape = Poly("angle.dxf", (0, 0), (10, 0), (10, 4), (4, 4), (4, 10), (0, 10));

      var (x, y, size) = NestReportPdf.NumberLabel(shape, "7", ox: 0, oy: 0, scale: 1);

      // Recover the centre the label was built around: Text() is given the baseline's left end.
      double centreX = x + (0.556 * size * "7".Length / 2.0);
      double centreY = y + (0.717 * size / 2.0);

      Inside(shape, centreX, centreY).Should().BeTrue("the number belongs on the part it names");
      Inside(shape, 5, 5).Should().BeFalse("and the bounding-box centre, which is what this replaces, is not");
      centreX.Should().BeApproximately(3.875, 1e-6);
      centreY.Should().BeApproximately(3.875, 1e-6);
    }

    [Theory]
    [InlineData(1, 4.0)]       // a tiny part still gets a number that survives printing
    [InlineData(100, 12.0)]    // and a big one does not get a silly one
    [InlineData(20, 8.4)]      // in between, the number follows the part
    public void TheNumberIsSizedToThePartWithinBounds(double side, double expected)
    {
      INfp shape = Poly("square.dxf", (0, 0), (side, 0), (side, side), (0, side));

      var (_, _, size) = NestReportPdf.NumberLabel(shape, "1", ox: 0, oy: 0, scale: 1);

      size.Should().BeApproximately(expected, 1e-9);
    }

    /// <summary>
    /// End to end, on the bytes that reach the paper. The writer leaves its content streams
    /// uncompressed, so the drawing's labels can be counted: they are the only bold text the report
    /// sets at this size, and there must be exactly one per placed part on every layout page.
    /// </summary>
    [Fact]
    public void EveryPlacedPartCarriesItsNumberOnTheDrawing()
    {
      // Small parts on a big sheet so the label size clamps to its floor of 4, which no table uses.
      var layouts = new List<(ISheetPlacement Sheet, int Count, string Name)>
      {
        (SheetOf(1.0, ("bracket.dxf", 3), ("plate.dxf", 1)), 2, "A"),
        (SheetOf(1.0, ("bracket.dxf", 2)), 1, "B"),
      };

      string path = Path.Combine(Path.GetTempPath(), $"sheetnest-report-{Guid.NewGuid():N}.pdf");
      try
      {
        NestReportPdf.Write(path, layouts, unplacedCount: 0);

        string pdf = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));

        Occurrences(pdf, "/F2 4 Tf").Should().Be(
          6,
          "four parts drawn on the first layout page and two on the second, each with its number");
        pdf.Should().Contain("(No.) Tj", "the table needs the column the drawing refers to");
        pdf.Should().Contain("(1) Tj").And.Contain("(2) Tj");
      }
      finally
      {
        File.Delete(path);
      }
    }

    private static int Occurrences(string haystack, string needle)
    {
      int count = 0;
      for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
           i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
      {
        count++;
      }

      return count;
    }

    /// <summary>Ray casting, so the L test asserts what it says it does rather than trusting a number.</summary>
    private static bool Inside(INfp poly, double x, double y)
    {
      var pts = poly.Points;
      bool inside = false;
      for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
      {
        if (((pts[i].Y > y) != (pts[j].Y > y))
          && (x < (((pts[j].X - pts[i].X) * (y - pts[i].Y)) / (pts[j].Y - pts[i].Y)) + pts[i].X))
        {
          inside = !inside;
        }
      }

      return inside;
    }

    private static INfp Poly(string name, params (double X, double Y)[] points)
      => new NoFitPolygon(points.Select(p => new SvgPoint(p.X, p.Y)).ToList()) { Name = name };

    private static IPartPlacement Part(string name, double x, double y)
      => new PartPlacement(Poly(name, (0, 0), (10, 0), (10, 10), (0, 10))) { X = x, Y = y, Source = 0 };

    private static IPartPlacement Part(string name, double x, double y, double side)
      => new PartPlacement(Poly(name, (0, 0), (side, 0), (side, side), (0, side))) { X = x, Y = y, Source = 0 };

    private static IPartPlacement Mirrored(IPartPlacement placement)
    {
      placement.IsMirrored = true;
      return placement;
    }

    private static ISheetPlacement Sheet(List<IPartPlacement> placements)
      => new SheetPlacement(PlacementTypeEnum.Gravity, DeepNestLib.Sheet.NewSheet(1, 200, 100), placements, 0, false);

    private static ISheetPlacement SheetOf(params (string Name, int Copies)[] parts)
      => SheetOf(10.0, parts);

    private static ISheetPlacement SheetOf(double side, params (string Name, int Copies)[] parts)
    {
      var placements = new List<IPartPlacement>();
      double x = 0;
      foreach (var (name, copies) in parts)
      {
        for (int i = 0; i < copies; i++)
        {
          placements.Add(Part(name, x, 0, side));
          x += side * 2;
        }
      }

      return Sheet(placements);
    }
  }
}
