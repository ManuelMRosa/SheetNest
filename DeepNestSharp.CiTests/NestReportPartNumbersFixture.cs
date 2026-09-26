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
  /// Each layout page numbers its parts from 1 and prints that number on each of their copies in the
  /// sheet drawing, so an operator can read a number off the drawing and find its row in the list on the
  /// same page. The number is therefore only worth anything if it means the SAME part everywhere it
  /// appears on that page.
  /// <para>
  /// That a second layout starts again from 1, and that Num Off counts one sheet, is checked on the
  /// printed bytes in <see cref="NestReportLayoutFixture"/>.
  /// </para>
  /// </summary>
  public class NestReportPartNumbersFixture
  {
    /// <summary>
    /// Names in natural order, the way a file listing shows them. These three separate it from both
    /// orders it could be mistaken for: plain text order puts p103 first, and largest quantity first,
    /// which the report used before, puts p88 first.
    /// </summary>
    [Fact]
    public void NumbersRunFromOneInNaturalNameOrder()
    {
      var parts = NestReportPdf.NumberedParts(SheetOf(("p103.dxf", 1), ("p88.dxf", 3), ("p9.dxf", 2)));

      parts.Select(t => t.File).Should().Equal(new[] { "p9.dxf", "p88.dxf", "p103.dxf" });
      parts.Select(t => t.Number).Should().Equal(new[] { 1, 2, 3 }, "the list reads 1, 2, 3 down the page");
      parts.Select(t => t.Qty).Should().Equal(new[] { 2, 3, 1 });
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

      var parts = NestReportPdf.NumberedParts(Sheet(placements));

      parts.Select(t => t.File).Should().Equal(new[] { "bracket.dxf", "bracket.dxf (mirrored)" });
      parts.Select(t => t.Number).Should().Equal(new[] { 1, 2 });
      parts.Select(t => t.Qty).Should().Equal(new[] { 2, 1 });
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
      double centreX = x + (0.6 * size * "7".Length / 2.0);
      double centreY = y + (0.616 * size / 2.0);

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
    /// A ring's area centroid is its own HOLE, not its material. That matters because the nester parks
    /// other parts inside big cutouts, so a number left on the centroid lands on top of the number of
    /// whatever is nested in there and neither can be read. The number belongs on the material band.
    /// </summary>
    [Fact]
    public void OnASquareFrameTheNumberSitsOnTheBandAndNotInTheCutout()
    {
      INfp frame = Poly("frame.dxf", (0, 0), (20, 0), (20, 20), (0, 20));
      INfp cutout = Poly("cutout", (5, 5), (15, 5), (15, 15), (5, 15));
      frame.Children.Add(cutout);

      var (centreX, centreY, size) = LabelCentre(frame, "7");

      Inside(cutout, 10, 10).Should().BeTrue("the centroid, which is what this replaces, is in the hole");
      Inside(frame, centreX, centreY).Should().BeTrue("the number belongs on the part it names");
      Inside(cutout, centreX, centreY).Should().BeFalse("and not in the cutout, where another part goes");
      size.Should().BeApproximately(4.0, 1e-9, "a 5 wide band only has room for the smallest number");
    }

    /// <summary>
    /// The same, on the shape that actually comes out of the DXF reader: arcs are tessellated at 6
    /// degrees at most, so a circle arrives as 60 points and a ring as two of them.
    /// </summary>
    [Fact]
    public void OnACircularRingTheNumberSitsOnTheBandAndNotInTheBore()
    {
      INfp ring = Circle("ring.dxf", 40, 40, 30);
      INfp bore = Circle("bore", 40, 40, 15);
      ring.Children.Add(bore);

      var (centreX, centreY, size) = LabelCentre(ring, "7");

      Inside(bore, 40, 40).Should().BeTrue("the centroid, which is what this replaces, is in the bore");
      Inside(ring, centreX, centreY).Should().BeTrue("the number belongs on the ring");
      Inside(bore, centreX, centreY).Should().BeFalse("and not in the bore");
      size.Should().BeLessThan(
        12.0,
        "sized to the 15 wide band, not to the 60 wide outer circle, which would clamp at the maximum");
      size.Should().BeGreaterThanOrEqualTo(4.0);
    }

    /// <summary>
    /// The size has to follow the MATERIAL, not the bounding box. This ring is picked so the answer is
    /// known on paper: a 100 square with a 60 square hole leaves a 20 wide band, and the roomiest spot
    /// is not the middle of that band but the CORNER POCKET, where a circle can touch two outer edges
    /// and the hole's corner at once. It sits at 20*sqrt(2)/(1+sqrt(2)) = 11.716, so the number is
    /// 2*11.716*0.42 = 9.84 points: below the 12 the bounding box would demand, above the 8.4 the band
    /// midline would give, and clear of the 4 floor. One number that separates all three.
    /// <para>
    /// The whole glyph is checked, not just its centre, because a number that straddles the hole edge
    /// is still unreadable.
    /// </para>
    /// </summary>
    [Fact]
    public void OnARingTheNumberIsSizedToTheMaterialBandNotTheBoundingBox()
    {
      INfp plate = Poly("plate.dxf", (0, 0), (100, 0), (100, 100), (0, 100));
      INfp window = Poly("window", (20, 20), (80, 20), (80, 80), (20, 80));
      plate.Children.Add(window);

      var (x, y, size) = NestReportPdf.NumberLabel(plate, "7", ox: 0, oy: 0, scale: 1);

      size.Should().BeApproximately(
        9.84,
        0.15,
        "the number is sized to the 20 wide band's roomiest corner, not to the 100 wide plate");

      double right = x + (0.6 * size);
      double top = y + (0.616 * size);
      foreach (var (cx, cy) in new[] { (x, y), (right, y), (x, top), (right, top) })
      {
        Inside(plate, cx, cy).Should().BeTrue("every corner of the number sits on the plate");
        Inside(window, cx, cy).Should().BeFalse("and none of it hangs over the hole");
      }
    }

    /// <summary>
    /// The narrow scope, pinned. A part whose centroid IS on the material must come back with the
    /// number it has always had, to the last decimal, holes or no holes. This one has a hole, so it
    /// proves the search is skipped because the centroid works and not because there was nothing to
    /// look at. If a later change widens the slow path to everyone, this is what says so.
    /// </summary>
    [Fact]
    public void APartWhoseCentroidIsOnMaterialKeepsExactlyTheNumberItAlreadyHad()
    {
      INfp plate = Poly("plate.dxf", (0, 0), (100, 0), (100, 100), (0, 100));
      plate.Children.Add(Poly("hole", (5, 5), (15, 5), (15, 15), (5, 15)));

      var (x, y, size) = NestReportPdf.NumberLabel(plate, "7", ox: 0, oy: 0, scale: 1);

      size.Should().BeApproximately(12.0, 1e-9, "0.42 of the 100 side, clamped at the ceiling");
      x.Should().BeApproximately(50.0 - (0.6 * 12.0 / 2.0), 1e-9);
      y.Should().BeApproximately(50.0 - (0.616 * 12.0 / 2.0), 1e-9);
    }

    /// <summary>
    /// A band too thin for even the smallest number still gets one. That is a decision, not an
    /// oversight: a part with no number is a part the operator cannot mark, so the number goes on the
    /// material at the 4 point floor and is allowed to graze the outline. The ANCHOR is on the
    /// material; the glyph around it is not checked, because here it deliberately overhangs.
    /// </summary>
    [Fact]
    public void OnARingTooThinForTheSmallestDigitTheNumberStillGoesOnTheMaterial()
    {
      INfp plate = Poly("thin.dxf", (0, 0), (100, 0), (100, 100), (0, 100));
      INfp window = Poly("window", (1, 1), (99, 1), (99, 99), (1, 99));
      plate.Children.Add(window);

      var (centreX, centreY, size) = LabelCentre(plate, "7");

      size.Should().BeApproximately(4.0, 1e-9, "a 1 wide band has room for nothing bigger than the floor");
      Inside(plate, centreX, centreY).Should().BeTrue("the number still belongs on the part");
      Inside(window, centreX, centreY).Should().BeFalse("and still not in the hole another part goes in");
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
        pdf.Should().MatchRegex(@"\(Item Part Name +Num Off\) Tj", "the table needs the column the drawing refers to");
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

    /// <summary>The centre the label was built around: Text() is given the baseline's left end.</summary>
    private static (double X, double Y, double Size) LabelCentre(INfp shape, string text)
    {
      var (x, y, size) = NestReportPdf.NumberLabel(shape, text, ox: 0, oy: 0, scale: 1);
      return (x + (0.6 * size * text.Length / 2.0), y + (0.616 * size / 2.0), size);
    }

    /// <summary>A circle as the DXF reader delivers one: 60 points, its 6 degree tessellation floor.</summary>
    private static INfp Circle(string name, double cx, double cy, double radius)
    {
      var pts = new List<SvgPoint>();
      for (int i = 0; i < 60; i++)
      {
        double a = i * 2 * Math.PI / 60.0;
        pts.Add(new SvgPoint(cx + (radius * Math.Cos(a)), cy + (radius * Math.Sin(a))));
      }

      return new NoFitPolygon(pts) { Name = name };
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
