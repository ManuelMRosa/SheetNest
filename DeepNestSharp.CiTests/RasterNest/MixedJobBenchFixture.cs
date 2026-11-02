namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// MEASUREMENT, not a promise: jobs of many different parts, nested the way the app nests them, with every
  /// figure a mixed job is judged by printed on one line. The parts are invented, the same shapes as the public
  /// demo pictures plus a frame with a big window and a small square that fits in it.
  /// </summary>
  /// <remarks>
  /// <para>Opt-in: set SHEETNEST_MIXED_BENCH=1 and SPARROW_EXE; SHEETNEST_MIXED_BENCH_JOBS="A,C" picks jobs. The job is nested for up to
  /// SHEETNEST_MIXED_BENCH_TIME seconds (15 by default, as the app), or at one effort when
  /// SHEETNEST_MIXED_BENCH_EFFORT is Fast, Normal or Best. Every job runs on unlimited 120 x 60 in sheets with a
  /// quarter inch of spacing and border and four-way rotation. SHEETNEST_MIXED_BENCH_TARGET=percent and
  /// SHEETNEST_MIXED_BENCH_OFFCUT=end|side|both|auto hand the nest over on the material usage target.</para>
  /// <para>A: the demo, 57 parts without common line, one sheet's worth. B: the demo with common line on the panels
  /// and plates. C: four times A, so several sheets. D: twelve frames and ninety six squares, which fit inside the
  /// frames' windows. E: three times B. F: another mix of several sheets, with panels.</para>
  /// </remarks>
  [Collection(SparrowEngineCollection.Name)]
  public sealed class MixedJobBenchFixture : IDisposable
  {
    private const double Spacing = 0.25D;
    private const int SheetW = 120;
    private const int SheetH = 60;
    private readonly ITestOutputHelper output;
    private readonly string directory;

    public MixedJobBenchFixture(ITestOutputHelper output)
    {
      this.output = output;
      this.directory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.directory);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.directory, true);
      }
      catch (IOException)
      {
        // Test cleanup must not hide the actual result.
      }
    }

    [MixedBenchTheory]
    [InlineData("A")]
    [InlineData("B")]
    [InlineData("C")]
    [InlineData("D")]
    [InlineData("E")]
    [InlineData("F")]
    public void MeasureAMixedJob(string job)
    {
      // SHEETNEST_MIXED_BENCH_JOBS="A,C" measures only those.
      string only = Environment.GetEnvironmentVariable("SHEETNEST_MIXED_BENCH_JOBS");
      if (!string.IsNullOrWhiteSpace(only) && !only.Split(',', StringSplitOptions.TrimEntries).Contains(job))
      {
        return;
      }

      var parts = this.Job(job);
      bool fixedEffort = Enum.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_MIXED_BENCH_EFFORT"), true, out NestEffort effort);
      int seconds = int.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_MIXED_BENCH_TIME"), out int s) && s > 0 ? s : NestRounds.DefaultSeconds;
      double target = int.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_MIXED_BENCH_TARGET"), out int percent) && percent > 0 ? percent / 100.0 : 0;
      var offcut = LargeCommonLineJobBenchFixture.BenchOffcut("SHEETNEST_MIXED_BENCH_OFFCUT", false);
      var stock = new[] { (SheetW, SheetH, 1, true) };

      // SHEETNEST_MIXED_BENCH_SECONDS_PER_SHEET nests with the app's time per sheet, and wins over _TIME.
      double perSheet = double.TryParse(Environment.GetEnvironmentVariable("SHEETNEST_MIXED_BENCH_SECONDS_PER_SHEET"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ps) && ps > 0 ? ps : 0;
      TimeSpan limit = TimeSpan.FromSeconds(seconds);
      string mode = fixedEffort
        ? effort.ToString()
        : (perSheet > 0 ? FormattableString.Invariant($"perSheet{perSheet:0.###}") : FormattableString.Invariant($"timed{seconds}"))
          + (target > 0 ? FormattableString.Invariant($"-target{percent}") : string.Empty);
      NestDiagnostics.Totals.Clear();
      long sparrowBefore = SparrowNestService.DiagSparrowMs;
      long mapBefore = SparrowNestService.DiagMapMs;
      var clock = Stopwatch.StartNew();
      INestResult result;
      string error;
      NestRoundsReport report = null;
      if (fixedEffort)
      {
        result = SparrowNestService.Nest(parts, stock, 4, Spacing, 0.25D, 5, SparrowExe.Resolve(), out error, effort: effort, commonLineGap: 0.010D, geometryTolerance: 0.0001D);
      }
      else
      {
        long roundStart = 0;
        result = SparrowNestService.NestForTime(
          parts,
          stock,
          4,
          Spacing,
          0.25D,
          5,
          SparrowExe.Resolve(),
          limit,
          out error,
          out report,
          roundStarted: _ => roundStart = clock.ElapsedMilliseconds,
          commonLineGap: 0.010D,
          geometryTolerance: 0.0001D,
          roundFinished: (round, finished) => this.output.WriteLine(FormattableString.Invariant(
            $"ROUND {job} {round} ms={clock.ElapsedMilliseconds - roundStart} placed={finished.TotalPlacedCount} usage={NestRounds.UsedUtilization(finished, offcut) * 100:F2}% hash={LargeCommonLineJobBenchFixture.Hash(finished.UsedSheets)}")),
          materialTarget: target,
          offcut: offcut,
          secondsPerSheet: perSheet,
          limit: known => limit = known);
      }

      clock.Stop();
      Assert.True(result != null, error);
      var sheets = result.UsedSheets.ToList();
      var sharing = new HashSet<int>(parts.Select((p, i) => (p, i)).Where(x => x.p.RequiresMergedLines && x.p.Cc != CommonCuttingMode.None).Select(x => x.i));
      var edges = sheets.SelectMany(sh => sh.CommonEdges ?? Array.Empty<CommonEdge>()).ToList();
      double shared = edges.Sum(e => Math.Sqrt(((e.End.X - e.Start.X) * (e.End.X - e.Start.X)) + ((e.End.Y - e.Start.Y) * (e.End.Y - e.Start.Y))));
      int unshared = sheets.Sum(sh =>
      {
        var welded = new HashSet<int>((sh.CommonEdges ?? Array.Empty<CommonEdge>()).SelectMany(e => new[] { e.PartAId, e.PartBId }));
        return sh.PartPlacements.Count(p => sharing.Contains(p.Source) && !welded.Contains(p.Id));
      });
      var lengths = sheets.Select(HodgsonRowPatternJobFixture.Length).ToList();
      double partArea = sheets.Sum(sh => Math.Abs(sh.TotalPartsArea));

      // The last sheet is charged only as far as its parts reach: what is left beyond them is a remnant.
      double usedArea = ((sheets.Count - 1) * (double)SheetW * SheetH) + (lengths.Last() * SheetH);
      this.output.WriteLine(FormattableString.Invariant(
        $"MIXED {job} {mode}: wallMs={clock.ElapsedMilliseconds} limitMs={(fixedEffort || limit == NestRounds.NoLimit ? -1 : (long)limit.TotalMilliseconds)} best={report?.BestRound} finished={report?.FinishedRounds} stop={report?.StopReason} sheets={sheets.Count} placed={sheets.Sum(sh => sh.PartPlacements.Count)}/{parts.Sum(p => p.Quantity)} unshared={unshared} lengths={string.Join("+", lengths.Select(l => l.ToString("F3", CultureInfo.InvariantCulture)))} util={partArea / (sheets.Count * (double)SheetW * SheetH):P2} utilUsed={partArea / usedArea:P2} usage={NestRounds.UsedUtilization(result, offcut) * 100:F2}% edges={edges.Count} shared={shared:F3} unsafe={sheets.Sum(HodgsonRowPatternJobFixture.UnsafePairs)} hash={LargeCommonLineJobBenchFixture.Hash(sheets)}"));

      // Summed over the searches raced side by side, so work, not wall clock.
      this.output.WriteLine(FormattableString.Invariant(
        $"WORK {job}: sparrowSum={SparrowNestService.DiagSparrowMs - sparrowBefore}ms mapSum={SparrowNestService.DiagMapMs - mapBefore}ms {string.Join(" ", NestDiagnostics.Totals.Where(t => t.Key.EndsWith(".ms", StringComparison.Ordinal)).OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}"))}"));
    }

    private static double Bulge(double sweepDegrees) => Math.Tan(sweepDegrees * Math.PI / 180D / 4D);

    private static (double X, double Y) Polar(double r, double degrees)
      => (r * Math.Cos(degrees * Math.PI / 180D), r * Math.Sin(degrees * Math.PI / 180D));

    private static DxfLwPolyline Outline(params (double X, double Y, double Bulge)[] points)
      => new(points.Select(p => new DxfLwPolylineVertex { X = p.X, Y = p.Y, Bulge = p.Bulge })) { IsClosed = true };

    private static DxfCircle Hole(double x, double y, double diameter) => new(new DxfPoint(x, y, 0D), diameter / 2D);

    private RasterPartInfo[] Job(string job) => job switch
    {
      "A" => new[] { this.Part("arm", 8), this.Part("plate", 4), this.Part("lever", 10), this.Part("ring", 5), this.Part("clip", 14), this.Part("hub", 16) },
      "B" => new[] { this.Part("panel", 16, true), this.Part("plate", 4, true), this.Part("ring", 3), this.Part("arm", 4), this.Part("hub", 12), this.Part("lever", 8) },
      "C" => new[] { this.Part("arm", 32), this.Part("plate", 16), this.Part("lever", 40), this.Part("ring", 20), this.Part("clip", 56), this.Part("hub", 64) },
      "D" => new[] { this.Part("frame", 12), this.Part("square", 96) },
      "E" => new[] { this.Part("panel", 48, true), this.Part("plate", 12, true), this.Part("ring", 9), this.Part("arm", 12), this.Part("hub", 36), this.Part("lever", 24) },
      "F" => new[] { this.Part("panel", 30), this.Part("plate", 30), this.Part("arm", 20), this.Part("lever", 20), this.Part("ring", 10), this.Part("clip", 40), this.Part("hub", 40) },
      _ => throw new ArgumentOutOfRangeException(nameof(job)),
    };

    /// <summary>What MainWindow.OnNest hands the engine for a part with common cutting on, or off.</summary>
    private RasterPartInfo Part(string name, int quantity, bool commonLine = false) => new()
    {
      Path = this.Write(name),
      Quantity = quantity,
      Rotations = 4,
      Spacing = Spacing,
      Cc = commonLine ? CommonCuttingMode.Unrestricted : CommonCuttingMode.None,
      CommonCuttingDomain = commonLine ? "direct" : string.Empty,
      RequiresMergedLines = commonLine,
    };

    private string Write(string name)
    {
      string path = Path.Combine(this.directory, name + ".dxf");
      if (File.Exists(path))
      {
        return path;
      }

      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2000;
      foreach (DxfEntity entity in Entities(name))
      {
        dxf.Entities.Add(entity);
      }

      dxf.Save(path, true);
      return path;
    }

    private static IEnumerable<DxfEntity> Entities(string name)
    {
      switch (name)
      {
        case "arm":
          {
            // A 140 degree band between radii 14 and 10.5 with eleven holes along its middle.
            var (a, b) = Polar(14, 20);
            var (c, d) = Polar(14, 160);
            var (e, f) = Polar(10.5, 160);
            var (g, h) = Polar(10.5, 20);
            yield return Outline((a, b, Bulge(140)), (c, d, 0), (e, f, Bulge(-140)), (g, h, 0));
            for (int angle = 27; angle < 160; angle += 13)
            {
              var (x, y) = Polar(12.25, angle);
              yield return Hole(x, y, 0.75);
            }

            break;
          }

        case "plate":
          yield return Outline((0.5, 0, 0), (12.5, 0, Bulge(90)), (13, 0.5, 0), (13, 17.5, Bulge(90)), (12.5, 18, 0), (0.5, 18, Bulge(90)), (0, 17.5, 0), (0, 0.5, Bulge(90)));
          break;
        case "lever":
          yield return Outline((0, 1, 0), (6, 1, 0), (7, 0, 0), (10, 0, 0), (10, 1.2, 0), (8.2, 1.2, 0), (8.2, 2.3, 0), (10, 2.3, 0), (10, 3.5, 0), (7, 3.5, 0), (6, 2.5, 0), (0, 2.5, 0));
          break;
        case "ring":
          {
            // A ring of radii 10 and 4.5 with a 3 in gap cut through it at the bottom.
            double yo = -Math.Sqrt((10 * 10) - (1.5 * 1.5));
            double yi = -Math.Sqrt((4.5 * 4.5) - (1.5 * 1.5));
            double outer = 360 - (2 * Math.Asin(1.5 / 10) * 180 / Math.PI);
            double inner = 360 - (2 * Math.Asin(1.5 / 4.5) * 180 / Math.PI);
            yield return Outline((1.5, yo, Bulge(outer)), (-1.5, yo, 0), (-1.5, yi, Bulge(-inner)), (1.5, yi, 0));
            break;
          }

        case "clip":
          yield return Outline((1.5, 0, 0), (8, 0, Bulge(180)), (8, 3, 0), (1.5, 3, Bulge(180)));
          yield return Hole(8, 1.5, 1.4);
          break;
        case "hub":
          yield return new DxfCircle(new DxfPoint(0, 0, 0), 3.5);
          yield return Hole(0, 0, 1);
          for (int i = 0; i < 5; i++)
          {
            var (x, y) = Polar(2, 90 + (i * 72));
            yield return Hole(x, y, 1.3);
          }

          break;
        case "panel":
          yield return Outline((0, 0, 0), (10, 0, 0), (10, 6, 0), (0, 6, 0));
          yield return Hole(1.5, 3, 1);
          yield return Hole(8.5, 3, 1);
          break;
        case "frame":
          // 24 x 16 with a 20 x 12 window: eight of the squares below fit in it at the spacing.
          yield return Outline((0, 0, 0), (24, 0, 0), (24, 16, 0), (0, 16, 0));
          yield return Outline((2, 2, 0), (22, 2, 0), (22, 14, 0), (2, 14, 0));
          break;
        case "square":
          yield return Outline((0, 0, 0), (4, 0, 0), (4, 4, 0), (0, 4, 0));
          break;
        default:
          throw new ArgumentOutOfRangeException(nameof(name));
      }
    }

    internal sealed class MixedBenchTheoryAttribute : TheoryAttribute
    {
      public MixedBenchTheoryAttribute()
      {
        if (Environment.GetEnvironmentVariable("SHEETNEST_MIXED_BENCH") != "1")
        {
          this.Skip = "Set SHEETNEST_MIXED_BENCH=1 and SPARROW_EXE to measure the mixed jobs.";
        }
      }
    }
  }
}
