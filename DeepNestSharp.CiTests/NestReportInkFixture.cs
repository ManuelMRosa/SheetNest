namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text;
  using System.Text.RegularExpressions;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.Reports;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The report is printed, and it is printed on whatever is in the shop. So it carries no colour at
  /// all: greys are fine because they survive a laser printer, but nothing chromatic. Parts and their
  /// holes are drawn as outlines, never filled, so a sheet costs almost no toner and a marked-up copy
  /// stays readable.
  /// <para>
  /// Both tests read the bytes that actually reach the paper. The writer leaves its content streams
  /// uncompressed, so the colour and paint operators can be read straight out of the file.
  /// </para>
  /// </summary>
  public class NestReportInkFixture
  {
    /// <summary>
    /// Every colour operator in the file has to be a grey, meaning its three components are equal.
    /// The job deliberately leaves parts unplaced, because the "Parts NOT placed" row is the one place
    /// the report used to reach for a real colour.
    /// </summary>
    [Fact]
    public void EveryColourInTheReportIsAGrey()
    {
      string pdf = WriteAndRead(unplacedCount: 2);

      var offenders = ColourOperators(pdf)
        .Where(c => !(c.R == c.G && c.G == c.B))
        .Select(c => FormattableString.Invariant($"{c.R} {c.G} {c.B} {c.Op}"))
        .Distinct()
        .ToList();

      offenders.Should().BeEmpty(
        "the report prints in black and white, so every rg/RG operator must have three equal components");
    }

    /// <summary>
    /// Parts and holes are stroked and never filled. Asserted from both ends on purpose: no fill
    /// operator anywhere, AND the exact number of outlines the drawing owes. Without the second half a
    /// report that drew no parts at all would pass.
    /// </summary>
    [Fact]
    public void PartsAndHolesAreDrawnAsOutlinesAndNothingIsFilled()
    {
      string pdf = WriteAndRead(unplacedCount: 0);

      // "b" closes, fills and strokes; it is the only way this writer ever filled a part or a hole.
      Occurrences(pdf, "rg b").Should().Be(
        0,
        "a part or a hole that still fills would paint over the sheet and cost toner");

      // Three parts drawn, one of which carries a hole, on the single layout page.
      Occurrences(pdf, StrokeOnlyPaint).Should().Be(
        4,
        "three part outlines and one hole outline, each closed and stroked without a fill");
    }

    /// <summary>The paint a stroked-only polygon ends with. Distinct from Rect, which paints with "re S".</summary>
    private const string StrokeOnlyPaint = "0.15 0.15 0.15 RG s";

    /// <summary>
    /// One layout: two plain squares and a third square carrying a hole, so both the part branch and
    /// the hole branch of the drawing are exercised.
    /// </summary>
    private static string WriteAndRead(int unplacedCount)
    {
      var withHole = Poly("washer.dxf", (0, 0), (10, 0), (10, 10), (0, 10));
      withHole.Children.Add(new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(3, 3), new SvgPoint(7, 3), new SvgPoint(7, 7), new SvgPoint(3, 7),
      }));

      var placements = new List<IPartPlacement>
      {
        new PartPlacement(Poly("bracket.dxf", (0, 0), (10, 0), (10, 10), (0, 10))) { X = 0, Y = 0, Source = 0 },
        new PartPlacement(Poly("bracket.dxf", (0, 0), (10, 0), (10, 10), (0, 10))) { X = 30, Y = 0, Source = 0 },
        new PartPlacement(withHole) { X = 60, Y = 0, Source = 0 },
      };

      var layouts = new List<(ISheetPlacement Sheet, int Count, string Name)>
      {
        (new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(1, 200, 100), placements, 0, false), 1, "A"),
      };

      string path = Path.Combine(Path.GetTempPath(), $"sheetnest-ink-{Guid.NewGuid():N}.pdf");
      try
      {
        NestReportPdf.Write(path, layouts, unplacedCount);
        return Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));
      }
      finally
      {
        File.Delete(path);
      }
    }

    private static IEnumerable<(double R, double G, double B, string Op)> ColourOperators(string pdf)
    {
      foreach (Match m in Regex.Matches(pdf, @"(\d+(?:\.\d+)?) (\d+(?:\.\d+)?) (\d+(?:\.\d+)?) (rg|RG)\b"))
      {
        yield return (
          double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
          double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
          double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
          m.Groups[4].Value);
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

    private static INfp Poly(string name, params (double X, double Y)[] points)
      => new NoFitPolygon(points.Select(p => new SvgPoint(p.X, p.Y)).ToList()) { Name = name };
  }
}
