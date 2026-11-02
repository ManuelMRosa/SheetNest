namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// A timed nest is only allowed to choose HOW MANY rounds run, never what a round computes: each
  /// round that finishes must be, placement for placement, the fixed-effort nest it stands for.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class TimedNestRoundsFixture
  {
    private readonly ITestOutputHelper output;

    public TimedNestRoundsFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    /// <summary>
    /// Two sheets of stock and exactly two sheets of work, so a round that inherited the stock an earlier
    /// round used up would come back short and show here.
    /// </summary>
    [SparrowTheory]
    [InlineData(2)]
    public void TimedNestRoundsReproduceTheEffortNestsFromAFullStock(int sheets)
    {
      string exe = SparrowExe.Resolve();
      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        var parts = new List<RasterPartInfo>
        {
          new RasterPartInfo { Path = WriteRectangle(dir, "square", 4.5, 4.5), Quantity = 8 },
          new RasterPartInfo { Path = WriteRectangle(dir, "bar", 3, 6), Quantity = 4 },
        };
        var stock = new List<(int, int, int, bool)> { (20, 10, sheets, false) };
        var rounds = new Dictionary<int, string>();

        var timed = SparrowNestService.NestForTime(
          parts,
          stock,
          4,
          0.25,
          0.25,
          5,
          exe,
          TimeSpan.FromMinutes(10),
          out string timedError,
          out NestRoundsReport report,
          roundFinished: (round, result) => rounds[round] = LargeCommonLineJobBenchFixture.Hash(result.UsedSheets));

        timed.Should().NotBeNull($"the job must nest (err={timedError})");
        this.output.WriteLine(FormattableString.Invariant(
          $"TIMED: finished={report.FinishedRounds} best={report.BestRound} stop={report.StopReason} rounds={string.Join(",", rounds.Keys)}"));
        rounds.Keys.Should().Contain(new[] { 1, 2 }, "ten minutes is far more than this job needs for Fast and Normal");
        rounds.Keys.Should().Contain(0, "a job the row pattern does not take opens with the quick search");

        foreach (var (round, hash) in rounds.Where(r => r.Key >= 1 && r.Key <= 3).OrderBy(r => r.Key))
        {
          var fixedEffort = SparrowNestService.Nest(
            parts,
            stock,
            4,
            0.25,
            0.25,
            5,
            exe,
            out string fixedError,
            effort: NestRounds.EffortOf(round));
          fixedEffort.Should().NotBeNull($"the {NestRounds.EffortOf(round)} nest must run (err={fixedError})");
          string expected = LargeCommonLineJobBenchFixture.Hash(fixedEffort.UsedSheets);
          this.output.WriteLine(FormattableString.Invariant(
            $"ROUND {round} {NestRounds.EffortOf(round)}: timed={hash} fixed={expected} placed={fixedEffort.TotalPlacedCount}"));
          hash.Should().Be(expected, $"round {round} must be exactly the {NestRounds.EffortOf(round)} nest");
        }
      }
      finally
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
      }
    }

    private static string WriteRectangle(string dir, string name, double width, double height)
    {
      var file = new IxMilia.Dxf.DxfFile();
      var pts = new[]
      {
        new IxMilia.Dxf.DxfPoint(0, 0, 0), new IxMilia.Dxf.DxfPoint(width, 0, 0),
        new IxMilia.Dxf.DxfPoint(width, height, 0), new IxMilia.Dxf.DxfPoint(0, height, 0),
        new IxMilia.Dxf.DxfPoint(0, 0, 0),
      };
      file.Entities.Add(new IxMilia.Dxf.Entities.DxfPolyline(pts.Select(p => new IxMilia.Dxf.Entities.DxfVertex(p))) { IsClosed = true });
      string path = Path.Combine(dir, name + ".dxf");
      file.Save(path);
      return path;
    }
  }
}
