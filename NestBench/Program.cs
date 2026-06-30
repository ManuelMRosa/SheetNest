namespace NestBench
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
  using DeepNestLib;
  using DeepNestLib.Geometry;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;

  /// <summary>
  /// Headless benchmark for the DeepNest engine: loads DXF parts, nests them onto a
  /// fixed stock for a fixed wall-clock budget, and reports material utilization.
  /// Purpose: a repeatable A/B yardstick to measure engine changes.
  /// </summary>
  internal static class Program
  {
    private static async Task<int> Main(string[] args)
    {
      var opt = Options.Parse(args);
      if (opt == null)
      {
        Options.PrintUsage();
        return 1;
      }

      // Parts: a generated rectangle (RectCount copies) and/or loaded DXFs (Copies each).
      // Each distinct part carries its own quantity so rectangles and irregulars can mix.
      var basis = new List<(INfp Part, int Count)>();
      int src = 0;
      if (opt.RectW > 0)
      {
        var rect = new NoFitPolygon();
        rect.AddPoint(new SvgPoint(0, 0));
        rect.AddPoint(new SvgPoint(opt.RectW, 0));
        rect.AddPoint(new SvgPoint(opt.RectW, opt.RectH));
        rect.AddPoint(new SvgPoint(0, opt.RectH));
        rect.Source = src++;
        basis.Add((rect, opt.RectCount));
      }

      if (opt.RectW == 0 || opt.Mix)
      {
        var dxfFiles = Directory.Exists(opt.DxfDir)
          ? Directory.GetFiles(opt.DxfDir, "*.dxf").OrderBy(f => f).Take(opt.MaxFiles).ToList()
          : new List<string>();
        if (dxfFiles.Count == 0 && opt.RectW == 0)
        {
          Console.Error.WriteLine($"No .dxf files in: {opt.DxfDir}");
          return 1;
        }

        foreach (var f in dxfFiles)
        {
          try
          {
            IRawDetail raw = DxfParser.LoadDxfFile(f).Result;
            if (raw != null && raw.TryConvertToNfp(src, out INfp nfp))
            {
              basis.Add((nfp, opt.Copies));
              src++;
            }
            else
            {
              Console.Error.WriteLine($"  skip (convert): {Path.GetFileName(f)}");
            }
          }
          catch (Exception ex)
          {
            Console.Error.WriteLine($"  skip ({ex.GetType().Name}): {Path.GetFileName(f)}");
          }
        }
      }

      if (basis.Count == 0)
      {
        Console.Error.WriteLine("No parts could be loaded.");
        return 1;
      }

      double sheetArea = (double)opt.SheetW * opt.SheetH;
      double demandArea = 0;
      int totalParts = basis.Sum(z => z.Count);
      Console.WriteLine("parts (distinct):");
      foreach (var (part, count) in basis)
      {
        var b = GeometryUtil.GetPolygonBounds(part);
        double area = Math.Abs(part.NetArea);
        demandArea += area * count;
        Console.WriteLine($"  src={part.Source,2}  bbox={b.Width,7:0.#} x {b.Height,-7:0.#}  netArea={area,11:N0}  x{count}");
      }

      double stockArea = sheetArea * opt.Sheets;
      Console.WriteLine($"demand={demandArea:N0}  stock={stockArea:N0}  "
        + $"(min sheets to fit area >= {Math.Ceiling(demandArea / sheetArea)};  util ceiling on {opt.Sheets} sheets = {Pct(Math.Min(1.0, demandArea / stockArea))})");
      Console.WriteLine(new string('-', 96));

      Console.WriteLine($"NestBench  distinctParts={basis.Count}  totalParts={totalParts}   "
        + $"stock={opt.SheetW}x{opt.SheetH} x{opt.Sheets}");
      Console.WriteLine($"config     placement={opt.Placement} rot={opt.Rotations} spacing={opt.Spacing} holes={opt.UseHoles} "
        + $"pop={opt.Population} mult={opt.Multiplier} dllImport={opt.UseDllImport}   budget={opt.Seconds:0.#}s reps={opt.Reps}");
      Console.WriteLine(new string('-', 96));

      var runs = new List<RunResult>();
      for (int rep = 1; rep <= opt.Reps; rep++)
      {
        var rr = await RunOnce(opt, basis).ConfigureAwait(false);
        runs.Add(rr);
        Console.WriteLine($"  rep {rep,2}:  density={Pct(rr.Density),7}  realU={Pct(rr.RealUtil),7}  box={rr.BoxW,5:0}x{rr.BoxH,-5:0}  placed={rr.Placed,3}/{rr.Total,-3}  "
          + $"sheets={rr.SheetsUsed,2}  gens={rr.Generations,3}  grid={rr.GridCount,3}  {rr.Elapsed,5:0.0}s");
      }

      Console.WriteLine(new string('-', 96));
      var best = runs.OrderByDescending(r => r.Density).First();
      double meanDensity = runs.Average(r => r.Density);
      double meanUtil = runs.Average(r => r.Utilization);
      Console.WriteLine($"  BEST  density={Pct(best.Density),7}  util={Pct(best.Utilization),7}  placed={best.Placed}/{best.Total}  sheets={best.SheetsUsed}");
      Console.WriteLine($"  MEAN  density={Pct(meanDensity),7}  util={Pct(meanUtil),7}   (n={runs.Count})");
      return 0;
    }

    private static async Task<RunResult> RunOnce(Options opt, List<(INfp Part, int Count)> basis)
    {
      // Deterministic seed => reproducible single-rep A/B. Force serial so thread
      // scheduling can't reintroduce nondeterminism.
      if (opt.Seed.HasValue)
      {
        DeepNestLib.GeneticAlgorithm.Procreant.RandomSeed = opt.Seed.Value;
      }

      DeepNestLib.GeneticAlgorithm.Procreant.SmartRotationSeed = opt.SmartRot;
      DeepNestLib.GeneticAlgorithm.Procreant.SmartRotationSteps = opt.SeedSteps;

      DeepNestLib.SvgNest.MaxNests = opt.MaxNests; // 0 = unbounded; >0 mimics the app's nests-per-run cap
      if (opt.CurveTol > 0) { DeepNestLib.SvgNest.Config.CurveTolerance = opt.CurveTol; } // match the app's 0.01
      DeepNestLib.Placement.Compaction.Enabled = opt.Compact;
      DeepNestLib.Placement.Reinsertion.Enabled = opt.Reinsert;
      DeepNestLib.Placement.CommonLine.Enabled = opt.CommonLine;
      DeepNestLib.Placement.CommonLine.Kerf = opt.Kerf;
      DeepNestLib.Placement.CommonLine.Spacing = opt.Spacing;
      DeepNestLib.Placement.GridNesting.Enabled = opt.Grid;
      DeepNestLib.Placement.GridNesting.Kerf = opt.Kerf;
      DeepNestLib.Placement.GridNesting.Spacing = opt.Spacing;
      DeepNestLib.Placement.GridNesting.MinGroupSize = opt.GridMin;

      var config = new TestSvgNestConfig
      {
        PlacementType = opt.Placement,
        Rotations = opt.Rotations,
        Spacing = opt.Spacing,
        UseHoles = opt.UseHoles,
        PopulationSize = opt.Population,
        Multiplier = opt.Multiplier,
        UseParallel = opt.Seed.HasValue ? false : opt.Parallel,
        UseDllImport = opt.UseDllImport,
        UseMinkowskiCache = true,
      };

      var progress = new SignalProgressDisplayer();
      var state = new NestState(config, new InlineDispatcherService());
      var ctx = new NestingContext(new NoOpMessageService(), progress, state, config);

      foreach (var (part, count) in basis)
      {
        for (int i = 0; i < count; i++)
        {
          ctx.Polygons.Add(part.Clone());
        }
      }

      for (int i = 0; i < opt.Sheets; i++)
      {
        var sheet = Sheet.NewSheet(i + 1, opt.SheetW, opt.SheetH);
        sheet.Source = 0; // single stock type
        ctx.Sheets.Add(sheet);
      }

      var sw = Stopwatch.StartNew();
      await ctx.StartNest().ConfigureAwait(false);

      int iter = 0;
      int stall = 0;
      int lastGen = -1;
      int lastCount = -1;
      while (sw.Elapsed.TotalSeconds < opt.Seconds && iter < opt.MaxIterations)
      {
        // One NestIterate evaluates a full generation (3 threads when parallel).
        await ctx.NestIterate(config).ConfigureAwait(false);
        iter++;

        // DeepNest stops itself once the GA can no longer diversify; detect the
        // resulting no-op spin and break instead of burning the time budget.
        int g = state.Generations;
        int c = state.TopNestResults?.Count ?? 0;
        if (g == lastGen && c == lastCount)
        {
          if (++stall > 200)
          {
            break;
          }
        }
        else
        {
          stall = 0;
          lastGen = g;
          lastCount = c;
        }
      }

      ctx.StopNest();
      sw.Stop();

      INestResult top = state.TopNestResults != null && state.TopNestResults.Count > 0
        ? state.TopNestResults.Top
        : null;

      if (top == null)
      {
        return new RunResult { Total = ctx.Polygons.Count, Elapsed = sw.Elapsed.TotalSeconds, Generations = state.Generations };
      }

      // Optional export smoke test: exercises the real DxfExporter path (incl. the LwPolyline cast).
      if (opt.ExportDir != null)
      {
        try
        {
          System.IO.Directory.CreateDirectory(opt.ExportDir);
          int s = 0;
          foreach (var sp in top.UsedSheets)
          {
            string path = System.IO.Path.Combine(opt.ExportDir, $"nest_sheet{s++}.dxf");
            using (var fs = System.IO.File.Create(path))
            {
              sp.ExportDxf(fs, false, false).GetAwaiter().GetResult();
            }

            Console.WriteLine($"  EXPORT OK: {path} ({new System.IO.FileInfo(path).Length} bytes)");
          }
        }
        catch (Exception ex)
        {
          Console.WriteLine($"  EXPORT FAILED: {ex.GetType().Name}: {ex.Message}");
        }
      }

      // Continuous packing-density metric: part area vs the bounding-box area the
      // placement actually occupies. Unlike MaterialUtilization (a step function of
      // sheet count) this moves with every compaction gain.
      // Optional one-time re-insertion polish of the winning nest (runs the GA fast, then
      // re-places each part against all others once — avoids starving the GA per-candidate).
      NfpHelper polishNfp = null;
      IPlacementWorker polishPw = null;
      if (opt.Polish)
      {
        polishNfp = new NfpHelper(MinkowskiSum.CreateInstance(config, state), new WindowUnk());
        polishPw = new StubPlacementWorker();
      }

      double boundingArea = 0;
      double boxW = 0;
      double boxH = 0;
      foreach (var sp in top.UsedSheets)
      {
        var pls = sp.PartPlacements.ToList();
        if (opt.Polish)
        {
          var gene = new DeepNestLib.GeneticAlgorithm.DeepNestGene(pls.Select(p => p.Part.ToChromosome()).ToList());
          Reinsertion.Optimize(pls, sp.Sheet, config, polishNfp, state, polishPw, gene);
        }

        double minX = pls.Min(p => p.MinX);
        double minY = pls.Min(p => p.MinY);
        boxW = pls.Max(p => p.MaxX) - minX;
        boxH = pls.Max(p => p.MaxY) - minY;
        boundingArea += boxW * boxH;
      }

      // Real utilization (rectangle mode only): deflate the inflated extent by the spacing margin.
      double realUtil = double.NaN;
      if (opt.RectW > 0 && !opt.Mix && boxW > opt.Spacing && boxH > opt.Spacing)
      {
        double realArea = (double)opt.RectCount * opt.RectW * opt.RectH;
        realUtil = realArea / ((boxW - opt.Spacing) * (boxH - opt.Spacing));
      }

      return new RunResult
      {
        Density = boundingArea > 0 ? top.TotalPartsArea / boundingArea : 0,
        RealUtil = realUtil,
        GridCount = DeepNestLib.Placement.GridNesting.LastGridCount,
        Utilization = top.MaterialUtilization,
        Placed = top.TotalPlacedCount,
        Total = top.TotalPartsCount,
        SheetsUsed = top.UsedSheets.Count,
        Fitness = top.Fitness,
        Elapsed = sw.Elapsed.TotalSeconds,
        Generations = state.Generations,
        BoxW = boxW,
        BoxH = boxH,
        MovedCount = DeepNestLib.Placement.Compaction.LastMovedCount,
        TotalSlide = DeepNestLib.Placement.Compaction.LastTotalSlide,
        ReinsMoved = DeepNestLib.Placement.Reinsertion.LastMovedCount,
        ReinsGain = DeepNestLib.Placement.Reinsertion.LastImprovement,
        ClGain = DeepNestLib.Placement.CommonLine.LastImprovement,
      };
    }

    private static string Pct(double v) => double.IsNaN(v) ? "  n/a" : (v * 100).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private sealed class RunResult
    {
      public double Density { get; set; }

      public double RealUtil { get; set; }

      public int GridCount { get; set; }

      public double Utilization { get; set; }

      public int Placed { get; set; }

      public int Total { get; set; }

      public int SheetsUsed { get; set; }

      public double Fitness { get; set; }

      public double Elapsed { get; set; }

      public int Generations { get; set; }

      public double BoxW { get; set; }

      public double BoxH { get; set; }

      public int MovedCount { get; set; }

      public double TotalSlide { get; set; }

      public int ReinsMoved { get; set; }

      public double ReinsGain { get; set; }

      public double ClGain { get; set; }
    }

    private sealed class Options
    {
      public string DxfDir { get; private set; } = @"C:\Users\rosam\source\repos\DeepNestSharp\DeepNestPort\dxfs";

      public int MaxFiles { get; private set; } = 1000;

      public int Copies { get; private set; } = 8;

      public int SheetW { get; private set; } = 3000;

      public int SheetH { get; private set; } = 1500;

      public int Sheets { get; private set; } = 4;

      public int Rotations { get; private set; } = 4;

      public double Spacing { get; private set; } = 5;

      public PlacementTypeEnum Placement { get; private set; } = PlacementTypeEnum.Gravity;

      public bool UseHoles { get; private set; } = false;

      public bool UseDllImport { get; private set; } = false;

      public bool Parallel { get; private set; } = false;

      public int? Seed { get; private set; } = null;

      public bool Compact { get; private set; } = false;

      public bool Reinsert { get; private set; } = false;

      public bool CommonLine { get; private set; } = false;

      public double Kerf { get; private set; } = 0.2;

      public int RectW { get; private set; } = 0;

      public int RectH { get; private set; } = 0;

      public int RectCount { get; private set; } = 24;

      public bool Grid { get; private set; } = false;

      public int GridMin { get; private set; } = 4;

      public int MaxNests { get; private set; } = 0;

      public int Stall { get; private set; } = 0;

      public double CurveTol { get; private set; } = 0;

      public string ExportDir { get; private set; } = null;

      public bool Mix { get; private set; } = false;

      public bool SmartRot { get; private set; } = false;

      public bool Polish { get; private set; } = false;

      public int SeedSteps { get; private set; } = 0;

      public int Population { get; private set; } = 50; // < ~20 breaks DeepNest's GA (fittestSurvivors = pop/10 must be >= 2)

      public int Multiplier { get; private set; } = 1;

      public double Seconds { get; private set; } = 15;

      public int Reps { get; private set; } = 3;

      public int MaxIterations { get; private set; } = 100000;

      public static Options Parse(string[] args)
      {
        var o = new Options();
        try
        {
          for (int i = 0; i < args.Length; i++)
          {
            string a = args[i];
            string Next() => args[++i];
            switch (a)
            {
              case "--dxfdir": o.DxfDir = Next(); break;
              case "--maxfiles": o.MaxFiles = int.Parse(Next()); break;
              case "--copies": o.Copies = int.Parse(Next()); break;
              case "--sheet":
                var wh = Next().Split('x', 'X');
                o.SheetW = int.Parse(wh[0]);
                o.SheetH = int.Parse(wh[1]);
                break;
              case "--sheets": o.Sheets = int.Parse(Next()); break;
              case "--rot": o.Rotations = int.Parse(Next()); break;
              case "--spacing": o.Spacing = double.Parse(Next(), CultureInfo.InvariantCulture); break;
              case "--placement": o.Placement = Enum.Parse<PlacementTypeEnum>(Next(), true); break;
              case "--holes": o.UseHoles = true; break;
              case "--dllimport": o.UseDllImport = true; break;
              case "--parallel": o.Parallel = true; break;
              case "--seed": o.Seed = int.Parse(Next()); break;
              case "--compact": o.Compact = true; break;
              case "--reinsert": o.Reinsert = true; break;
              case "--commonline": o.CommonLine = true; break;
              case "--kerf": o.Kerf = double.Parse(Next(), CultureInfo.InvariantCulture); break;
              case "--rect":
                var rwh = Next().Split('x', 'X');
                o.RectW = int.Parse(rwh[0]);
                o.RectH = int.Parse(rwh[1]);
                break;
              case "--rectcount": o.RectCount = int.Parse(Next()); break;
              case "--grid": o.Grid = true; break;
              case "--gridmin": o.GridMin = int.Parse(Next()); break;
              case "--maxnests": o.MaxNests = int.Parse(Next()); break;
              case "--stall": o.Stall = int.Parse(Next()); break;
              case "--curvetol": o.CurveTol = double.Parse(Next()); break;
              case "--export": o.ExportDir = Next(); break;
              case "--mix": o.Mix = true; break;
              case "--smartrot": o.SmartRot = true; break;
              case "--polish": o.Polish = true; break;
              case "--seedsteps": o.SeedSteps = int.Parse(Next()); break;
              case "--pop": o.Population = int.Parse(Next()); break;
              case "--mult": o.Multiplier = int.Parse(Next()); break;
              case "--seconds": o.Seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
              case "--reps": o.Reps = int.Parse(Next()); break;
              case "-h":
              case "--help": return null;
              default:
                Console.Error.WriteLine("Unknown arg: " + a);
                return null;
            }
          }
        }
        catch (Exception ex)
        {
          Console.Error.WriteLine("Bad args: " + ex.Message);
          return null;
        }

        if (o.RectW > 0 && !o.Mix)
        {
          o.Copies = o.RectCount; // pure rectangle run: copies = rectcount
        }

        return o;
      }

      public static void PrintUsage()
      {
        Console.WriteLine("NestBench - DeepNest utilization benchmark");
        Console.WriteLine("  --dxfdir <dir>     folder of .dxf parts (default: repo DeepNestPort\\dxfs)");
        Console.WriteLine("  --maxfiles <n>     limit distinct parts loaded");
        Console.WriteLine("  --copies <n>       copies per distinct part (default 8)");
        Console.WriteLine("  --sheet <WxH>      stock size, e.g. 3000x1500");
        Console.WriteLine("  --sheets <n>       number of stock sheets (default 4)");
        Console.WriteLine("  --rot <n>          rotation steps (default 4)");
        Console.WriteLine("  --spacing <mm>     part-to-part gap (default 5)");
        Console.WriteLine("  --placement <t>    Gravity | BoundingBox | Squeeze");
        Console.WriteLine("  --holes            enable part-in-hole nesting");
        Console.WriteLine("  --dllimport        use native minkowski.dll (faster NFP)");
        Console.WriteLine("  --parallel         evaluate the GA population on 3 threads");
        Console.WriteLine("  --seed <n>         deterministic RNG seed (forces serial; reproducible A/B)");
        Console.WriteLine("  --compact          run post-placement compaction pass");
        Console.WriteLine("  --reinsert         run post-placement re-insertion local search");
        Console.WriteLine("  --commonline       run common-line gap closure (rectangular edges)");
        Console.WriteLine("  --kerf <mm>        shared-cut width for common-line (default 0.2)");
        Console.WriteLine("  --rect <WxH>       generate identical WxH rectangles instead of DXFs");
        Console.WriteLine("  --rectcount <n>    number of generated rectangles (default 24)");
        Console.WriteLine("  --grid             hybrid grid pre-placement for identical rectangles");
        Console.WriteLine("  --gridmin <n>      min identical rectangles to grid (default 4)");
        Console.WriteLine("  --mix              load DXFs in addition to the generated rectangle");
        Console.WriteLine("  --smartrot         seed parts at their minimum bounding-box orientation");
        Console.WriteLine("  --polish           one-time re-insertion polish of the winning nest");
        Console.WriteLine("  --seedsteps <n>    orientations for the smart seed (decoupled from --rot)");
        Console.WriteLine("  --pop <n>          GA population (default 12)");
        Console.WriteLine("  --mult <n>         GA multiplier (default 1)");
        Console.WriteLine("  --seconds <s>      wall-clock budget per rep (default 15)");
        Console.WriteLine("  --reps <n>         repetitions, reports best+mean (default 3)");
      }
    }
  }
}
