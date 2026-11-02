namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// The nest by the clock, end to end with the real engine: a time per sheet is a time the operator waits,
  /// so the nest has to come back within it, with every part placed.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class ClockNestFixture
  {
    /// <summary>What the test machine may add on top of the nest's own leeway: starting the test, loading the
    /// engine for the first time, a busy runner.</summary>
    private static readonly TimeSpan MachineSlack = TimeSpan.FromSeconds(1.5);

    private readonly ITestOutputHelper output;

    public ClockNestFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    /// <summary>Three sheets' worth of invented parts, with common line and without, at two seconds a sheet.</summary>
    [SparrowTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATimePerSheetIsKept(bool commonLine)
    {
      string exe = SparrowExe.Resolve();
      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        var parts = new List<RasterPartInfo>
        {
          Part(WriteRectangle(dir, "square", 4.5, 4.5), 30, commonLine),
          Part(WriteRectangle(dir, "bar", 3, 9), 24, commonLine),
          Part(WriteRectangle(dir, "plate", 7, 5), 12, commonLine),
        };
        var stock = new List<(int, int, int, bool)> { (40, 20, 1, true) };
        TimeSpan limit = TimeSpan.Zero;
        var watch = Stopwatch.StartNew();

        var result = SparrowNestService.NestForTime(
          parts,
          stock,
          4,
          0.25,
          0.25,
          5,
          exe,
          NestRounds.NoLimit,
          out string error,
          out NestRoundsReport report,
          secondsPerSheet: 2,
          limit: known => limit = known);
        watch.Stop();

        result.Should().NotBeNull($"the job must nest (err={error})");
        this.output.WriteLine(FormattableString.Invariant(
          $"CLOCK: commonLine={commonLine} limit={limit.TotalSeconds:F1}s wall={watch.Elapsed.TotalSeconds:F2}s sheets={result.UsedSheets.Count()} placed={result.TotalPlacedCount} stop={report.StopReason} overTime={report.OverTime} edges={result.UsedSheets.Sum(s => s.CommonEdges?.Count ?? 0)}"));
        limit.Should().BeGreaterThan(TimeSpan.Zero, "a time per sheet gives the job a limit");
        result.UnplacedParts.Should().BeEmpty("there is stock for every part");
        watch.Elapsed.Should().BeLessThan(limit + SparrowNestService.ClockLeeway + MachineSlack, "the nest is built to end on its time");
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

    private static RasterPartInfo Part(string path, int quantity, bool commonLine)
      => commonLine
        ? new RasterPartInfo
        {
          Path = path,
          Quantity = quantity,
          Cc = DeepNestLib.NestProject.CommonCuttingMode.Unrestricted,
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        }
        : new RasterPartInfo { Path = path, Quantity = quantity };

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
