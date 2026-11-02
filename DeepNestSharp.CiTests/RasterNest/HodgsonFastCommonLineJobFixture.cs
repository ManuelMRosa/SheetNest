namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
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
  /// The operator's Hodgson job through the Fast search alone, the first round of a timed nest and what a job too
  /// big for one sheet gets: 150 copies, common line, four-way rotation, a quarter inch of spacing and border, one
  /// 120 x 60 in sheet. Every seed found a plan, then rigid compaction left two clusters 0.0001 in inside their
  /// spacing and the sheet went back to the baseline with no shared cut at all.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public sealed class HodgsonFastCommonLineJobFixture
  {
    private const string DxfDirectoryVariable = "SHEETNEST_MIXED_COMMON_LINE_DXF_DIR";
    private const double Spacing = 0.25D;
    private readonly ITestOutputHelper output;

    public HodgsonFastCommonLineJobFixture(ITestOutputHelper output) => this.output = output;

    [HodgsonRowPatternJobFixture.HodgsonJobFact]
    public void TheFastSearchKeepsItsSharedCutsWhenCompactionCrowdsTwoClusters()
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

      INestResult result = SparrowNestService.Nest(
        parts,
        new[] { (120, 60, 1, false) },
        rotations: 4,
        spacing: Spacing,
        margin: 0.25D,
        perSheetBudgetSec: 5,
        sparrowExePath: SparrowExe.Resolve(),
        error: out string error,
        effort: NestEffort.Fast,
        commonLineGap: 0.010D,
        geometryTolerance: 0.0001D);
      clock.Stop();

      result.Should().NotBeNull(error);
      var sheets = result.UsedSheets.ToList();
      var edges = sheets.SelectMany(s => s.CommonEdges ?? Array.Empty<CommonEdge>()).ToList();
      double shared = edges.Sum(e => Math.Sqrt(((e.End.X - e.Start.X) * (e.End.X - e.Start.X)) + ((e.End.Y - e.Start.Y) * (e.End.Y - e.Start.Y))));
      int isolated = sheets.Sum(HodgsonRowPatternJobFixture.Isolated);
      this.output.WriteLine(FormattableString.Invariant(
        $"HODGSONx150 Fast: wallMs={clock.ElapsedMilliseconds} sheets={sheets.Count} edges={edges.Count} shared={shared:F4} isolated={isolated} hash={LargeCommonLineJobBenchFixture.Hash(sheets)}"));
      sheets.Should().ContainSingle();
      sheets[0].PartPlacements.Should().HaveCount(150);
      result.UnplacedParts.Should().BeEmpty();
      edges.Should().NotBeEmpty("the planner found a plan on every seed, and a compaction that fails its checks must not throw it away");
      isolated.Should().BeLessThan(15, "nearly every part shares a cut (none left on its own when measured)");
      HodgsonRowPatternJobFixture.UnsafePairs(sheets[0]).Should().Be(0);
    }
  }
}
