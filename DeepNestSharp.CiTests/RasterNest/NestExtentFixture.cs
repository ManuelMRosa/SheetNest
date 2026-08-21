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
  /// What the nest actually leaves the operator: how WIDE a block the parts end up in, and therefore how much
  /// of the sheet comes back as one usable piece.
  /// </summary>
  /// <remarks>
  /// <para>Nothing measured this before. Every engine test counted parts placed or read the engine's own
  /// density, and both of those saturate within a handful of iterations while the pack is still far too wide.
  /// That blind spot is how a search budget of 15 iterations survived as "converged" and how a user came to
  /// report that his leftover material was scattered instead of being one strip he could put back on the rack.</para>
  /// <para>The bar is a real job: fourteen parts on 2650 x 2070 with 9 mm spacing and a 15 mm border, which
  /// another nester packs into a block 2247.7 mm wide, leaving a 387 mm strip at full height. That job's files
  /// belong to the person who sent them and are not in this repo, so the guard below runs on the repo's own
  /// drawings shaped into the same kind of problem: a demand that fits with room to spare, on a sheet wider
  /// than it is tall.</para>
  /// </remarks>
  public class NestExtentFixture
  {
    /// <summary>The proportion of the sheet the parts take up, matched to the reported job (60.5%), so the
    /// problem is the same shape: comfortably feasible, and won by packing tight rather than by fitting at all.</summary>
    private const double AreaFill = 0.605;

    /// <summary>Sheet aspect, also from the reported job (2650 / 2070).</summary>
    private const double SheetAspect = 2650.0 / 2070.0;

    private readonly ITestOutputHelper output;

    public NestExtentFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    /// <summary>
    /// THE GUARD: the nest has to leave a strip of the sheet free, in one piece, at the end it packs away
    /// from. That is the whole complaint behind this work, and nothing else in the suite would notice it.
    /// </summary>
    /// <remarks>
    /// It asserts on the STRIP and not on how densely the block is filled, because the fill does not
    /// discriminate. Measured on this instance: at the budget that shipped the parts sprawled to within
    /// 1 mm of the far edge and STILL read 74.7% filled, comfortably over any fill bar worth setting, while
    /// the free strip went from 22 mm to 1 mm. Sabotage check before trusting this test: run it with
    /// SHEETNEST_NEST_ITERS=15 and watch it go red.
    /// </remarks>
    [Fact]
    public void ANormalEffortNestLeavesAUsableStrip()
    {
      var m = Measure(NestEffort.Normal);
      if (m == null)
      {
        return;
      }

      this.output.WriteLine(m.Value.Line);

      m.Value.Placed.Should().Be(m.Value.Total, "every part has to be on the sheet before tightness means anything");
      (m.Value.Strip / m.Value.SheetW).Should().BeGreaterOrEqualTo(
        0.05,
        "the parts must pack tightly enough to leave at least a twentieth of the sheet free in one strip; "
        + "anything less comes back to the operator as scrap rather than stock");
    }

    /// <summary>
    /// MEASUREMENT, not a promise: what each effort costs and buys. Set SHEETNEST_BENCH=1 to run it. It is a
    /// bench, so it is opt-in, and it takes its budget BY PARAMETER rather than through the environment: a
    /// process-wide SHEETNEST_NEST_ITERS once leaked into the rest of the suite and left it running for fifty
    /// minutes.
    /// </summary>
    [Fact]
    public void MeasureWhatEachEffortBuys()
    {
      if (Environment.GetEnvironmentVariable("SHEETNEST_BENCH") != "1")
      {
        this.output.WriteLine("BENCH: set SHEETNEST_BENCH=1 to run.");
        return;
      }

      this.output.WriteLine(string.Format(
        "{0,-8} {1,-8} {2,10} {3,10} {4,10} {5,9} {6,8}", "effort", "placed", "extentX", "extentY", "strip", "fill", "sec"));
      foreach (int rot in new[] { 4, 8, 36 })
      {
        foreach (var effort in new[] { NestEffort.Fast, NestEffort.Normal })
        {
          var m = Measure(effort, rot);
          if (m == null)
          {
            return;
          }

          this.output.WriteLine(m.Value.Line);
        }
      }
    }

    /// <summary>Nests the bench job at one effort and reports the block the parts landed in. Null when the
    /// engine or the drawings are not on this machine.</summary>
    private static (string Line, int Placed, int Total, double Fill, double Strip, double SheetW)? Measure(NestEffort effort, int rotations = 4)
    {
      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        return null;
      }

      string dxfDir = FindDxfDir();
      if (dxfDir == null)
      {
        return null;
      }

      const int Copies = 7;
      var helper = new NestExecutionHelper();
      var parts = new List<RasterPartInfo>();
      double partArea = 0;
      double maxSide = 0;
      for (int i = 1; i <= 2; i++)
      {
        string path = Path.Combine(dxfDir, $"_{i}.dxf");
        if (File.Exists(path) && helper.LoadRawDetail(new FileInfo(path)) is { } det
            && det.TryConvertToNfp(0, out INfp nfp) && nfp.Points.Length > 2)
        {
          partArea += Math.Abs(nfp.NetArea) * Copies;
          maxSide = Math.Max(maxSide, Math.Max(nfp.MaxX - nfp.MinX, nfp.MaxY - nfp.MinY));
          parts.Add(new RasterPartInfo { Path = path, Quantity = Copies, Rotations = rotations, Cc = CommonCuttingMode.None });
        }
      }

      parts.Should().HaveCount(2);

      // Spacing and border in the same proportion to the parts as the reported job (9 mm and 15 mm against a
      // 936 mm part), so the clearances are not what makes this easier or harder than the real thing.
      double spacing = maxSide / 104.0;
      double margin = maxSide / 62.0;
      foreach (var p in parts)
      {
        p.Spacing = spacing;
      }

      double sheetArea = partArea / AreaFill;
      int sheetH = (int)Math.Round(Math.Sqrt(sheetArea / SheetAspect));
      int sheetW = (int)Math.Round(sheetH * SheetAspect);

      var watch = System.Diagnostics.Stopwatch.StartNew();
      var result = SparrowNestService.Nest(
        parts,
        new List<(int, int, int)> { (sheetW, sheetH, 1) },
        rotations,
        spacing,
        margin,
        5,
        exe,
        out string err,
        default,
        null,
        null,
        effort);
      watch.Stop();

      result.Should().NotBeNull($"nest must return a result (err={err})");

      var placements = result.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
      placements.Should().NotBeEmpty();

      var (ex, ey) = SparrowNestService.Extent(placements);
      double placedArea = placements.Sum(p => Math.Abs(p.PlacedPart.Area));
      double fill = ex > 0 && ey > 0 ? placedArea / (ex * ey) : 0;
      double strip = sheetW - margin - (placements.Max(p => p.PlacedPart.MaxX));

      string line = FormattableString.Invariant(
        $"rot{rotations,-3} {effort,-8} {placements.Count,-8} {ex,10:F0} {ey,10:F0} {strip,10:F0} {fill,9:P1} {watch.Elapsed.TotalSeconds,8:F1}");
      return (line, placements.Count, Copies * 2, fill, strip, sheetW);
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
