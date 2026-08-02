namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using System.Text;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// "I ran the exact same nest three times and each run produced a different offcut size" (issue #2).
  /// sparrow only stops on WALL CLOCK (-t; there is no iteration limit in its CLI), so a fixed RNG seed
  /// does NOT make a single try repeatable — how far the search gets depends on machine load. What we
  /// control is the best-of-K choice on top, so these measurements ask two questions:
  ///   1. how wide is the run-to-run spread of the OUTCOME the operator sees (the pack extent)?
  ///   2. is sparrow's own density — what PickBest ranks by — the same winner the extent would pick?
  /// Opt-in: SHEETNEST_PERF=1, SPARROW_EXE, and SHEETNEST_PERF_DXF pointing at a part file.
  /// </summary>
  public class NestConsistencyFixture
  {
    private readonly ITestOutputHelper output;

    public NestConsistencyFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Fact]
    public void MeasureRunToRunSpread()
    {
      string exe = SparrowExe.Resolve();
      string dxf = Environment.GetEnvironmentVariable("SHEETNEST_PERF_DXF");
      if (Environment.GetEnvironmentVariable("SHEETNEST_PERF") != "1"
          || string.IsNullOrWhiteSpace(exe) || !System.IO.File.Exists(exe)
          || string.IsNullOrWhiteSpace(dxf) || !System.IO.File.Exists(dxf))
      {
        return;
      }

      // A second part type is what the report is actually about ("8 of the first and 7 of the second fit,
      // but 8 and 8 sometimes spills two parts onto sheet two") — a single repeated part is a much easier
      // packing problem and hides the variance.
      string dxf2 = Environment.GetEnvironmentVariable("SHEETNEST_PERF_DXF2");
      int qty = ReadInt("SHEETNEST_PERF_QTY", 25);
      int qty2 = ReadInt("SHEETNEST_PERF_QTY2", 0);
      int sheetW = ReadInt("SHEETNEST_PERF_W", 60);
      int sheetH = ReadInt("SHEETNEST_PERF_H", 120);
      int sheets = ReadInt("SHEETNEST_PERF_SHEETS", 5);
      int runs = ReadInt("SHEETNEST_PERF_RUNS", 5);
      double spacing = ReadDouble("SHEETNEST_PERF_SPACING", 0.25);

      var runLog = new StringBuilder(
        FormattableString.Invariant(
          $"# qty={qty} qty2={qty2} sheet={sheetW}x{sheetH}x{sheets} spacing={spacing} runs={runs}\n")
        + "run,sheets,placed,lastExtentX,lastExtentY,perSheet,hash\n");
      var candLog = new StringBuilder("run,pack,seed,count,density,extentX,extentY\n");
      var extents = new List<double>();
      var hashes = new List<string>();

      for (int r = 1; r <= runs; r++)
      {
        var parts = new List<RasterPartInfo> { new RasterPartInfo { Path = dxf, Quantity = qty } };
        if (!string.IsNullOrWhiteSpace(dxf2) && System.IO.File.Exists(dxf2) && qty2 > 0)
        {
          parts.Add(new RasterPartInfo { Path = dxf2, Quantity = qty2 });
        }

        var stock = new List<(int, int, int)> { (sheetW, sheetH, sheets) };
        SparrowNestService.DiagCandidates.Clear();

        var result = SparrowNestService.Nest(parts, stock, 36, spacing, 0.5, 8, exe, out _);

        // The offcut the operator measures is on the LAST sheet — the partial one. Per-sheet counts go in
        // too, because "two parts on a second sheet when only one was needed" is a per-sheet symptom.
        var used = result?.UsedSheets.ToList();
        int placed = used?.Sum(s => s.PartPlacements.Count) ?? -1;
        var last = used != null && used.Count > 0 ? used[used.Count - 1].PartPlacements.ToList() : null;
        var (ex, ey) = SparrowNestService.Extent(last);
        extents.Add(ey);
        string perSheet = used == null ? string.Empty : string.Join("|", used.Select(s => s.PartPlacements.Count));
        string hash = PlacementHash(result);
        hashes.Add(hash);

        runLog.AppendLine(FormattableString.Invariant(
          $"{r},{used?.Count ?? -1},{placed},{ex:F3},{ey:F3},{perSheet},{hash}"));
        foreach (string c in SparrowNestService.DiagCandidates)
        {
          candLog.AppendLine(FormattableString.Invariant($"{r},{c}"));
        }
      }

      runLog.AppendLine(FormattableString.Invariant(
        $"# extentY spread: min {extents.Min():F3}, max {extents.Max():F3}, delta {extents.Max() - extents.Min():F3}"));
      runLog.AppendLine(FormattableString.Invariant(
        $"# distinct nests across {runs} runs: {hashes.Distinct().Count()} (1 = deterministic)"));

      this.Emit(runLog.ToString() + "\n" + candLog.ToString(), ".consistency.csv");

      hashes.Distinct().Should().HaveCount(1, "the same job must produce the same nest on every run");
    }

    /// <summary>A canonical fingerprint of a whole nest: every placement's source, position, rotation and
    /// mirror, sheet by sheet, sorted so that only the geometry can change it — not the order the engine
    /// happened to emit it in.</summary>
    private static string PlacementHash(INestResult result)
    {
      if (result == null)
      {
        return "null";
      }

      var sb = new StringBuilder();
      foreach (var sheet in result.UsedSheets)
      {
        var rows = sheet.PartPlacements
          .Select(p => FormattableString.Invariant($"{p.Source}|{p.X:F6}|{p.Y:F6}|{p.Rotation:F6}|{p.IsMirrored}"))
          .OrderBy(s => s, StringComparer.Ordinal);
        sb.Append(string.Join(";", rows)).Append('#');
      }

      using var sha = System.Security.Cryptography.SHA256.Create();
      return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Substring(0, 16);
    }

    private static int ReadInt(string name, int fallback)
    {
      return int.TryParse(
        Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
        ? v
        : fallback;
    }

    private static double ReadDouble(string name, double fallback)
    {
      return double.TryParse(
        Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
        ? v
        : fallback;
    }

    private void Emit(string csv, string suffix)
    {
      this.output.WriteLine(csv);
      string outPath = Environment.GetEnvironmentVariable("SHEETNEST_PERF_OUT");
      if (!string.IsNullOrEmpty(outPath))
      {
        System.IO.File.WriteAllText(outPath + suffix, csv);
      }
    }
  }
}
