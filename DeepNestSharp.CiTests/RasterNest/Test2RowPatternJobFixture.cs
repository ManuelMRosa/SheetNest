namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// The operator's large job as the app runs it: 800 copies of test2.dxf, common line, 0 and 90 degrees, a
  /// quarter inch of spacing and border, unlimited 120 x 60 in sheets, nested for the default 15 seconds. He
  /// asked for 26 on every full sheet, every part sharing a cut, in under 25 seconds; the search alone takes
  /// about a minute for it.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public sealed class Test2RowPatternJobFixture
  {
    private const string DxfDirectoryVariable = "SHEETNEST_MIXED_COMMON_LINE_DXF_DIR";
    private const double Spacing = 0.25D;
    private readonly ITestOutputHelper output;

    public Test2RowPatternJobFixture(ITestOutputHelper output) => this.output = output;

    [Test2JobFact]
    public void EightHundredCopiesNestInTimeWithEveryPartSharing()
    {
      var parts = new[]
      {
        new RasterPartInfo
        {
          Path = Path.Combine(Environment.GetEnvironmentVariable(DxfDirectoryVariable)!, "test2.dxf"),
          Quantity = 800,
          Rotations = RotationCodes.RotZeroAnd90,
          Spacing = Spacing,
          Cc = CommonCuttingMode.SamePart,
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        },
      };
      var clock = Stopwatch.StartNew();

      INestResult result = SparrowNestService.NestForTime(
        parts,
        new[] { (120, 60, 1, true) },
        RotationCodes.RotZeroAnd90,
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
      this.output.WriteLine(FormattableString.Invariant(
        $"TEST2x800: wallMs={clock.ElapsedMilliseconds} best={report.BestRound} opening={report.OpeningFinished} stop={report.StopReason} sheets={sheets.Count} hash={LargeCommonLineJobBenchFixture.Hash(sheets)}"));
      report.OpeningFinished.Should().BeTrue();
      report.BestRound.Should().Be(0, "the search cannot finish 800 copies in 15 seconds, so the row pattern is the nest");
      sheets.Should().HaveCount(31);
      sheets.Take(30).Should().OnlyContain(s => s.PartPlacements.Count == 26);
      sheets[30].PartPlacements.Should().HaveCount(20);
      sheets.Sum(Isolated).Should().Be(0, "every part shares a cut");
      UnsafePairs(sheets[0]).Should().Be(0);
      UnsafePairs(sheets[30]).Should().Be(0);
      if (Environment.GetEnvironmentVariable("SHEETNEST_ASSERT_COMMON_LINE_PERF") == "1")
      {
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(25));
      }
    }

    private static int Isolated(ISheetPlacement sheet)
    {
      var welded = new HashSet<int>((sheet.CommonEdges ?? Array.Empty<CommonEdge>()).SelectMany(e => new[] { e.PartAId, e.PartBId }));
      return sheet.PartPlacements.Count(p => !welded.Contains(p.Id));
    }

    /// <summary>Two cuts closer than the spacing with no shared cut recorded between them cannot be cut.</summary>
    private static int UnsafePairs(ISheetPlacement sheet)
    {
      var welded = new HashSet<(int, int)>((sheet.CommonEdges ?? Array.Empty<CommonEdge>())
        .Select(e => (Math.Min(e.PartAId, e.PartBId), Math.Max(e.PartAId, e.PartBId))));
      var on = sheet.PartPlacements.ToList();
      int unsafePairs = 0;
      for (int i = 0; i < on.Count; i++)
      {
        for (int j = i + 1; j < on.Count; j++)
        {
          if (!welded.Contains((Math.Min(on[i].Id, on[j].Id), Math.Max(on[i].Id, on[j].Id)))
              && PlacementCollision.TooClose(on[i].PlacedPart, on[j].PlacedPart, Spacing - PlacementCollision.PlacementNoise, 0D))
          {
            unsafePairs++;
          }
        }
      }

      return unsafePairs;
    }

    private sealed class Test2JobFactAttribute : FactAttribute
    {
      public Test2JobFactAttribute()
      {
        string directory = Environment.GetEnvironmentVariable(DxfDirectoryVariable);
        string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, "test2.dxf")))
        {
          this.Skip = $"Set {DxfDirectoryVariable} to the directory containing test2.dxf.";
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
