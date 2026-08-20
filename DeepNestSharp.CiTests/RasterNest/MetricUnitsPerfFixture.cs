namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
  using System.Linq;
  using System.Text;
  using DeepNestLib;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// A job drawn in millimetres must not cost more to nest than the SAME job drawn in inches.
  /// It used to cost ~12x more end-to-end (a user reported a 14-part single sheet taking 20 minutes),
  /// because <c>ClipperOffset</c>'s default ArcTolerance is an ABSOLUTE 0.25 integer units: the point
  /// count of a round join is pi/acos(1 - tolerance/delta), so it grows with the offset measured in
  /// integer units. At the fixed 1e6 scale a 9 mm spacing offsets by 4.5e6 units against 1.77e5 for the
  /// same 0.354", which resampled every clearance shell into ~10k vertices and made each collision test
  /// ~13x dearer. The deep timings behind this guard are opt-in via SHEETNEST_PERF=1.
  /// </summary>
  public class MetricUnitsPerfFixture
  {
    private readonly ITestOutputHelper output;

    public MetricUnitsPerfFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Fact]
    public void CompactionCostsTheSameInMillimetresAsInInches()
    {
      Compact(1.0);  // warm up the JIT so the first measured run isn't paying for it
      long inMs = Compact(1.0 / 25.4);
      long mmMs = Compact(1.0);

      this.output.WriteLine(FormattableString.Invariant($"compaction: mm {mmMs} ms, in {inMs} ms"));

      // Generous bound — this catches an order-of-magnitude unit penalty (it was 14x), not jitter.
      mmMs.Should().BeLessThan(
        Math.Max(1500, inMs * 5),
        "a metric job must not cost an order of magnitude more to compact than the same job in inches");
    }

    /// <summary>Compacts the reference sheet (14 wavy holed parts, 2070x287, spacing 9) in the given unit
    /// scale and returns the elapsed milliseconds.</summary>
    private static long Compact(double u, int vertices = 60)
    {
      var items = BuildSheet(vertices, u);
      var sw = Stopwatch.StartNew();
      RasterCompact.Compact(items, 2070 * u, 287 * u, 5 * u);
      sw.Stop();
      return sw.ElapsedMilliseconds;
    }

    [Fact]
    public void MeasureCompactionCostAcrossVertexCounts()
    {
      if (Environment.GetEnvironmentVariable("SHEETNEST_PERF") != "1")
      {
        return;
      }

      var log = new StringBuilder("units,vertices,ms\n");
      foreach (var (units, u) in new[] { ("mm", 1.0), ("in", 1.0 / 25.4) })
      {
        foreach (int v in new[] { 60, 300, 1200, 3000 })
        {
          log.AppendLine(FormattableString.Invariant($"{units},{v},{Compact(u, v)}"));
        }
      }

      this.Emit(log.ToString(), ".compact.csv");
    }

    /// <summary>
    /// End-to-end: the WHOLE nest (load, dilate, sparrow, map, compact, holes) on a job shaped like the
    /// one reported at 20 minutes — 2 part types x 7, one 2070x287 sheet, spacing 9, free rotation —
    /// run once in mm and once in inches. Opt-in: SHEETNEST_PERF=1 plus SPARROW_EXE.
    /// </summary>
    [Fact]
    public void MeasureFullNestCost()
    {
      string exe = SparrowExe.Resolve();
      if (Environment.GetEnvironmentVariable("SHEETNEST_PERF") != "1"
          || string.IsNullOrWhiteSpace(exe) || !System.IO.File.Exists(exe))
      {
        return;
      }

      var log = new StringBuilder("units,seconds,loadMs,sparrowMs,mapMs,holesMs,points,sheets,placed,unplaced\n");
      string dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "SheetNestPerf", Guid.NewGuid().ToString("N"));
      System.IO.Directory.CreateDirectory(dir);
      try
      {
        foreach (var (units, u) in new[] { ("mm", 1.0), ("in", 1.0 / 25.4) })
        {
          var parts = new List<RasterPartInfo>();
          for (int k = 0; k < 2; k++)
          {
            string path = System.IO.Path.Combine(dir, $"part{k}_{units}.dxf");
            WriteDxf(path, WavyPart((285 - (k * 45)) * u, 135 * u, 900));
            parts.Add(new RasterPartInfo { Path = path, Quantity = 7 });
          }

          var stock = new List<(int, int, int)>
          {
            ((int)Math.Round(2070 * u), (int)Math.Round(287 * u), 1),
          };

          // Where does a metric job actually spend the extra time? Splitting it settles whether the
          // residual gap is ours (loading, compaction) or the sparrow engine's own search.
          SparrowNestService.DiagLoadMs = 0;
          SparrowNestService.DiagSparrowMs = 0;
          SparrowNestService.DiagMapMs = 0;
          SparrowNestService.DiagHolesMs = 0;

          var sw = Stopwatch.StartNew();
          var result = SparrowNestService.Nest(parts, stock, 36, 9 * u, 5 * u, 8, exe, out _);
          sw.Stop();

          // Vertex count as the loader produced it — hypothesis A is that a metric drawing tessellates
          // its arcs finer, so the same part arrives with more points and everything downstream pays.
          int points = 0;
          var helper = new NestExecutionHelper();
          foreach (var p in parts)
          {
            if (helper.LoadRawDetail(new System.IO.FileInfo(p.Path)) is { } det
                && det.TryConvertToNfp(0, out INfp nfp))
            {
              points += nfp.Points.Length + (nfp.Children?.Sum(c => c.Points.Length) ?? 0);
            }
          }

          log.AppendLine(FormattableString.Invariant(
            $"{units},{sw.Elapsed.TotalSeconds:F1},{SparrowNestService.DiagLoadMs},{SparrowNestService.DiagSparrowMs},{SparrowNestService.DiagMapMs},{SparrowNestService.DiagHolesMs},{points},{result?.UsedSheets.Count ?? -1},{result?.UsedSheets.Sum(s => s.PartPlacements.Count) ?? -1},{result?.UnplacedParts.Count ?? -1}"));
        }
      }
      finally
      {
        try
        {
          System.IO.Directory.Delete(dir, true);
        }
        catch (System.IO.IOException)
        {
        }
      }

      this.Emit(log.ToString(), ".e2e.csv");
    }

    /// <summary>
    /// Does the loader hand back MORE geometry for a metric drawing? DxfParser's chord tolerance is
    /// 0.05 in DRAWING units, and the arc step is 2*acos(1 - 0.05/R) clamped to [1, 6] degrees — so the
    /// same physical part drawn in mm is tessellated against a tolerance 25x finer than in inches. Small
    /// radii clamp at 6 degrees either way and nothing changes; big ones do not, and then a metric part
    /// arrives with several times the vertices, which every later stage pays for. Opt-in: SHEETNEST_PERF=1.
    /// </summary>
    [Fact]
    public void MeasureTessellationAcrossUnits()
    {
      if (Environment.GetEnvironmentVariable("SHEETNEST_PERF") != "1")
      {
        return;
      }

      var log = new StringBuilder("part,radius,inchPoints,mmPoints,ratio\n");
      string dir = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "SheetNestTess", Guid.NewGuid().ToString("N"));
      System.IO.Directory.CreateDirectory(dir);
      try
      {
        var helper = new NestExecutionHelper();

        // A disc of the given radius, written as a real ARC-bearing DXF so the parser tessellates it —
        // once at an inch-sized radius and once at the same shape scaled to millimetres.
        foreach (double rIn in new[] { 0.5, 2.0, 8.0, 40.0 })
        {
          int Points(double radius)
          {
            string path = System.IO.Path.Combine(dir, $"disc_{radius:F3}.dxf");
            var file = new IxMilia.Dxf.DxfFile();
            file.Header.Version = IxMilia.Dxf.DxfAcadVersion.R2000;
            file.Entities.Add(new IxMilia.Dxf.Entities.DxfCircle(
              new IxMilia.Dxf.DxfPoint(radius, radius, 0), radius));
            file.Save(path);

            return helper.LoadRawDetail(new System.IO.FileInfo(path)) is { } det
              && det.TryConvertToNfp(0, out INfp nfp)
              ? nfp.Points.Length
              : -1;
          }

          int inPts = Points(rIn);
          int mmPts = Points(rIn * 25.4);
          log.AppendLine(FormattableString.Invariant(
            $"disc,{rIn}in={rIn * 25.4:F1}mm,{inPts},{mmPts},{(inPts > 0 ? mmPts / (double)inPts : 0):F2}"));
        }
      }
      finally
      {
        try
        {
          System.IO.Directory.Delete(dir, true);
        }
        catch (System.IO.IOException)
        {
        }
      }

      this.Emit(log.ToString(), ".tess.csv");
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

    /// <summary>14 wavy, holed parts laid out in two rows with slack between them, like a fresh pack.</summary>
    private static List<CompactItem> BuildSheet(int vertices, double u)
    {
      var poly = WavyPart(260 * u, 130 * u, vertices);
      var items = new List<CompactItem>();
      for (int i = 0; i < 14; i++)
      {
        items.Add(new CompactItem
        {
          Poly = poly,
          X = (5 + ((i % 7) * 290)) * u,
          Y = (5 + ((i / 7) * 140)) * u,
          Spacing = 9 * u,
        });
      }

      return items;
    }

    /// <summary>A concave closed outline sampled at `vertices` points, with an elliptical hole — stands in
    /// for a curved furniture part whose arcs tessellate into hundreds of segments.</summary>
    private static INfp WavyPart(double w, double h, int vertices)
    {
      int n = Math.Max(8, vertices);
      var pts = new List<SvgPoint>(n);
      for (int i = 0; i < n; i++)
      {
        double t = 2 * Math.PI * i / n;
        double r = 1 + (0.18 * Math.Sin(5 * t)) - (0.10 * Math.Cos(3 * t));
        pts.Add(new SvgPoint(
          w / 2 * (1 + (r * Math.Cos(t) / 1.3)),
          h / 2 * (1 + (r * Math.Sin(t) / 1.3))));
      }

      var poly = new NoFitPolygon(pts);

      int hn = Math.Max(8, vertices / 2);
      var hole = new List<SvgPoint>(hn);
      for (int i = 0; i < hn; i++)
      {
        double t = 2 * Math.PI * i / hn;
        hole.Add(new SvgPoint(
          (w / 2) + (w * 0.18 * Math.Cos(t)),
          (h / 2) + (h * 0.18 * Math.Sin(t))));
      }

      poly.Children.Add(new NoFitPolygon(hole));
      return poly;
    }

    /// <summary>
    /// Stage breakdown of a REAL job (SHEETNEST_PERF=1, SPARROW_EXE, and SHEETNEST_PERF_DXF pointing at a
    /// part file): load / sparrow / map+compact / holes, plus the wave and try counts, repeated a few
    /// times so run-to-run spread is visible. This is the measurement behind any change to the best-of-K
    /// wave policy — the wall clock of a single-sheet job is dominated by whole sparrow waves.
    /// </summary>
    [Fact]
    public void MeasureRealJobStageBreakdown()
    {
      string exe = SparrowExe.Resolve();
      string dxf = Environment.GetEnvironmentVariable("SHEETNEST_PERF_DXF");
      if (Environment.GetEnvironmentVariable("SHEETNEST_PERF") != "1"
          || string.IsNullOrWhiteSpace(exe) || !System.IO.File.Exists(exe)
          || string.IsNullOrWhiteSpace(dxf) || !System.IO.File.Exists(dxf))
      {
        return;
      }

      int qty = ReadInt("SHEETNEST_PERF_QTY", 26);
      int sheetW = ReadInt("SHEETNEST_PERF_W", 60);
      int sheetH = ReadInt("SHEETNEST_PERF_H", 120);
      int runs = ReadInt("SHEETNEST_PERF_RUNS", 3);
      double spacing = double.TryParse(
        Environment.GetEnvironmentVariable("SHEETNEST_PERF_SPACING"),
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out double sp)
        ? sp
        : 0.25;

      var log = new StringBuilder(
        FormattableString.Invariant($"# spacing={spacing} qty={qty} sheet={sheetW}x{sheetH}\n")
        + "run,totalSec,loadMs,sparrowMs,mapMs,holesMs,waves,tries,sheets,placed,utilPct\n");
      for (int r = 1; r <= runs; r++)
      {
        var parts = new List<RasterPartInfo> { new RasterPartInfo { Path = dxf, Quantity = qty } };
        var stock = new List<(int, int, int)> { (sheetW, sheetH, 1) };

        SparrowNestService.DiagLoadMs = 0;
        SparrowNestService.DiagSparrowMs = 0;
        SparrowNestService.DiagMapMs = 0;
        SparrowNestService.DiagHolesMs = 0;
        SparrowNestService.DiagWaves = 0;
        SparrowNestService.DiagTries = 0;

        var sw = Stopwatch.StartNew();
        var result = SparrowNestService.Nest(parts, stock, 36, spacing, 0.5, 8, exe, out _);
        sw.Stop();

        var placements = result?.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
        double used = placements?.Sum(p => Math.Abs(p.PlacedPart.Area)) ?? 0;
        log.AppendLine(FormattableString.Invariant(
          $"{r},{sw.Elapsed.TotalSeconds:F1},{SparrowNestService.DiagLoadMs},{SparrowNestService.DiagSparrowMs},{SparrowNestService.DiagMapMs},{SparrowNestService.DiagHolesMs},{SparrowNestService.DiagWaves},{SparrowNestService.DiagTries},{result?.UsedSheets.Count ?? -1},{placements?.Count ?? -1},{used / (sheetW * (double)sheetH) * 100:F1}"));
      }

      this.Emit(log.ToString(), ".realjob.csv");
    }

    private static int ReadInt(string name, int fallback)
    {
      return int.TryParse(
        Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
        ? v
        : fallback;
    }

    /// <summary>Writes an outline (and its hole) as closed LWPOLYLINEs so the real loader can read it.</summary>
    private static void WriteDxf(string path, INfp poly)
    {
      var file = new IxMilia.Dxf.DxfFile();
      file.Header.Version = IxMilia.Dxf.DxfAcadVersion.R2000; // LWPOLYLINE needs R13+ or it is dropped on save
      foreach (var contour in new[] { poly }.Concat(poly.Children))
      {
        file.Entities.Add(new IxMilia.Dxf.Entities.DxfLwPolyline(
          contour.Points.Select(p => new IxMilia.Dxf.Entities.DxfLwPolylineVertex { X = p.X, Y = p.Y }))
        {
          IsClosed = true,
        });
      }

      file.Save(path);
    }
  }
}
