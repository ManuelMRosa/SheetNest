namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// The operator's Hodgson job as the app runs it: 150 copies, common line, four-way rotation, a quarter inch
  /// of spacing and border, one 120 x 60 in sheet, nested for the default 15 seconds. He asked for it in under
  /// a minute with every part sharing a cut; the search alone takes over a minute and leaves a part on its own.
  /// Six hundred copies need two sheets, and there the search takes a quarter of an hour.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public sealed class HodgsonRowPatternJobFixture
  {
    private const string DxfDirectoryVariable = "SHEETNEST_MIXED_COMMON_LINE_DXF_DIR";
    private const double Spacing = 0.25D;

    /// <summary>How far apart the ends of two shared cuts may be and still meet at one node: two common-line gaps
    /// and twice the placement noise, the reach the common-line checks give a junction.</summary>
    private const double JunctionReach = (2 * 0.010D) + (2 * PlacementCollision.PlacementNoise);
    private readonly ITestOutputHelper output;

    public HodgsonRowPatternJobFixture(ITestOutputHelper output) => this.output = output;

    [HodgsonJobFact]
    public void AHundredAndFiftyCopiesNestInTimeWithEveryPartSharing()
    {
      var parts = new[]
      {
        new RasterPartInfo
        {
          Path = Path.Combine(Environment.GetEnvironmentVariable(DxfDirectoryVariable)!, "Hodgson.dxf"),
          Quantity = 150,
          Rotations = 4,
          Spacing = Spacing,
          Cc = CommonCuttingMode.Unrestricted,
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        },
      };
      var clock = Stopwatch.StartNew();

      INestResult result = SparrowNestService.NestForTime(
        parts,
        new[] { (120, 60, 1, false) },
        4,
        Spacing,
        0.25D,
        5,
        SparrowExe.Resolve(),
        TimeSpan.FromSeconds(NestRounds.DefaultSeconds),
        out string error,
        out NestRoundsReport report,
        commonLineGap: 0.010D,
        geometryTolerance: 0.0001D);
      clock.Stop();

      result.Should().NotBeNull(error);
      var sheets = result.UsedSheets.ToList();
      var edges = sheets.SelectMany(s => s.CommonEdges ?? Array.Empty<CommonEdge>()).ToList();
      double shared = edges.Sum(e => Math.Sqrt(((e.End.X - e.Start.X) * (e.End.X - e.Start.X)) + ((e.End.Y - e.Start.Y) * (e.End.Y - e.Start.Y))));
      double length = Length(sheets[0]);
      this.output.WriteLine(FormattableString.Invariant(
        $"HODGSONx150: wallMs={clock.ElapsedMilliseconds} best={report.BestRound} opening={report.OpeningFinished} stop={report.StopReason} sheets={sheets.Count} length={length:F3} edges={edges.Count} shared={shared:F4} hash={LargeCommonLineJobBenchFixture.Hash(sheets)}"));
      report.OpeningFinished.Should().BeTrue();
      report.BestRound.Should().Be(0, "the search takes over a minute, so the row pattern is the nest");
      sheets.Should().ContainSingle();
      sheets[0].PartPlacements.Should().HaveCount(150);
      result.UnplacedParts.Should().BeEmpty();
      Isolated(sheets[0]).Should().Be(0, "every part shares a cut");
      UnsafePairs(sheets[0]).Should().Be(0);
      length.Should().BeLessThan(37.4, "the shortest layout comes before more shared cut; with shared cut first the parts reached 50.04 in");
      if (Environment.GetEnvironmentVariable("SHEETNEST_ASSERT_COMMON_LINE_PERF") == "1")
      {
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
      }
    }

    /// <summary>Six hundred copies on as many sheets as they need. The search puts 419 and 181 on two sheets in
    /// about fifteen minutes, with 23 parts on their own. The pattern only takes jobs that fit on one sheet when a
    /// part's copies reach into each other's boxes, unless the part fills most of its box, as this one does.</summary>
    [HodgsonJobFact]
    public void SixHundredCopiesNestInTimeOnTwoSheetsWithEveryPartSharing()
    {
      var parts = new[]
      {
        new RasterPartInfo
        {
          Path = Path.Combine(Environment.GetEnvironmentVariable(DxfDirectoryVariable)!, "Hodgson.dxf"),
          Quantity = 600,
          Rotations = 4,
          Spacing = Spacing,
          Cc = CommonCuttingMode.Unrestricted,
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        },
      };
      var clock = Stopwatch.StartNew();

      INestResult result = SparrowNestService.NestForTime(
        parts,
        new[] { (120, 60, 1, true) },
        4,
        Spacing,
        0.25D,
        5,
        SparrowExe.Resolve(),
        TimeSpan.FromSeconds(NestRounds.DefaultSeconds),
        out string error,
        out NestRoundsReport report,
        commonLineGap: 0.010D,
        geometryTolerance: 0.0001D);
      clock.Stop();

      result.Should().NotBeNull(error);
      var sheets = result.UsedSheets.ToList();
      var edges = sheets.SelectMany(s => s.CommonEdges ?? Array.Empty<CommonEdge>()).ToList();
      double shared = edges.Sum(e => Math.Sqrt(((e.End.X - e.Start.X) * (e.End.X - e.Start.X)) + ((e.End.Y - e.Start.Y) * (e.End.Y - e.Start.Y))));
      this.output.WriteLine(FormattableString.Invariant(
        $"HODGSONx600: wallMs={clock.ElapsedMilliseconds} best={report.BestRound} opening={report.OpeningFinished} stop={report.StopReason} sheets={sheets.Count} perSheet={string.Join("+", sheets.Select(s => s.PartPlacements.Count))} lengths={string.Join("+", sheets.Select(s => Length(s).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))} edges={edges.Count} shared={shared:F4} hash={LargeCommonLineJobBenchFixture.Hash(sheets)}"));
      report.OpeningFinished.Should().BeTrue();
      report.BestRound.Should().Be(0, "the search takes a quarter of an hour, so the row pattern is the nest");
      sheets.Should().HaveCount(2, "no more sheets than the search");
      sheets.Sum(s => s.PartPlacements.Count).Should().Be(600);
      result.UnplacedParts.Should().BeEmpty();
      sheets.Should().OnlyContain(s => Isolated(s) == 0, "every part shares a cut");
      sheets.Should().OnlyContain(s => UnsafePairs(s) == 0);
      Length(sheets.OrderBy(s => s.PartPlacements.Count).First()).Should().BeLessThan(29.1, "the last sheet takes the shortest layout; with shared cut first it reached 50.04 in");
      if (Environment.GetEnvironmentVariable("SHEETNEST_ASSERT_COMMON_LINE_PERF") == "1")
      {
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
      }
    }

    /// <summary>How far along the sheet's long side, which runs along X here, the parts reach.</summary>
    internal static double Length(ISheetPlacement sheet) => SparrowNestService.Extent(sheet.PartPlacements.ToList()).W;

    internal static int Isolated(ISheetPlacement sheet)
    {
      var welded = new HashSet<int>((sheet.CommonEdges ?? Array.Empty<CommonEdge>()).SelectMany(e => new[] { e.PartAId, e.PartBId }));
      return sheet.PartPlacements.Count(p => !welded.Contains(p.Id));
    }

    /// <summary>Two cuts closer than the spacing with no shared cut recorded between them cannot be cut, unless
    /// they come close only at a node where their shared cuts with one common neighbour end, as at a cross or a
    /// T: the common-line checks cut that node as one junction. Two parts that come close anywhere else count.</summary>
    internal static int UnsafePairs(ISheetPlacement sheet)
      => UnsafePairs(sheet.PartPlacements.ToList(), sheet.CommonEdges ?? Array.Empty<CommonEdge>());

    internal static int UnsafePairs(IReadOnlyList<IPartPlacement> on, IReadOnlyList<CommonEdge> edges)
    {
      var welded = new HashSet<(int, int)>(edges.Select(e => (Math.Min(e.PartAId, e.PartBId), Math.Max(e.PartAId, e.PartBId))));
      var ends = edges
        .SelectMany(e => new[]
        {
          (Part: e.PartAId, Neighbour: e.PartBId, Point: e.Start),
          (Part: e.PartAId, Neighbour: e.PartBId, Point: e.End),
          (Part: e.PartBId, Neighbour: e.PartAId, Point: e.Start),
          (Part: e.PartBId, Neighbour: e.PartAId, Point: e.End),
        })
        .ToLookup(end => end.Part);
      int unsafePairs = 0;
      for (int i = 0; i < on.Count; i++)
      {
        for (int j = i + 1; j < on.Count; j++)
        {
          if (welded.Contains((Math.Min(on[i].Id, on[j].Id), Math.Max(on[i].Id, on[j].Id)))
              || !PlacementCollision.TooClose(on[i].PlacedPart, on[j].PlacedPart, Spacing - PlacementCollision.PlacementNoise, 0D))
          {
            continue;
          }

          var nodes = ends[on[i].Id]
            .SelectMany(a => ends[on[j].Id].Where(b => b.Neighbour == a.Neighbour && Distance(a.Point, b.Point) <= JunctionReach).Select(_ => a.Point))
            .ToList();
          if (nodes.Count == 0 || !CloseOnlyAt(on[i].PlacedPart, on[j].PlacedPart, nodes))
          {
            unsafePairs++;
          }
        }
      }

      return unsafePairs;
    }

    /// <summary>Every corner of either outline that lies within the spacing of the other lies within the spacing
    /// of one of the nodes. Two outlines that run close along a stretch always have such a corner away from
    /// the nodes, at one end of the stretch.</summary>
    private static bool CloseOnlyAt(INfp a, INfp b, IReadOnlyList<CommonLinePoint> nodes)
    {
      bool AwayFromNodes(SvgPoint p) => nodes.All(node => Math.Sqrt(((p.X - node.X) * (p.X - node.X)) + ((p.Y - node.Y) * (p.Y - node.Y))) > Spacing);
      return !a.Points.Any(p => AwayFromNodes(p) && DistanceToOutline(p, b) < Spacing)
        && !b.Points.Any(p => AwayFromNodes(p) && DistanceToOutline(p, a) < Spacing);
    }

    private static double DistanceToOutline(SvgPoint p, INfp outline)
    {
      SvgPoint[] points = outline.Points;
      double best = double.MaxValue;
      for (int k = 0; k < points.Length; k++)
      {
        SvgPoint s = points[k];
        SvgPoint e = points[(k + 1) % points.Length];
        double vx = e.X - s.X;
        double vy = e.Y - s.Y;
        double lengthSquared = (vx * vx) + (vy * vy);
        double t = lengthSquared <= 0 ? 0 : Math.Clamp((((p.X - s.X) * vx) + ((p.Y - s.Y) * vy)) / lengthSquared, 0, 1);
        double dx = p.X - (s.X + (t * vx));
        double dy = p.Y - (s.Y + (t * vy));
        best = Math.Min(best, Math.Sqrt((dx * dx) + (dy * dy)));
      }

      return best;
    }

    private static double Distance(CommonLinePoint a, CommonLinePoint b)
      => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    internal sealed class HodgsonJobFactAttribute : FactAttribute
    {
      public HodgsonJobFactAttribute()
      {
        string directory = Environment.GetEnvironmentVariable(DxfDirectoryVariable);
        string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "Hodgson.dxf")))
        {
          this.Skip = $"Set {DxfDirectoryVariable} to the directory containing Hodgson.dxf.";
        }
        else if (Environment.GetEnvironmentVariable("SHEETNEST_REQUIRE_SPARROW") != "1"
          && (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)))
        {
          this.Skip = "Set SPARROW_EXE to run the native nesting regression.";
        }
      }
    }
  }
}
