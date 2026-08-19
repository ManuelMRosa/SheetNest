namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// Measurement harness, not a pass/fail test: runs a mixed basket over MIXED STOCK and reports what the
  /// job actually cost in material and wall time. It exists because choosing the sheet size changes the
  /// answer for every job with more than one size, and "it should pack better" is not a number.
  /// <para>Deliberately uses the counted (three-field) stock overload so the same file compiles against a
  /// build without the unlimited flag: that is what makes an A/B against the previous engine possible.</para>
  /// <para>Set SPARROW_EXE to run; otherwise no-ops.</para>
  /// </summary>
  public class MixedStockUtilizationFixture
  {
    private readonly ITestOutputHelper output;

    public MixedStockUtilizationFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Fact]
    public void ReportMaterialUsedOnMixedStock()
    {
      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("MIX: SPARROW_EXE not set — skipping.");
        return;
      }

      string dxfDir = FindDxfDir();
      dxfDir.Should().NotBeNull();

      // The mixed basket the benchmark already uses elsewhere: six distinct parts, two of each.
      string[] names = { "_2.dxf", "_4.dxf", "_6.dxf", "_7.dxf", "_8.dxf", "_9.dxf" };
      var helper = new NestExecutionHelper();
      var parts = new List<RasterPartInfo>();
      double partArea = 0;
      double biggest = 0;

      foreach (string name in names)
      {
        string path = Path.Combine(dxfDir, name);
        File.Exists(path).Should().BeTrue($"{name} must be there for the measurement to mean anything");
        helper.LoadRawDetail(new FileInfo(path)).TryConvertToNfp(0, out INfp nfp).Should().BeTrue();

        parts.Add(new RasterPartInfo { Path = path, Quantity = 2 });
        partArea += 2 * Math.Abs(nfp.Area);
        biggest = Math.Max(biggest, Math.Max(nfp.MaxX - nfp.MinX, nfp.MaxY - nfp.MinY));
      }

      // A full sheet and an offcut. The SAME job is run with the stock listed both ways round, because an
      // engine that walks the list in order gets the ordering it is handed for free: with the good size
      // first it looks identical to one that chooses, and only the reversed listing tells them apart.
      int unit = (int)Math.Ceiling(biggest);
      int offcut = unit * 2;
      int full = unit * 3;
      this.output.WriteLine(FormattableString.Invariant($"MIX: offcut={offcut} full={full}"));

      this.RunOne("offcut-first", parts, new List<(int, int, int)> { (offcut, offcut, 4), (full, full, 4) }, partArea, exe);
      this.RunOne("full-first", parts, new List<(int, int, int)> { (full, full, 4), (offcut, offcut, 4) }, partArea, exe);
    }

    private void RunOne(string label, List<RasterPartInfo> parts, List<(int, int, int)> stock, double partArea, string exe)
    {
      var watch = System.Diagnostics.Stopwatch.StartNew();
      var result = SparrowNestService.Nest(parts, stock, 4, 1.0, 0.5, 5, exe, out string err);
      watch.Stop();

      result.Should().NotBeNull($"the mixed-stock nest must return a result (err={err})");

      double sheetArea = result.UsedSheets.Sum(s => s.Sheet.WidthCalculated * s.Sheet.HeightCalculated);
      int placed = result.UsedSheets.Sum(s => s.PartPlacements.Count);
      var bySize = result.UsedSheets
        .GroupBy(s => $"{Math.Round(s.Sheet.WidthCalculated)}x{Math.Round(s.Sheet.HeightCalculated)}")
        .Select(g => $"{g.Key}×{g.Count()}");

      double utilization = sheetArea <= 0 ? 0 : partArea / sheetArea * 100;
      string sheets = string.Join(" ", bySize);
      int unplaced = result.UnplacedParts.Count();
      long ms = watch.ElapsedMilliseconds;

      this.output.WriteLine(FormattableString.Invariant(
        $"MIX[{label}]: sheets=[{sheets}] placed={placed}/12 unplaced={unplaced} sheetArea={sheetArea:F0} utilization={utilization:F1}% ms={ms}"));
    }

    private static string FindDxfDir()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null)
      {
        string candidate = Path.Combine(dir.FullName, "DeepNestPort", "dxfs");
        if (Directory.Exists(candidate))
        {
          return candidate;
        }

        dir = dir.Parent;
      }

      return null;
    }
  }
}
