namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
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
  /// repo). SHEETNEST_BENCH_EFFORT picks Fast (default), Normal or Best; SHEETNEST_BENCH_QTY the copies;
  /// SHEETNEST_BENCH_TIME=seconds nests for up to that long instead, in rounds, as the app does.
  /// SHEETNEST_BENCH_DXF="path*count;path*count" runs another job on the same sheet instead, and
  /// SHEETNEST_BENCH_COMMONLINE=off runs it with common cutting off. SHEETNEST_BENCH_ROTATIONS takes a
  /// rotation code (4 by default; 1002 is 0 and 90 degrees) and SHEETNEST_BENCH_STOCK=unlimited gives the
  /// sheet unlimited stock, so a large job runs on as many sheets as it needs.
  /// SHEETNEST_BENCH_SHEET=WxH changes the sheet (120x60 by default), SHEETNEST_BENCH_SHEETS=n stocks n of
  /// it, and SHEETNEST_BENCH_UNITS=mm reads the drawings and the sheet in millimetres, with the spacing,
  /// border and common-line settings scaled the way the app scales them. SHEETNEST_BENCH_TARGET=percent and
  /// SHEETNEST_BENCH_OFFCUT=end|side|both|auto hand the timed nest over on the material usage target.</para>
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
      int rotations = int.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_ROTATIONS"), out int code) ? code : 4;
      bool unlimited = string.Equals(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_STOCK"), "unlimited", StringComparison.OrdinalIgnoreCase);
      string[] sheetSize = (Environment.GetEnvironmentVariable("SHEETNEST_BENCH_SHEET") ?? "120x60").Split('x');
      int sheetW = int.Parse(sheetSize[0], CultureInfo.InvariantCulture);
      int sheetH = int.Parse(sheetSize[1], CultureInfo.InvariantCulture);
      int sheetCount = int.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_SHEETS"), out int stocked) && stocked > 0 ? stocked : 1;

      // MainWindow.OnNest: spacing and border are typed in drawing units, the common-line settings are kept
      // in inches and scaled.
      double unitScale = string.Equals(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_UNITS"), "mm", StringComparison.OrdinalIgnoreCase)
        ? CommonLineDefaults.InchesToMillimeters
        : 1.0;
      double spacing = 0.25D * unitScale;

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
        Rotations = rotations,
        Spacing = spacing,
        Cc = commonLine ? CommonCuttingMode.Unrestricted : CommonCuttingMode.None,
        CommonCuttingDomain = commonLine ? "direct" : string.Empty,
        RequiresMergedLines = commonLine,
      }).ToArray();
      string jobName = string.Join("+", drawings.Select(d => FormattableString.Invariant($"{Path.GetFileNameWithoutExtension(d.Path)}x{d.Count}")));

      NestDiagnostics.Totals.Clear();
      SparrowNestService.DiagCommonLineTimings.Clear();
      long sparrowBefore = SparrowNestService.DiagSparrowMs;
      long mapBefore = SparrowNestService.DiagMapMs;
      var stock = new List<(int, int, int, bool)> { (sheetW, sheetH, sheetCount, unlimited) };
      var watch = Stopwatch.StartNew();
      INestResult result;
      string error;

      // SHEETNEST_BENCH_TIME=seconds runs the job the way the app does now, nesting for up to that long,
      // and prints every round that finished so each can be held to the fixed-effort nest it repeats.
      // SHEETNEST_BENCH_SECONDS_PER_SHEET=seconds nests with the app's time per sheet instead, which wins over
      // SHEETNEST_BENCH_TIME: the job gets that much for every sheet it is expected to take.
      bool timed = int.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_TIME"), out int seconds) && seconds > 0;
      double perSheet = double.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_SECONDS_PER_SHEET"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ps) && ps > 0 ? ps : 0;
      timed |= perSheet > 0;
      if (timed)
      {
        TimeSpan limit = seconds > 0 ? TimeSpan.FromSeconds(seconds) : NestRounds.NoLimit;
        // SHEETNEST_BENCH_TARGET=percent hands the nest over on the material usage target, as the app does;
        // SHEETNEST_BENCH_OFFCUT=end|side|both|auto leaves out the offcut the app cuts with that setting.
        double target = int.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_BENCH_TARGET"), out int percent) && percent > 0 ? percent / 100.0 : 0;
        var offcut = BenchOffcut("SHEETNEST_BENCH_OFFCUT", unitScale != 1.0);
        long roundStart = 0;
        result = SparrowNestService.NestForTime(
          parts,
          stock,
          rotations: rotations,
          spacing: spacing,
          margin: spacing,
          perSheetBudgetSec: 5,
          sparrowExePath: exe,
          maxTime: limit,
          error: out error,
          report: out NestRoundsReport report,
          roundStarted: _ => roundStart = watch.ElapsedMilliseconds,
          commonLineGap: 0.010D * unitScale,
          geometryTolerance: 0.0001D * unitScale,
          roundFinished: (round, finished) => this.output.WriteLine(FormattableString.Invariant(
            $"ROUND {round} effort={(round == 0 ? "opening" : NestRounds.EffortOf(round).ToString())} ms={watch.ElapsedMilliseconds - roundStart} placed={finished.TotalPlacedCount} shared={NestRounds.Summarize(finished).SharedLength:F4} usage={NestRounds.UsedUtilization(finished, offcut) * 100:F2}% hash={Hash(finished.UsedSheets)}")),
          materialTarget: target,
          offcut: offcut,
          secondsPerSheet: perSheet,
          limit: known => limit = known);
        this.output.WriteLine(FormattableString.Invariant(
          $"TIMED: limit={(limit == NestRounds.NoLimit ? "none" : limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s")} perSheet={perSheet:0.###}s target={target * 100:F0}% best={report?.BestRound} finished={report?.FinishedRounds} overTime={report?.OverTime} stop={report?.StopReason} usage={(result == null ? 0 : NestRounds.UsedUtilization(result, offcut)) * 100:F2}% wall={watch.ElapsedMilliseconds}ms"));
      }
      else
      {
        result = SparrowNestService.Nest(
          parts,
          stock,
          rotations: rotations,
          spacing: spacing,
          margin: spacing,
          perSheetBudgetSec: 5,
          sparrowExePath: exe,
          error: out error,
          effort: effort,
          commonLineGap: 0.010D * unitScale,
          geometryTolerance: 0.0001D * unitScale);
      }

      watch.Stop();

      Assert.True(result != null, error);
      var sheets = result.UsedSheets.ToList();
      var edges = sheets.SelectMany(s => s.CommonEdges ?? Array.Empty<CommonEdge>()).ToList();
      double shared = edges.Sum(e => Math.Sqrt(((e.End.X - e.Start.X) * (e.End.X - e.Start.X)) + ((e.End.Y - e.Start.Y) * (e.End.Y - e.Start.Y))));

      // A part with no shared cut on its sheet is one the operator sees cut on its own.
      int isolated = sheets.Sum(s =>
      {
        var welded = new HashSet<int>((s.CommonEdges ?? Array.Empty<CommonEdge>()).SelectMany(e => new[] { e.PartAId, e.PartBId }));
        return s.PartPlacements.Count(p => !welded.Contains(p.Id));
      });

      this.output.WriteLine(FormattableString.Invariant(
        $"JOB: {jobName} commonLine={(commonLine ? "on" : "off")} rotations={rotations} sheet={sheetW}x{sheetH}{(unitScale == 1.0 ? "in" : "mm")} stock={(unlimited ? "unlimited" : sheetCount.ToString(CultureInfo.InvariantCulture))} effort={(timed ? "timed" : effort.ToString())} wall={watch.ElapsedMilliseconds}ms sparrowSum={SparrowNestService.DiagSparrowMs - sparrowBefore}ms mapSum={SparrowNestService.DiagMapMs - mapBefore}ms"));
      this.output.WriteLine(FormattableString.Invariant(
        $"RESULT: sheets={sheets.Count} placed={sheets.Sum(s => s.PartPlacements.Count)} perSheet={sheets.Min(s => s.PartPlacements.Count)}..{sheets.Max(s => s.PartPlacements.Count)} unplaced={result.UnplacedParts.Count()} edges={edges.Count} isolated={isolated} shared={shared:F4} lengths={string.Join("+", sheets.Select(s => SparrowNestService.Extent(s.PartPlacements.ToList()).W.ToString("F3", CultureInfo.InvariantCulture)))} hash={Hash(sheets)}"));
      this.output.WriteLine(FormattableString.Invariant($"LOAD: ms={SparrowNestService.DiagLoadMs} deferred={SparrowNestService.DiagDeferred}"));
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

    /// <summary>The offcut a bench leaves out of the material usage: the app's setting named in
    /// <paramref name="variable"/> (end, side, both or auto) with its default sizes, or null (off) when unset.</summary>
    internal static OffcutOptions BenchOffcut(string variable, bool unitsMm)
    {
      if (!Enum.TryParse(Environment.GetEnvironmentVariable(variable), true, out OffcutDirection direction))
      {
        return null;
      }

      var (limits, spacing, edgeOverlap) = OffcutOptions.Defaults(unitsMm);
      return new OffcutOptions
      {
        Direction = direction,
        Spacing = spacing,
        MinX = limits.MinX,
        MinY = limits.MinY,
        MaxX = limits.MaxX,
        MaxY = limits.MaxY,
        EdgeOverlap = edgeOverlap,
      };
    }

    /// <summary>Every placement and every shared cut, so two runs that differ anywhere differ here.</summary>
    internal static string Hash(IEnumerable<ISheetPlacement> sheets)
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
