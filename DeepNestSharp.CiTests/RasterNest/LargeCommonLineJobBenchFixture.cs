namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  using System.Security.Cryptography;
  using System.Text;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// MEASUREMENT, not a promise: a large common-line job the way the app runs it, timed stage by stage.
  /// </summary>
  /// <remarks>
  /// <para>The job is the one that took over ten minutes on Fast: 150 copies of the Hodgson part, four-way
  /// rotation, common cutting on, one 120 x 60 in sheet, 0.25 in spacing and border. Every other common-line
  /// test stops at 50 copies on a 42 x 31 sheet with two rotations, which is not where the time goes.</para>
  /// <para>It prints a hash of the whole result, every placement and every shared cut, so a change meant
  /// only to make it faster can be held to producing the very same nest.</para>
  /// <para>Opt-in: set SHEETNEST_BENCH=1, SPARROW_EXE and SHEETNEST_HODGSON_DXF (the drawing is not in the
  /// repo). SHEETNEST_BENCH_EFFORT picks Fast (default), Normal or Best; SHEETNEST_BENCH_QTY the copies.
  /// SHEETNEST_BENCH_DXF="path*count;path*count" runs another job on the same sheet instead, and
  /// SHEETNEST_BENCH_COMMONLINE=off runs it with common cutting off.</para>
  /// </remarks>
  [Collection(SparrowEngineCollection.Name)]
  public class LargeCommonLineJobBenchFixture
  {
    private readonly ITestOutputHelper output;

    public LargeCommonLineJobBenchFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Fact]
    public void MeasureTheHodgsonJobOnAFullSheet()
    {
      string exe = SparrowExe.Resolve();
      string dxf = Environment.GetEnvironmentVariable("SHEETNEST_HODGSON_DXF");
      if (Environment.GetEnvironmentVariable("SHEETNEST_BENCH") != "1"
          || string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)
          || string.IsNullOrWhiteSpace(dxf) || !File.Exists(dxf))
      {
        this.output.WriteLine("BENCH: set SHEETNEST_BENCH=1, SPARROW_EXE and SHEETNEST_HODGSON_DXF to run.");
        return;
      }

      NestEffort effort = Enum.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_EFFORT"), true, out NestEffort chosen)
        ? chosen
        : NestEffort.Fast;
      int quantity = int.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_QTY"), out int qty) && qty > 0 ? qty : 150;
      bool commonLine = !string.Equals(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_COMMONLINE"), "off", StringComparison.OrdinalIgnoreCase);

      // Other jobs: SHEETNEST_BENCH_DXF="path*count;path*count". Without it, the Hodgson job above.
      var drawings = new List<(string Path, int Count)>();
      foreach (string entry in (Environment.GetEnvironmentVariable("SHEETNEST_BENCH_DXF") ?? string.Empty)
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
      {
        string[] fields = entry.Split('*');
        drawings.Add((fields[0], fields.Length > 1 && int.TryParse(fields[1], out int count) ? count : 1));
      }

      if (drawings.Count == 0)
      {
        drawings.Add((dxf, quantity));
      }

      // What MainWindow.OnNest hands the engine for a part with common cutting on, or off.
      var parts = drawings.Select(drawing => new RasterPartInfo
      {
        Path = drawing.Path,
        Quantity = drawing.Count,
        Rotations = 4,
        Spacing = 0.25D,
        Cc = commonLine ? CommonCuttingMode.Unrestricted : CommonCuttingMode.None,
        CommonCuttingDomain = commonLine ? "direct" : string.Empty,
        RequiresMergedLines = commonLine,
      }).ToArray();
      string jobName = string.Join("+", drawings.Select(d => FormattableString.Invariant($"{Path.GetFileNameWithoutExtension(d.Path)}x{d.Count}")));

      NestDiagnostics.Totals.Clear();
      SparrowNestService.DiagCommonLineTimings.Clear();
      long sparrowBefore = SparrowNestService.DiagSparrowMs;
      long mapBefore = SparrowNestService.DiagMapMs;
      var watch = Stopwatch.StartNew();
      INestResult result = SparrowNestService.Nest(
        parts,
        new List<(int, int, int, bool)> { (120, 60, 1, false) },
        rotations: 4,
        spacing: 0.25D,
        margin: 0.25D,
        perSheetBudgetSec: 5,
        sparrowExePath: exe,
        error: out string error,
        effort: effort,
        commonLineGap: 0.010D,
        geometryTolerance: 0.0001D);
      watch.Stop();

      Assert.True(result != null, error);
      var sheets = result.UsedSheets.ToList();
      var edges = sheets.SelectMany(s => s.CommonEdges ?? Array.Empty<CommonEdge>()).ToList();
      double shared = edges.Sum(e => Math.Sqrt(((e.End.X - e.Start.X) * (e.End.X - e.Start.X)) + ((e.End.Y - e.Start.Y) * (e.End.Y - e.Start.Y))));

      this.output.WriteLine(FormattableString.Invariant(
        $"JOB: {jobName} commonLine={(commonLine ? "on" : "off")} effort={effort} wall={watch.ElapsedMilliseconds}ms sparrowSum={SparrowNestService.DiagSparrowMs - sparrowBefore}ms mapSum={SparrowNestService.DiagMapMs - mapBefore}ms"));
      this.output.WriteLine(FormattableString.Invariant(
        $"RESULT: sheets={sheets.Count} placed={sheets.Sum(s => s.PartPlacements.Count)} unplaced={result.UnplacedParts.Count()} edges={edges.Count} shared={shared:F4} hash={Hash(sheets)}"));
      foreach (var total in NestDiagnostics.Totals.OrderBy(t => t.Key, StringComparer.Ordinal))
      {
        this.output.WriteLine(FormattableString.Invariant($"STAGE: {total.Key}={total.Value}"));
      }

      foreach (string timing in SparrowNestService.DiagCommonLineTimings)
      {
        this.output.WriteLine("SEED: " + timing);
      }

      foreach (string failure in SparrowNestService.DiagDirectFailures)
      {
        this.output.WriteLine("FAILURE: " + failure);
      }

      foreach (var notice in SparrowNestService.DiagCommonLineFallbacks)
      {
        this.output.WriteLine("FALLBACK: " + notice.Reason);
      }
    }

    /// <summary>Every placement and every shared cut, so two runs that differ anywhere differ here.</summary>
    private static string Hash(IEnumerable<ISheetPlacement> sheets)
    {
      var text = new StringBuilder();
      foreach (ISheetPlacement sheet in sheets)
      {
        foreach (string row in sheet.PartPlacements
          .Select(p => FormattableString.Invariant($"{p.Source}|{p.X:F6}|{p.Y:F6}|{p.Rotation:F6}|{p.IsMirrored}"))
          .OrderBy(s => s, StringComparer.Ordinal))
        {
          text.Append(row).Append(';');
        }

        foreach (string cut in (sheet.CommonEdges ?? Array.Empty<CommonEdge>())
          .Select(e => FormattableString.Invariant($"{e.Start.X:F6},{e.Start.Y:F6},{e.End.X:F6},{e.End.Y:F6}"))
          .OrderBy(s => s, StringComparer.Ordinal))
        {
          text.Append(cut).Append(';');
        }

        text.Append('#');
      }

      using var sha = SHA256.Create();
      return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Substring(0, 16);
    }
  }
}
