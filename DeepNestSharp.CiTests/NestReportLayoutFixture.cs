namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
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
  /// The report is a nest sheet per layout, set out the way a shop already reads one: the settings top
  /// left, the parts list under them and the sheet drawn on the right. It carries what SheetNest knows
  /// and the operator needs, and nothing it would have to make up: no machine, tooling, material or
  /// thickness lines.
  /// <para>
  /// Every test reads the bytes that reach the paper. The writer leaves its content streams
  /// uncompressed, so each line of text can be found whole.
  /// </para>
  /// </summary>
  public class NestReportLayoutFixture
  {
    [Fact]
    public void EachLayoutGetsItsOwnPageAndThereIsNoSummaryPage()
    {
      string pdf = WriteAndRead("job.pdf", unplacedCount: 0);

      Regex.Matches(pdf, @"/Type /Page\b").Count.Should().Be(2, "two layouts, one page each and nothing else");
      pdf.Should().Contain("/Count 2");
    }

    [Fact]
    public void ThePageIsLetterLandscape()
    {
      string pdf = WriteAndRead("job.pdf", unplacedCount: 0);

      pdf.Should().Contain("/MediaBox [0 0 792 612]");
    }

    [Fact]
    public void ItIsSetInCourier()
    {
      string pdf = WriteAndRead("job.pdf", unplacedCount: 0);

      pdf.Should().Contain("/BaseFont /Courier ").And.Contain("/BaseFont /Courier-Bold ");
      pdf.Should().NotContain("Helvetica");
    }

    /// <summary>
    /// Each settings line is checked with its value beside it, per page: a label printed with the wrong
    /// layout's number would still pass a check that only looked for the label.
    /// </summary>
    [Fact]
    public void EachPageCarriesItsOwnLayoutsSettings()
    {
      string pdf = WriteAndRead("P1 96958 Wuxi.pdf", unplacedCount: 0);

      Line(pdf, "SETTING INFORMATION").Should().Be(2);
      Line(pdf, "NEST PARTS LIST").Should().Be(2);
      Line(pdf, @"DRAWING NAME +: P1 96958 Wuxi").Should().Be(2, "the job is named after the file it was saved as");
      Line(pdf, @"LAYOUT +: 1 of 2").Should().Be(1);
      Line(pdf, @"LAYOUT +: 2 of 2").Should().Be(1);
      Line(pdf, @"SHEET SIZE +: 200 x 100 in").Should().Be(1);
      Line(pdf, @"SHEET SIZE +: 120 x 60 in").Should().Be(1);
      Line(pdf, @"NUMBER OF SHEETS +: 3").Should().Be(1, "the first layout is cut three times");
      Line(pdf, @"NUMBER OF SHEETS +: 1").Should().Be(1);
      Line(pdf, @"SHEET UTILIZATION +: \d+\.\d%").Should().Be(2);
    }

    [Fact]
    public void ItLeavesOutWhatSheetNestDoesNotKnow()
    {
      string pdf = WriteAndRead("job.pdf", unplacedCount: 2);

      foreach (var absent in new[] { "MATERIAL", "THICKNESS", "MACHINE", "TOOL", "sq ft" })
      {
        pdf.Should().NotContain(absent);
      }
    }

    /// <summary>
    /// Num Off is what ONE sheet of the layout yields, the way a nest sheet counts it: the bracket sits
    /// twice on a layout cut three times, so its row says 2, not 6.
    /// </summary>
    [Fact]
    public void NumOffCountsOneSheetOfTheLayout()
    {
      string pdf = WriteAndRead("job.pdf", unplacedCount: 0);

      Line(pdf, "Item Part Name +Num Off").Should().Be(2);
      Line(pdf, @" +1 bracket\.dxf +2").Should().Be(1, "first page: the bracket, twice on the sheet");
      Line(pdf, @" +2 washer\.dxf +1").Should().Be(1);
      Line(pdf, @" +1 bracket\.dxf +1").Should().Be(1, "second page numbers its own parts from 1");
    }

    [Fact]
    public void PartsNotPlacedIsPrintedOnlyWhenThereAreSome()
    {
      WriteAndRead("job.pdf", unplacedCount: 0).Should().NotContain("NOT PLACED");

      Line(WriteAndRead("job.pdf", unplacedCount: 2), @"PARTS NOT PLACED +: 2").Should().Be(2, "on every page, so none can be read alone as the whole job");
    }

    /// <summary>How many times one whole line of text, matched by the pattern, is set on the paper.</summary>
    private static int Line(string pdf, string pattern)
      => Regex.Matches(pdf, @"\(" + pattern + @"\) Tj").Count;

    /// <summary>
    /// Two layouts on different sheets. The first carries two brackets and a washer with a hole and is cut
    /// three times; the second carries one bracket and is cut once.
    /// </summary>
    private static string WriteAndRead(string fileName, int unplacedCount)
    {
      var washer = Poly("washer.dxf", (0, 0), (10, 0), (10, 10), (0, 10));
      washer.Children.Add(new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(3, 3), new SvgPoint(7, 3), new SvgPoint(7, 7), new SvgPoint(3, 7),
      }));

      var first = new List<IPartPlacement>
      {
        new PartPlacement(Poly("bracket.dxf", (0, 0), (10, 0), (10, 10), (0, 10))) { X = 0, Y = 0, Source = 0 },
        new PartPlacement(Poly("bracket.dxf", (0, 0), (10, 0), (10, 10), (0, 10))) { X = 30, Y = 0, Source = 0 },
        new PartPlacement(washer) { X = 60, Y = 0, Source = 1 },
      };
      var second = new List<IPartPlacement>
      {
        new PartPlacement(Poly("bracket.dxf", (0, 0), (10, 0), (10, 10), (0, 10))) { X = 0, Y = 0, Source = 0 },
      };

      var layouts = new List<(ISheetPlacement Sheet, int Count, string Name)>
      {
        (new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(1, 200, 100), first, 0, false), 3, "A"),
        (new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(2, 120, 60), second, 0, false), 1, "B"),
      };

      string dir = Path.Combine(Path.GetTempPath(), $"sheetnest-layout-{Guid.NewGuid():N}");
      Directory.CreateDirectory(dir);
      try
      {
        string path = Path.Combine(dir, fileName);
        NestReportPdf.Write(path, layouts, unplacedCount);
        return Encoding.GetEncoding(28591).GetString(File.ReadAllBytes(path));
      }
      finally
      {
        Directory.Delete(dir, recursive: true);
      }
    }

    private static INfp Poly(string name, params (double X, double Y)[] points)
      => new NoFitPolygon(points.Select(p => new SvgPoint(p.X, p.Y)).ToList()) { Name = name };
  }
}
