namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text;
  using System.Text.RegularExpressions;
  using System.Windows.Media;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.Reports;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Nest report on screen is the PDF's page, laid out by the same code, with one difference: there is no paper,
  /// so on the viewer's grey the sheet is the only white thing. These tests hold the screen to the printed page by
  /// comparing what each actually draws: the text the PDF writes against the glyphs on screen, the outlines the PDF
  /// strokes against the ones on screen, and the sheet rectangle against the one white fill.
  /// </summary>
  public class NestReportPreviewFixture
  {
    private static readonly DateTime When = new DateTime(2026, 9, 10, 8, 46, 49);

    /// <summary>
    /// Compared as one string per page rather than line by line: WPF may break a line of text into several glyph
    /// runs, but it cannot change a character or its order without this failing.
    /// </summary>
    [Fact]
    public void EachPageOnScreenSaysExactlyWhatThePrintedPageSays()
    {
      var layouts = Layouts();
      var printed = PrintedPages(layouts, unplacedCount: 2);

      printed.Should().HaveCount(layouts.Count, "one printed page per layout");
      for (int i = 0; i < layouts.Count; i++)
      {
        var screen = NestReportPdf.RenderPage(layouts, i, "job", 2, "in", When);

        string.Concat(ScreenText(screen.Drawing)).Should().Be(
          string.Concat(printed[i].Text),
          $"page {i + 1} on screen must carry every character of page {i + 1} of the PDF, in the same order");
      }
    }

    [Fact]
    public void EachPageOnScreenOutlinesWhatThePrintedPageOutlines()
    {
      var layouts = Layouts();
      var printed = PrintedPages(layouts, unplacedCount: 0);

      for (int i = 0; i < layouts.Count; i++)
      {
        var drawings = Geometry(NestReportPdf.RenderPage(layouts, i, "job", 0, "in", When).Drawing).ToList();

        drawings.Count(d => d.Brush == null && d.Geometry is StreamGeometry).Should().Be(
          printed[i].Outlines,
          $"page {i + 1} outlines every part and hole the PDF outlines, no more and no fewer");
      }
    }

    /// <summary>
    /// No paper: the only visible fill on the page is the sheet, it is white, and it sits exactly where the PDF
    /// strokes the sheet (the screen measures Y down from the top of the page, the PDF up from the bottom).
    /// </summary>
    [Fact]
    public void OnScreenOnlyTheSheetIsWhiteAndItSitsWhereThePdfDrawsIt()
    {
      var layouts = Layouts();
      var printed = PrintedPages(layouts, unplacedCount: 0);

      for (int i = 0; i < layouts.Count; i++)
      {
        var filled = Geometry(NestReportPdf.RenderPage(layouts, i, "job", 0, "in", When).Drawing)
          .Where(d => d.Brush is SolidColorBrush brush && brush.Color.A > 0)
          .ToList();

        filled.Should().ContainSingle($"page {i + 1}: nothing but the sheet is painted, so there is no paper");
        ((SolidColorBrush)filled[0].Brush).Color.Should().Be(Colors.White, "the sheet is the white thing on the grey");

        var (x, y, w, h) = printed[i].Sheet;
        var bounds = filled[0].Geometry.Bounds;
        bounds.X.Should().BeApproximately(x, 0.011);
        bounds.Y.Should().BeApproximately(612 - y - h, 0.011);
        bounds.Width.Should().BeApproximately(w, 0.011);
        bounds.Height.Should().BeApproximately(h, 0.011);
      }
    }

    /// <summary>
    /// Writes the real PDF and reads it back page by page: each content stream's text (the strings shown with Tj),
    /// how many closed, stroked-only outlines it paints, and the sheet rectangle.
    /// </summary>
    private static List<(List<string> Text, int Outlines, (double X, double Y, double W, double H) Sheet)> PrintedPages(
      IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> layouts,
      int unplacedCount)
    {
      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        string path = Path.Combine(dir, "job.pdf");
        NestReportPdf.Write(path, layouts, unplacedCount, "in", When);
        string pdf = Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));

        return Regex.Matches(pdf, @"stream\n(.*?)\nendstream", RegexOptions.Singleline).Cast<Match>().Select(page =>
        {
          string body = page.Groups[1].Value;
          var text = Regex.Matches(body, @"\((.*?)\) Tj").Cast<Match>().Select(t => Unescape(t.Groups[1].Value)).ToList();
          int outlines = Regex.Matches(body, @"0\.15 0\.15 0\.15 RG s").Count;
          var rect = Regex.Match(body, @"0\.9 w 0\.15 0\.15 0\.15 RG (\S+) (\S+) (\S+) (\S+) re S");
          return (text, outlines, (D(rect.Groups[1].Value), D(rect.Groups[2].Value), D(rect.Groups[3].Value), D(rect.Groups[4].Value)));
        }).ToList();
      }
      finally
      {
        Directory.Delete(dir, recursive: true);
      }
    }

    private static double D(string value) => double.Parse(value, CultureInfo.InvariantCulture);

    private static string Unescape(string pdfString)
      => pdfString.Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");

    private static IEnumerable<string> ScreenText(Drawing drawing)
    {
      if (drawing is DrawingGroup group)
      {
        foreach (var child in group.Children)
        {
          foreach (var text in ScreenText(child))
          {
            yield return text;
          }
        }
      }
      else if (drawing is GlyphRunDrawing glyphs)
      {
        yield return new string(glyphs.GlyphRun.Characters.ToArray());
      }
    }

    private static IEnumerable<GeometryDrawing> Geometry(Drawing drawing)
    {
      if (drawing is DrawingGroup group)
      {
        foreach (var child in group.Children)
        {
          foreach (var geometry in Geometry(child))
          {
            yield return geometry;
          }
        }
      }
      else if (drawing is GeometryDrawing geometry)
      {
        yield return geometry;
      }
    }

    /// <summary>
    /// Two layouts on different sheets: two brackets and a washer with a hole, then a single bracket, so the pages
    /// differ and one of them strokes a hole.
    /// </summary>
    private static IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> Layouts()
    {
      var washer = Square("washer.dxf");
      washer.Children.Add(new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(3, 3), new SvgPoint(7, 3), new SvgPoint(7, 7), new SvgPoint(3, 7),
      }));

      var first = new List<IPartPlacement>
      {
        new PartPlacement(Square("bracket.dxf")) { X = 0, Y = 0, Source = 0 },
        new PartPlacement(Square("bracket.dxf")) { X = 30, Y = 0, Source = 0 },
        new PartPlacement(washer) { X = 60, Y = 0, Source = 1 },
      };
      var second = new List<IPartPlacement>
      {
        new PartPlacement(Square("bracket.dxf")) { X = 0, Y = 0, Source = 0 },
      };

      return new List<(ISheetPlacement Sheet, int Count, string Name)>
      {
        (new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(1, 200, 100), first, 0, false), 3, "A"),
        (new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(2, 120, 60), second, 0, false), 1, "B"),
      };
    }

    private static INfp Square(string name)
      => new NoFitPolygon(new List<SvgPoint> { new SvgPoint(0, 0), new SvgPoint(10, 0), new SvgPoint(10, 10), new SvgPoint(0, 10) }) { Name = name };
  }
}
