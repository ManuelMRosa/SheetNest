namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text.Json;
  using System.Threading;
  using System.Threading.Tasks;
  using ClipperLib;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;

  /// <summary>
  /// The nester: hands the job to the external <c>sparrow</c> engine (a native
  /// Windows binary bundled beside the app) and maps its solution back into the app's <see cref="INestResult"/> so it renders
  /// in the existing viewer. Multi-sheet: it fills sheets from the stock one at a time (each packed by
  /// sparrow, then gravity-compacted to the bottom-left corner), and pattern-replicates identical
  /// layouts so a uniform job is 1-2 sparrow runs instead of N. The placement mapping (rotate about the
  /// origin, then translate) was verified to reproduce sparrow's layout with zero overlap.
  /// </summary>
  internal static class SparrowNestService
  {
    // Stage timers, read by MetricUnitsPerfFixture.MeasureRealJobStageBreakdown. The wall time of a nest
    // is (waves x per-sheet budget) and everything else is noise â€” but only a breakdown proves that, so
    // keep these: they are what a "nesting is slow" report gets measured with. Caller resets them.
    internal static long DiagLoadMs;
    internal static long DiagSparrowMs;
    internal static long DiagMapMs;
    internal static long DiagHolesMs;
    internal static long DiagWaves;
    internal static long DiagTries;

    // Every best-of-K candidate as "sheet,seed,count,density,extentX,extentY", appended as each try lands.
    // A user reported the same job producing a different offcut on every run; this is what shows whether
    // the winner sparrow's own density picks is also the one that leaves the largest remnant. Caller resets.
    internal static readonly List<string> DiagCandidates = new List<string>();

    private static int diagPackSeq;

    private sealed class Loaded
    {
      public int Source;
      public INfp Nfp;        // original (rendered) geometry
      public INfp Dilated;    // tooling footprint grown by FitSpacing/2 â€” what sparrow packs
      public INfp Tooling;    // tooling footprint WITHOUT the spacing shell â€” what compaction must respect
      public int[] Angles;    // allowed orientations (discrete fallback + used by hole-filling)
      public bool Continuous; // "Free" rotation â†’ send NO orientation list so sparrow rotates continuously
      public double EffSpacing;   // NOMINAL spacing: what this part keeps to anything it cannot share a cut with
      public CommonCuttingMode Cc; // who it may share a cut edge with; None = nobody
      public int ShareKey;    // identity for SamePart: same drawing AND same hand

      // What to grow by where only ONE polygon per part can be handed over: the engine's input and the
      // hole filler. Zero when this part may share with EVERY other part in the job (then a tight pack is
      // exactly right and nothing is lost), otherwise the full nominal spacing, because those two callers
      // cannot express a clearance that depends on which neighbour it is. Compaction can, and uses
      // EffSpacing with MayShare instead.
      public double FitSpacing;
      public double Kerf;     // cut width (drawing units) â€” the exact gap common-line neighbours snap to
      public int Qty;
      public bool Mirrored;   // this population is X-flipped â€” placements carry IsMirrored for the exporter
      public int Priority;    // 1-10, LOWER nests first (1 = highest); decides which parts fill earlier sheets
    }

    /// <summary>Single-sheet convenience (used by tests): one sheet of the given size.</summary>
    internal static INestResult Nest(
      IReadOnlyList<RasterPartInfo> parts,
      double sheetWin,
      double sheetHin,
      int rotations,
      double spacing,
      double margin,
      int timeLimitSec,
      string sparrowExePath,
      out string error,
      CancellationToken cancel = default)
      => Nest(parts, new[] { ((int)Math.Round(sheetWin), (int)Math.Round(sheetHin), 1) }, rotations, spacing, margin, timeLimitSec, sparrowExePath, out error, cancel);

    /// <summary>Multi-sheet nest: fills sheets from <paramref name="stock"/> until the pool is empty or
    /// the stock is exhausted; whatever cannot fit is returned as unplaced.
    /// <para><paramref name="perSheetBudgetSec"/> no longer bounds the search â€” how hard a sheet is
    /// searched is a fixed iteration count (see <see cref="IterationBudget"/>), because a budget in
    /// seconds means a busy or slower machine returns a different nest. It survives as the scale for the
    /// watchdog that kills a hung engine process.</para></summary>
    internal static INestResult Nest(
      IReadOnlyList<RasterPartInfo> parts,
      IReadOnlyList<(int Win, int Hin, int Qty)> stock,
      int rotations,
      double spacing,
      double margin,
      int perSheetBudgetSec,
      string sparrowExePath,
      out string error,
      CancellationToken cancel = default,
      System.IProgress<(int Placed, int Total, int Sheet, double Density)> progress = null)
    {
      error = null;
      if (string.IsNullOrWhiteSpace(sparrowExePath) || !File.Exists(sparrowExePath))
      {
        error = "Nesting engine (sparrow.exe) was not found.";
        return null;
      }

      // Expand the stock into a flat list of sheet slots (respecting each size's quantity).
      var slots = new List<(int W, int H)>();
      if (stock != null)
      {
        foreach (var s in stock)
        {
          if (s.Win > 0 && s.Hin > 0 && s.Qty > 0)
          {
            for (int i = 0; i < s.Qty; i++)
            {
              slots.Add((s.Win, s.Hin));
            }
          }
        }
      }

      if (slots.Count == 0)
      {
        error = "Add a sheet size first.";
        return null;
      }

      const int MaxSheets = 1000;
      if (slots.Count > MaxSheets)
      {
        slots = slots.Take(MaxSheets).ToList();
      }

      // Common-line packs as densely as sparrow can (mixing orientations); the snap then aligns whatever
      // near-parallel edges it left, and the post decides which shared edges to cut once. The snap needs
      // straight H/V edges, so common-line uses axis-aligned rotation (4-way) rather than free/fine angles.
      var diagLoad = System.Diagnostics.Stopwatch.StartNew();
      var loaded = LoadAll(parts, rotations, spacing, out error);
      DiagLoadMs = diagLoad.ElapsedMilliseconds;
      if (loaded == null || loaded.Count == 0)
      {
        error ??= "No valid parts to nest.";
        return null;
      }

      return RunNestBody(loaded, slots, margin, perSheetBudgetSec, sparrowExePath, cancel, progress, out error);
    }

    /// <summary>The per-sheet nest loop: fills sheets from the stock (best-of-K sparrow packs, corner
    /// compaction, pattern replication) and maps to an <see cref="INestResult"/>.</summary>
    private static INestResult RunNestBody(
      List<Loaded> loaded,
      List<(int W, int H)> slots,
      double margin,
      int perSheetBudgetSec,
      string sparrowExePath,
      CancellationToken cancel,
      System.IProgress<(int Placed, int Total, int Sheet, double Density)> progress,
      out string error)
    {
      error = null;
      var loadedById = loaded.ToDictionary(l => l.Source);
      var pool = loaded.ToDictionary(l => l.Source, l => l.Qty);
      int totalParts = pool.Values.Sum();

      var sheetLayouts = new List<List<IPartPlacement>>();
      var sheetSizes = new List<(int W, int H)>();

      int slot = 0;
      while (slot < slots.Count && pool.Values.Sum() > 0)
      {
        cancel.ThrowIfCancellationRequested();
        int placedSoFar = totalParts - pool.Values.Sum();
        int sheetNum = sheetLayouts.Count + 1;
        progress?.Report((placedSoFar, totalParts, sheetNum, 0));
        Action<double> onDensity = d => progress?.Report((placedSoFar, totalParts, sheetNum, d));
        var (w, h) = slots[slot];
        var batchQty = SelectBatch(pool, loadedById, w, h);

        // Search budget, in ITERATIONS rather than seconds â€” see RunSparrowOnce. A sheet with few parts
        // converges sooner, so the budget scales with the batch (this matters for the sparse tail sheet
        // and for many small sheets); early termination (-x) still trims a sheet that gets stuck. The
        // wall clock a sheet takes is now a consequence of the machine, not an input to the answer.
        int batchParts = batchQty.Values.Sum();
        int budget = IterationBudget(batchParts);

        // How many identical sheets this exact batch will tile (it is packed ONCE then pattern-replicated).
        // A batch that governs many clones is worth many best-of-K tries â€” the cost is amortized over all
        // the clones â€” which makes the template sheet converge to a consistent, dense layout (more time
        // does NOT reduce sparrow's run-to-run variance, but more tries do).
        int replicas = batchQty.Count == 0 ? 1 : batchQty.Min(kv => pool[kv.Key] / Math.Max(1, kv.Value));

        // The final/tail sheet takes the whole remaining pool (a leftover count that won't fill a sheet).
        // It is a single one-off â€” replicas=1 â†’ base tries â†’ it varies run-to-run. Since it's just ONE
        // sheet, invest more best-of-K in it so it lands consistently, like the replicated body already does.
        bool isFinalSheet = batchParts == pool.Values.Sum();
        int tries = TriesFor(replicas, isFinalSheet);

        // packW/packH = the sheet dims PackOneSheet packed in (always the stock slot's own orientation â€” the
        // sheet is never auto-rotated). Kept as a return value so the render matches what was packed.
        // Watchdog only: a search must finish because it ran out of iterations, never because it ran out
        // of seconds. Scaled off the caller's time preference so a user who asks for longer nests also
        // gets a longer leash, but far above any healthy run â€” it exists to catch a hung process.
        int hardTimeoutSec = Math.Max(60, perSheetBudgetSec * 30);

        var (placements, placedBySrc, packW, packH) = PackOneSheet(loaded, batchQty, w, h, margin, budget, hardTimeoutSec, tries, sparrowExePath, cancel, onDensity, out string perr);
        if (cancel.IsCancellationRequested)
        {
          error = "Cancelled.";
          return null;
        }

        if (placements == null || placements.Count == 0)
        {
          // Nothing fit this sheet size (a part is bigger than it) â€” try the next slot/size.
          slot++;
          continue;
        }

        sheetLayouts.Add(placements);
        sheetSizes.Add((packW, packH));
        Deduct(pool, placedBySrc);
        slot++;

        // Pattern replication: while the pool still holds the SAME composition and the next slots are
        // the same size, clone this layout instead of re-nesting (uniform jobs â†’ 1 run + replicate).
        while (slot < slots.Count && slots[slot].W == w && slots[slot].H == h && PoolContains(pool, placedBySrc))
        {
          sheetLayouts.Add(ClonePlacements(placements));
          sheetSizes.Add((packW, packH));
          Deduct(pool, placedBySrc);
          slot++;
          progress?.Report((totalParts - pool.Values.Sum(), totalParts, sheetLayouts.Count, 1.0));
        }
      }

      progress?.Report((totalParts - pool.Values.Sum(), totalParts, sheetLayouts.Count, 1.0));

      if (sheetLayouts.Count == 0)
      {
        error = "The nesting engine returned no placements.";
        return null;
      }

      // jagua ignores item holes, so sparrow never nests inside them. Recover that density ourselves:
      // drop leftover parts into the holes of already-placed parts (exact geometry, spacing preserved).
      var diagHoles = System.Diagnostics.Stopwatch.StartNew();
      FillHoles(sheetLayouts, pool, loadedById, cancel);
      DiagHolesMs = diagHoles.ElapsedMilliseconds;

      var collection = new SheetPlacementCollection();
      int id = 0;
      for (int i = 0; i < sheetLayouts.Count; i++)
      {
        foreach (var pp in sheetLayouts[i])
        {
          ((PartPlacement)pp).Id = id++;
        }

        var sheet = Sheet.NewSheet(i + 1, sheetSizes[i].W, sheetSizes[i].H);
        collection.Add(new SheetPlacement(PlacementTypeEnum.BoundingBox, sheet, sheetLayouts[i], 0, SvgNest.Config.ClipperScale));
      }

      var unplaced = new List<INfp>();
      foreach (var kv in pool)
      {
        for (int q = 0; q < kv.Value; q++)
        {
          unplaced.Add(loadedById[kv.Key].Nfp);
        }
      }

      int placedTotal = sheetLayouts.Sum(s => s.Count);
      return new NestResult(placedTotal + unplaced.Count, collection, unplaced, PlacementTypeEnum.BoundingBox, 0, 0);
    }

    /// <summary>
    /// True when these two parts are allowed to share a cut edge. The stricter mode wins, so a Same-part
    /// part shares only with its own drawing and its own hand however permissive the other side is.
    /// </summary>
    private static bool PartsMayShare(RasterPartInfo a, RasterPartInfo b)
    {
      if (a.Cc == CommonCuttingMode.None || b.Cc == CommonCuttingMode.None)
      {
        return false;
      }

      if (a.Cc == CommonCuttingMode.SamePart || b.Cc == CommonCuttingMode.SamePart)
      {
        return string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) && a.Mirrored == b.Mirrored;
      }

      return true;
    }

    /// <summary>Loads each part's contour once (arcs tessellated by the app pipeline) and pre-computes its
    /// spacing-dilated shell + allowed orientations. Holes (INfp.Children) are not yet forwarded.</summary>
    private static List<Loaded> LoadAll(IReadOnlyList<RasterPartInfo> parts, int rotations, double spacing, out string error)
    {
      error = null;
      var helper = new NestExecutionHelper();
      var loaded = new List<Loaded>();
      int source = 0;

      // The engine takes ONE polygon per part and has no notion of a clearance that depends on which
      // neighbour it is, so the shell we grow it by has to answer for every neighbour at once. Grow by
      // nothing only when every part in the job may share a cut with every other, which is when a tight
      // pack is exactly right; otherwise carry the full spacing and let compaction reclaim the contact
      // between the pairs that really do share. When they all share this is byte-for-byte what the old
      // job-wide common-line flag did.
      bool everyPartShares = parts.Count > 0 && parts.All(a => a != null && parts.All(b => b == null || PartsMayShare(a, b)));

      // "The same part" means the same DRAWING, not the same row: the list may legitimately hold one file
      // twice (once common-cut, once spaced), and two rows of the same file are still the same part on the
      // sheet. A MIRRORED population is a different part, though — its shared edges are the other hand's.
      var shareKeys = new Dictionary<(string Path, bool Mirrored), int>();

      foreach (var part in parts)
      {
        if (part == null || string.IsNullOrWhiteSpace(part.Path) || part.Quantity <= 0)
        {
          continue;
        }

        DeepNestLib.IO.IRawDetail det;
        try
        {
          det = helper.LoadRawDetail(new FileInfo(part.Path));
        }
        catch (Exception ex)
        {
          // A file that cannot be read stops the nest and says which one and why. Nesting the rest quietly
          // would hand the operator a sheet that is missing a part, and that gets cut before anyone counts.
          error = $"{Path.GetFileName(part.Path)} could not be read.{Environment.NewLine}{Environment.NewLine}{ex.GetBaseException().Message}";
          return null;
        }

        if (det != null && det.TryConvertToNfp(source, out INfp nfp) && nfp.Points.Length > 2)
        {
          if (part.Mirrored)
          {
            // Bake the mirror into the RENDERED geometry; the placement also carries IsMirrored so the
            // DXF exporter (which reloads the ORIGINAL file) mirrors it once too â€” same order the
            // exporter uses (MirrorX about origin â†’ rotate â†’ translate), so both paths agree.
            nfp = nfp.MirrorX();
          }

          int code = part.Rotations > 0 ? part.Rotations : rotations;

          // Common-line needs straight H/V edges for the snap to line up neighbours. Fine/continuous
          // rotation (45Â° steps, free 15Â°) would leave edges at odd angles the snap can't use, so drop it
          // to 4-way. Decide by the ANGLES, not the code number: the special codes 1001/1002/1003 are
          // numerically >= 1000 but are already axis-aligned (90-only, 0/90, 90/270) and must be kept â€” an
          // earlier `code >= 8` test wrongly turned 0/90 into 4-way, adding 180/270.
          // PER PART, not per job: clipping every part because ONE of them is common-cut would quietly
          // take free rotation away from parts that never asked for it, and cost density for nothing.
          if (part.Cc != CommonCuttingMode.None && RotationCodes.PermittedSet(code).Any(a => a % 90 != 0))
          {
            code = 4;
          }

          double effSpacing = part.Spacing >= 0 ? part.Spacing : Math.Max(0, spacing);
          double fitSpacing = everyPartShares ? 0 : effSpacing;

          var shareOf = (Path: part.Path ?? string.Empty, part.Mirrored);
          if (!shareKeys.TryGetValue(shareOf, out int shareKey))
          {
            shareKeys[shareOf] = shareKey = shareKeys.Count;
          }

          // Mirroring is baked into the geometry above, so the tooling has to follow it.
          var toolPaths = part.ToolPaths;
          if (part.Mirrored && toolPaths != null)
          {
            toolPaths = toolPaths.Select(p => (IReadOnlyList<SvgPoint>)p.Select(v => new SvgPoint(-v.X, v.Y)).ToList()).ToList();
          }

          loaded.Add(new Loaded
          {
            Source = source,
            Nfp = nfp,
            Dilated = ToolingFootprint(nfp, toolPaths, part.Kerf, fitSpacing),

            // Same shape without the spacing shell: compaction adds the spacing itself, so handing it
            // the dilated one would double-count. With no tooling this IS the outline, which keeps plain
            // DXF parts compacting exactly as they did.
            Tooling = ToolingFootprint(nfp, toolPaths, part.Kerf, 0),
            Angles = RotationCodes.PermittedSet(code),
            Continuous = code == 36, // only "Free" (sentinel 36) is continuous; 1001/1002/1003 are discrete sets
            EffSpacing = effSpacing,
            FitSpacing = fitSpacing,
            Cc = part.Cc,
            ShareKey = shareKey,
            Kerf = Math.Max(0, part.Kerf),
            Qty = part.Quantity,
            Mirrored = part.Mirrored,
            Priority = part.Priority,
          });
          source++;
        }
      }

      return loaded;
    }

    /// <summary>Picks a batch from the pool up to ~1.3Ã— the sheet area (extra choice for sparrow), keeping
    /// source variety for mixed jobs. Always returns at least one part so an oversize part gets a try.</summary>
    private static Dictionary<int, int> SelectBatch(Dictionary<int, int> pool, Dictionary<int, Loaded> loadedById, int w, int h)
    {
      double cap = 1.3 * w * h;
      double sheetArea = (double)w * h;
      var batch = new Dictionary<int, int>();
      double acc = 0;
      int prevPriority = int.MinValue;

      // Highest priority first (1 = highest, lower number wins) so preferred parts fill earlier
      // sheets and the leftover tail â€” what ends up unplaced when material runs out â€” is lowest priority.
      foreach (var kv in pool.OrderBy(kv => loadedById[kv.Key].Priority).ThenBy(kv => kv.Key))
      {
        if (kv.Value <= 0)
        {
          continue;
        }

        // Don't let a lower-priority part share a sheet that higher-priority parts already fill: once a
        // full sheet's worth of higher-priority area is queued, stop before crossing to a lower priority.
        // Keeps priority parts off the "left over" pile even though sparrow itself is priority-blind.
        int pr = loadedById[kv.Key].Priority;
        if (pr > prevPriority && acc >= sheetArea)
        {
          break;
        }

        double area = Math.Max(1e-6, Math.Abs(loadedById[kv.Key].Nfp.Area));
        int take = 0;
        while (take < kv.Value && acc + area <= cap)
        {
          acc += area;
          take++;
        }

        if (take > 0)
        {
          batch[kv.Key] = take;
          prevPriority = pr;
        }

        if (acc >= cap)
        {
          break;
        }
      }

      if (batch.Count == 0)
      {
        // Nothing fit the area cap â€” force the single highest-priority remaining part onto the sheet.
        var first = pool.Where(k => k.Value > 0)
          .OrderBy(k => loadedById[k.Key].Priority).ThenBy(k => k.Key).First();
        batch[first.Key] = 1;
      }

      return batch;
    }

    /// <summary>Packs one batch onto one sheet, best-of-K with EARLY-STOP: races sparrow searches (fixed seeds)
    /// in waves and keeps the candidate that PLACES THE MOST parts (tie: denser, then lower seed). Stops
    /// launching more tries as soon as a whole wave fails to improve the best part-count â€” easy/uniform sheets
    /// converge in a wave or two, so they don't burn all `tries`; hard/variable sheets keep going up to `tries`.
    /// Packs ONLY in the user's chosen sheet orientation â€” the sheet is never auto-rotated (a 120Ã—60 preset must
    /// render 120Ã—60); to use the other orientation the operator picks that preset.</summary>
    private static (List<IPartPlacement> Placements, Dictionary<int, int> PlacedBySource, int PackW, int PackH) PackOneSheet(
      List<Loaded> loaded, Dictionary<int, int> batchQty, int sheetW, int sheetH, double margin, int budget, int hardTimeoutSec, int tries, string exe, CancellationToken cancel, Action<double> onDensity, out string error)
    {
      error = null;
      var batch = loaded.Where(l => batchQty.TryGetValue(l.Source, out int q) && q > 0).ToList();
      if (batch.Count == 0)
      {
        return (null, null, sheetW, sheetH);
      }

      // Live-density progress reports the best seen across all tries so the bar climbs with the winner.
      double bestSeen = 0;
      object gate = new object();
      Action<double>? aggDensity = onDensity == null ? null : d =>
      {
        lock (gate)
        {
          if (d > bestSeen)
          {
            bestSeen = d;
            onDensity(d);
          }
        }
      };

      string workDir = Path.Combine(Path.GetTempPath(), "SheetNestSparrow", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(workDir);
      try
      {
        // sparrow fills the strip height edge-to-edge; reserve the margins up front (usable height) so the
        // anchored pack doesn't poke past the far edge and get dropped as overflow.
        double stripHeight = Math.Max(1.0, sheetH - (2 * margin));
        string inputPath = Path.Combine(workDir, "job.json");
        File.WriteAllText(inputPath, BuildJaguaJson("job", stripHeight, batch, batchQty));

        // WHICH seeds are raced against each other, and how many, decides the result â€” so the wave is a
        // FIXED size on every machine. How many of those run at the same instant is just scheduling, and
        // that is what the core count is allowed to decide: a machine with fewer cores takes longer to
        // finish the same wave, but computes the same nest. Tying the wave itself to ProcessorCount (it
        // used to be the whole story) meant a 4-core laptop and a 20-core desktop nested the same job
        // differently.
        int waveTries = Math.Min(tries, WaveSize);
        int maxConc = Math.Max(1, Math.Min(waveTries, Concurrency()));

        // The pack is gravity-compacted along the sheet's LONG axis, so that extent is what decides how
        // much of the sheet is left in one piece. It is the metric the operator sees; sparrow's density
        // is measured on its own strip BEFORE the compaction, so the two can disagree.
        bool longAxisIsX = sheetW >= sheetH;
        var winners = new List<(List<IPartPlacement> Pl, Dictionary<int, int> By, int Count, double Extent, double Density, int Seed)>();
        int bestCount = -1, launched = 0;
        int packIndex = System.Threading.Interlocked.Increment(ref diagPackSeq);

        while (launched < tries && !cancel.IsCancellationRequested)
        {
          int waveSize = Math.Min(waveTries, tries - launched);
          var wPl = new List<IPartPlacement>[waveSize];
          var wBy = new Dictionary<int, int>[waveSize];
          var wSt = new (int Count, double Density, int Seed)?[waveSize];
          int startSeed = launched + 1;
          var options = new ParallelOptions { MaxDegreeOfParallelism = maxConc };
          Parallel.For(0, waveSize, options, j =>
          {
            int seed = startSeed + j;
            string tryDir = Path.Combine(workDir, "try" + seed.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(tryDir);
            var diagRun = System.Diagnostics.Stopwatch.StartNew();
            string outJson = RunSparrowOnce(exe, tryDir, inputPath, budget, hardTimeoutSec, seed, cancel, aggDensity, out _);
            System.Threading.Interlocked.Add(ref DiagSparrowMs, diagRun.ElapsedMilliseconds);
            if (outJson == null || cancel.IsCancellationRequested)
            {
              return;
            }

            var diagMap = System.Diagnostics.Stopwatch.StartNew();
            var (pl, by) = MapAndCompact(outJson, batch, sheetW, sheetH, margin, cancel);
            System.Threading.Interlocked.Add(ref DiagMapMs, diagMap.ElapsedMilliseconds);
            wPl[j] = pl;
            wBy[j] = by;
            wSt[j] = (pl.Count, ParseDensity(outJson), seed);
          });

          launched += waveSize;
          System.Threading.Interlocked.Increment(ref DiagWaves);
          System.Threading.Interlocked.Add(ref DiagTries, waveSize);
          for (int j = 0; j < waveSize; j++)
          {
            if (wSt[j].HasValue)
            {
              var (ex, ey) = Extent(wPl[j]);
              winners.Add((wPl[j], wBy[j], wSt[j].Value.Count, longAxisIsX ? ex : ey, wSt[j].Value.Density, wSt[j].Value.Seed));
              lock (DiagCandidates)
              {
                DiagCandidates.Add(FormattableString.Invariant(
                  $"{packIndex},{wSt[j].Value.Seed},{wSt[j].Value.Count},{wSt[j].Value.Density:F5},{ex:F3},{ey:F3}"));
              }
            }
          }

          // Plateau: stop once a full wave fails to beat the best part-count so far (â‰¥1 wave already ran).
          // Part-count is a hard metric that plateaus fast on easy sheets and correlates with density.
          int waveBest = winners.Count == 0 ? -1 : winners.Max(w => w.Count);
          if (bestCount >= 0 && waveBest <= bestCount)
          {
            break;
          }

          bestCount = waveBest;
        }

        if (cancel.IsCancellationRequested)
        {
          error = "Cancelled.";
          return (null, null, sheetW, sheetH);
        }

        if (winners.Count == 0)
        {
          error = "The nesting engine produced no solution.";
          return (null, null, sheetW, sheetH);
        }

        int bi = PickBest(winners.Select(w => (w.Count, w.Extent, w.Density, w.Seed)).ToList());
        var sel = winners[bi];
        return (sel.Pl, sel.By, sheetW, sheetH);
      }
      finally
      {
        try
        {
          Directory.Delete(workDir, true);
        }
        catch (IOException)
        {
        }
      }
    }

    /// <summary>Maps sparrow's placements, anchors + gravity-compacts the pack to the bottom-left corner,
    /// and splits off anything past the sheet edge (not placed).</summary>
    private static (List<IPartPlacement> Placements, Dictionary<int, int> PlacedBySource) MapAndCompact(
      string outputJson, List<Loaded> batch, double sheetWin, double sheetHin, double margin, CancellationToken cancel)
    {
      using var doc = JsonDocument.Parse(outputJson);
      var placed = doc.RootElement.GetProperty("solution").GetProperty("layout").GetProperty("placed_items");
      var spacingById = batch.ToDictionary(l => l.Source, l => l.EffSpacing);
      var kerfById = batch.ToDictionary(l => l.Source, l => l.Kerf);
      var toolingById = batch.ToDictionary(l => l.Source, l => l.Tooling);
      var ccById = batch.ToDictionary(l => l.Source, l => (l.Cc, l.ShareKey));

      var all = new List<IPartPlacement>();
      foreach (var pi in placed.EnumerateArray())
      {
        // item_id is the batch INDEX (see BuildJaguaJson); translate it back to the real Loaded/Source.
        int itemId = pi.GetProperty("item_id").GetInt32();
        if (itemId < 0 || itemId >= batch.Count)
        {
          continue;
        }

        var src = batch[itemId];
        var tf = pi.GetProperty("transformation");
        double rot = tf.GetProperty("rotation").GetDouble();
        var t = tf.GetProperty("translation");
        var rotated = Math.Abs(rot % 360) < 1e-9 ? src.Nfp : src.Nfp.Rotate(rot);
        all.Add(new PartPlacement(rotated) { X = t[0].GetDouble(), Y = t[1].GetDouble(), Rotation = rot, Source = src.Source, IsMirrored = src.Mirrored });
      }

      if (all.Count == 0)
      {
        return (new List<IPartPlacement>(), new Dictionary<int, int>());
      }

      // Pre-anchor min â†’ margin so compaction starts on-sheet, then gravity-compact to the corner.
      double minX = all.Min(p => p.PlacedPart.MinX);
      double minY = all.Min(p => p.PlacedPart.MinY);
      foreach (var pp in all)
      {
        pp.X += margin - minX;
        pp.Y += margin - minY;
      }

      // Compact against the TOOLING footprint, not the bare outline. Compaction slides parts together
      // until their polygons are `spacing` apart â€” with common-line that is exact contact, which on a
      // toolpathed part would drive the kerf and the lead-ins into the neighbour, undoing the clearance
      // sparrow just respected. Feeding it the footprint makes "exact contact" mean the outlines sit one
      // kerf apart: the shared cut edge, with neither part losing material.
      var compactItems = all.Select(pp => new CompactItem
      {
        Poly = toolingById.TryGetValue(pp.Source, out var tooling) && tooling != null
          ? tooling.Rotate(pp.Rotation)   // same frame as pp.Part: rotated about the origin, mirror baked in
          : pp.Part,
        X = pp.X,
        Y = pp.Y,
        Spacing = spacingById.TryGetValue(pp.Source, out double sp) ? sp : 0,
        Cc = ccById.TryGetValue(pp.Source, out var cc) ? cc.Cc : CommonCuttingMode.None,
        ShareKey = ccById.TryGetValue(pp.Source, out var sk) ? sk.ShareKey : 0,
      }).ToList();
      RasterCompact.Compact(compactItems, sheetWin, sheetHin, margin, cancel: cancel);
      for (int k = 0; k < all.Count; k++)
      {
        all[k].X = compactItems[k].X;
        all[k].Y = compactItems[k].Y;
      }

      // Common-line: nudge near-parallel neighbour edges onto exactly one kerf apart and aligned, so the
      // post processor recognises the shared cut and cuts it once. Best-effort per part (all-or-nothing):
      // a part only moves if the shift keeps every part's tooling clear of every other.
      if (batch.Any(l => l.Cc != CommonCuttingMode.None))
      {
        SnapCommonLineEdges(all, kerfById, toolingById, ccById);
      }

      const double tol = 1e-3;
      var placements = new List<IPartPlacement>();
      var placedBySource = new Dictionary<int, int>();
      foreach (var pp in all)
      {
        var ppp = pp.PlacedPart;
        if (ppp.MinX < -tol || ppp.MinY < -tol || ppp.MaxX > sheetWin + tol || ppp.MaxY > sheetHin + tol)
        {
          continue; // past the sheet edge â†’ not placed on this sheet (stays in the pool)
        }

        placements.Add(pp);
        placedBySource[pp.Source] = placedBySource.TryGetValue(pp.Source, out int n) ? n + 1 : 1;
      }

      return (placements, placedBySource);
    }

    /// <summary>An axis-aligned straight edge of a placed part, in absolute sheet coordinates.</summary>
    private readonly struct AaEdge
    {
      public AaEdge(int part, bool vertical, double pos, double lo, double hi, bool materialBelow)
      {
        this.Part = part;
        this.Vertical = vertical;   // true = runs along Y at X=Pos; false = runs along X at Y=Pos
        this.Pos = pos;             // the constant coordinate (X for vertical, Y for horizontal)
        this.Lo = lo;
        this.Hi = hi;               // the edge spans [Lo, Hi] on the other axis
        this.MaterialBelow = materialBelow; // material is on the lower-Pos side (a "right"/"top" edge)
      }

      public int Part { get; }

      public bool Vertical { get; }

      public double Pos { get; }

      public double Lo { get; }

      public double Hi { get; }

      public bool MaterialBelow { get; }
    }

    /// <summary>
    /// Common-line snap: nudges parts so a near-parallel neighbour edge pair lands exactly one kerf apart
    /// and aligned, giving the post processor coincident toolpath endpoints to recognise. Only axis-aligned
    /// edges (a common-cut part is clipped to 90-degree steps, so shared edges are horizontal or vertical).
    /// Only pairs that are ALLOWED to share are snapped: a Same-part item is never pulled onto a different
    /// drawing. All-or-nothing per connected group: a group of parts only moves if, after moving, every
    /// part's tooling footprint still clears every other part (no cut eating into a neighbour).
    /// </summary>
    internal static void SnapCommonLineEdges(
      List<IPartPlacement> all,
      Dictionary<int, double> kerfById,
      Dictionary<int, INfp> toolingById,
      Dictionary<int, (CommonCuttingMode Cc, int ShareKey)> ccById,
      CommonCuttingTolerances tol = null)
    {
      tol ??= CommonCuttingTolerances.Default;
      int n = all.Count;
      if (n < 2)
      {
        return;
      }

      double KerfOf(int i) => kerfById.TryGetValue(all[i].Source, out double k) ? Math.Max(0, k) : 0;

      if (Enumerable.Range(0, n).All(i => KerfOf(i) <= 0))
      {
        return; // no tooling anywhere: there is no kerf to snap onto (compaction already closed these)
      }

      bool MayShare(int ia, int ib)
      {
        if (!ccById.TryGetValue(all[ia].Source, out var a) || !ccById.TryGetValue(all[ib].Source, out var b))
        {
          return false;
        }

        if (a.Cc == CommonCuttingMode.None || b.Cc == CommonCuttingMode.None)
        {
          return false;
        }

        if (a.Cc == CommonCuttingMode.SamePart || b.Cc == CommonCuttingMode.SamePart)
        {
          return a.ShareKey == b.ShareKey;
        }

        return true;
      }

      // Absolute geometry + axis-aligned edges per part. Edge length is filtered by the part's OWN kerf:
      // the batch maximum would throw away usable edges on a finer-cutting part in a mixed job.
      var absPts = new List<System.Windows.Point>[n];
      var edges = new List<AaEdge>();
      // Sine, not degrees: the test below compares a component against the length, which IS the sine.
      double angTol = tol.AngleToleranceSin;
      for (int i = 0; i < n; i++)
      {
        // Ignore tiny edges (chamfers etc.). The absolute floor matters where the kerf is fine: a very
        // short segment out of the parser carries more direction noise than signal.
        double minLen = Math.Max(KerfOf(i) * tol.MinEdgeLengthKerfs, tol.MinEdgeLengthAbsolute);
        var pts = all[i].PlacedPart.Points.Select(p => new System.Windows.Point(p.X, p.Y)).ToList();
        absPts[i] = pts;
        double cx = pts.Average(p => p.X);
        int m = pts.Count;
        for (int k = 0; k < m; k++)
        {
          var a = pts[k];
          var b = pts[(k + 1) % m];
          double dx = b.X - a.X, dy = b.Y - a.Y;
          double len = Math.Sqrt((dx * dx) + (dy * dy));
          if (len < minLen)
          {
            continue;
          }

          if (Math.Abs(dx) < angTol * len) // vertical
          {
            double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
            edges.Add(new AaEdge(i, true, a.X, lo, hi, cx < a.X)); // material left of the edge â†’ "right" edge
          }
          else if (Math.Abs(dy) < angTol * len) // horizontal
          {
            double lo = Math.Min(a.X, b.X), hi = Math.Max(a.X, b.X);
            double cy = pts.Average(p => p.Y);
            edges.Add(new AaEdge(i, false, a.Y, lo, hi, cy < a.Y)); // material below the edge â†’ "top" edge
          }
        }
      }

      // Find neighbour edge pairs one kerf apart and aligned, and the shift that makes it exact. A "right"
      // edge of A (material below Pos) facing a "left" edge of B (material above Pos), one kerf apart.
      var shifts = new Dictionary<(int A, int B), (double Dx, double Dy)>();
      foreach (var ea in edges.Where(e => e.MaterialBelow))
      {
        foreach (var eb in edges.Where(e => !e.MaterialBelow && e.Vertical == ea.Vertical && e.Part != ea.Part
                                            && MayShare(ea.Part, e.Part)))
        {
          // PER PAIR, not the batch maximum: one part cutting at 0.006 must not drag a 0.002 neighbour
          // onto a 0.006 seam, and it is the wider of the two cuts that has to fit between them.
          double kerf = Math.Max(KerfOf(ea.Part), KerfOf(eb.Part));
          if (kerf <= 0)
          {
            continue;
          }

          double gap = eb.Pos - ea.Pos; // B sits above A on the Pos axis
          if (gap < tol.GapMinKerfs * kerf || gap > tol.GapMaxKerfs * kerf)
          {
            continue;
          }

          double overlap = Math.Min(ea.Hi, eb.Hi) - Math.Max(ea.Lo, eb.Lo);
          if (overlap < tol.MinOverlapKerfs * kerf)
          {
            continue; // must actually run alongside each other; a corner touch overlaps by nothing
          }

          // Shift B: perpendicular to land exactly one kerf off A, and along the edge to align the ends.
          double perp = (ea.Pos + kerf) - eb.Pos;   // move B's Pos to A.Pos + kerf
          double along = ea.Lo - eb.Lo;              // align the low ends

          // How far the part is asked to travel for this seam. Nothing else bounds it: aligning the low
          // ends can ask for a long slide when two edges barely overlap, and the only thing that used to
          // stop it was the tooling check at the very end, which reverts EVERYTHING when it fires.
          if (Math.Sqrt((perp * perp) + (along * along)) > tol.MaxSnapTravelKerfs * kerf)
          {
            continue;
          }

          var s = ea.Vertical ? (perp, along) : (along, perp);
          var key = (ea.Part, eb.Part);
          if (!shifts.ContainsKey(key))
          {
            shifts[key] = s;
          }
        }
      }

      if (shifts.Count == 0)
      {
        return;
      }

      // Connected groups by union-find; propagate a per-part offset from an anchor via BFS. A group whose
      // constraints disagree (a cycle wants two different offsets) is left as sparrow placed it.
      var adj = new Dictionary<int, List<(int Other, double Dx, double Dy, bool Forward)>>();
      void AddEdge(int a, int b, double dx, double dy)
      {
        (adj.TryGetValue(a, out var la) ? la : adj[a] = new()).Add((b, dx, dy, true));
        (adj.TryGetValue(b, out var lb) ? lb : adj[b] = new()).Add((a, dx, dy, false));
      }

      foreach (var kv in shifts)
      {
        AddEdge(kv.Key.A, kv.Key.B, kv.Value.Dx, kv.Value.Dy);
      }

      var offset = new (double X, double Y)?[n];
      var visited = new bool[n];
      var groups = new List<List<int>>();
      var badGroup = new HashSet<int>();
      foreach (var start in adj.Keys)
      {
        if (visited[start])
        {
          continue;
        }

        var group = new List<int>();
        var queue = new Queue<int>();
        offset[start] = (0, 0);
        visited[start] = true;
        queue.Enqueue(start);
        bool consistent = true;
        while (queue.Count > 0)
        {
          int u = queue.Dequeue();
          group.Add(u);
          foreach (var (v, dx, dy, forward) in adj[u])
          {
            // Constraint: B_offset = A_offset + shift. Forward edge is Aâ†’B (u is A), else u is B.
            var want = forward
              ? (offset[u].Value.X + dx, offset[u].Value.Y + dy)
              : (offset[u].Value.X - dx, offset[u].Value.Y - dy);
            if (offset[v] is { } have)
            {
              if (Math.Abs(have.X - want.Item1) > 1e-4 || Math.Abs(have.Y - want.Item2) > 1e-4)
              {
                consistent = false;
              }
            }
            else
            {
              offset[v] = want;
              visited[v] = true;
              queue.Enqueue(v);
            }
          }
        }

        groups.Add(group);
        if (!consistent)
        {
          foreach (var g in group)
          {
            badGroup.Add(g);
          }
        }
      }

      // Apply tentatively, then verify no tooling footprint invades another part. Revert failing groups.
      var origX = all.Select(p => p.X).ToArray();
      var origY = all.Select(p => p.Y).ToArray();
      foreach (var group in groups)
      {
        if (group.Any(g => badGroup.Contains(g)))
        {
          continue;
        }

        foreach (int i in group)
        {
          all[i].X = origX[i] + offset[i].Value.X;
          all[i].Y = origY[i] + offset[i].Value.Y;
        }
      }

      if (!ToolingClearsEveryone(all, toolingById))
      {
        // Something invaded â€” snap made it worse. Roll the whole pass back (safe: sparrow's layout stands).
        for (int i = 0; i < n; i++)
        {
          all[i].X = origX[i];
          all[i].Y = origY[i];
        }
      }
    }

    /// <summary>True when no part's tooling footprint overlaps another part's outline by more than a sliver
    /// (kerf bands may touch/overlap each other, but a cut must never eat into a neighbouring part).</summary>
    private static bool ToolingClearsEveryone(List<IPartPlacement> all, Dictionary<int, INfp> toolingById)
    {
      double scale = SvgNest.Config.ClipperScale;
      int n = all.Count;
      var partPaths = new List<IntPoint>[n];
      var toolPaths = new List<IntPoint>[n];
      for (int i = 0; i < n; i++)
      {
        partPaths[i] = ShiftPath(DeepNestClipper.ScaleUpPath(all[i].Part.Points, scale), (long)Math.Round(all[i].X * scale), (long)Math.Round(all[i].Y * scale));
        if (toolingById.TryGetValue(all[i].Source, out var tool) && tool != null)
        {
          var rot = Math.Abs(all[i].Rotation % 360) < 1e-9 ? tool : tool.Rotate(all[i].Rotation);
          toolPaths[i] = ShiftPath(DeepNestClipper.ScaleUpPath(rot.Points, scale), (long)Math.Round(all[i].X * scale), (long)Math.Round(all[i].Y * scale));
        }
        else
        {
          toolPaths[i] = partPaths[i];
        }
      }

      double invadeLimit = 0.02 * scale * scale; // area unitsÂ²; ignore rounding slivers
      for (int i = 0; i < n; i++)
      {
        for (int j = 0; j < n; j++)
        {
          if (i == j)
          {
            continue;
          }

          // Does i's cut (tooling) eat into j's actual part? intersection(tool_i, part_j) area.
          var clip = new Clipper();
          clip.AddPath(toolPaths[i], PolyType.ptSubject, true);
          clip.AddPath(partPaths[j], PolyType.ptClip, true);
          var sol = new List<List<IntPoint>>();
          clip.Execute(ClipType.ctIntersection, sol);
          double area = sol.Sum(p => Math.Abs(Clipper.Area(p)));
          if (area > invadeLimit)
          {
            return false;
          }
        }
      }

      return true;
    }

    private static List<IntPoint> ShiftPath(List<IntPoint> path, long dx, long dy)
      => path.Select(p => new IntPoint(p.X + dx, p.Y + dy)).ToList();

    private static void Deduct(Dictionary<int, int> pool, Dictionary<int, int> counts)
    {
      foreach (var kv in counts)
      {
        pool[kv.Key] = Math.Max(0, pool[kv.Key] - kv.Value);
      }
    }

    private static bool PoolContains(Dictionary<int, int> pool, Dictionary<int, int> counts)
    {
      foreach (var kv in counts)
      {
        if (kv.Value <= 0)
        {
          continue;
        }

        if (!pool.TryGetValue(kv.Key, out int have) || have < kv.Value)
        {
          return false;
        }
      }

      return counts.Count > 0;
    }

    private static List<IPartPlacement> ClonePlacements(List<IPartPlacement> src)
    {
      var clone = new List<IPartPlacement>(src.Count);
      foreach (var pp in src)
      {
        clone.Add(new PartPlacement(pp.Part) { X = pp.X, Y = pp.Y, Rotation = pp.Rotation, Source = pp.Source, IsMirrored = pp.IsMirrored });
      }

      return clone;
    }

    /// <summary>
    /// Post-pass: drop leftover parts INTO the holes of already-placed parts. jagua ignores item holes
    /// (so sparrow never nests inside them); this recovers that density with exact Clipper geometry â€”
    /// the spacing-dilated candidate must fit fully inside the hole and clear anything already in it.
    /// </summary>
    private static void FillHoles(List<List<IPartPlacement>> sheetLayouts, Dictionary<int, int> pool, Dictionary<int, Loaded> loadedById, CancellationToken cancel)
    {
      if (pool.Values.Sum() == 0)
      {
        return;
      }

      const double scale = 1e6; // parts are small; 1e6 keeps Clipper coords well inside its safe range
      double smallestPartArea = pool.Where(k => k.Value > 0).Select(k => Math.Abs(loadedById[k.Key].Nfp.Area)).DefaultIfEmpty(double.MaxValue).Min();

      try
      {
        for (int si = 0; si < sheetLayouts.Count && pool.Values.Sum() > 0; si++)
        {
          // Every hole on this sheet (sheet coords, biggest first â€” big parts into big holes).
          var holes = new List<List<IntPoint>>();
          foreach (var pp in sheetLayouts[si].ToList())
          {
            var children = pp.PlacedPart.Children;
            if (children == null)
            {
              continue;
            }

            foreach (var child in children)
            {
              if (child.Points.Length >= 3)
              {
                var path = DeepNestClipper.ScaleUpPath(child.Points, scale);
                if (Math.Abs(Clipper.Area(path)) / (scale * scale) >= smallestPartArea)
                {
                  holes.Add(path);
                }
              }
            }
          }

          holes.Sort((a, b) => Math.Abs(Clipper.Area(b)).CompareTo(Math.Abs(Clipper.Area(a))));

          foreach (var hole in holes)
          {
            cancel.ThrowIfCancellationRequested();
            if (pool.Values.Sum() == 0)
            {
              break;
            }

            var occupants = new List<List<IntPoint>>();
            double seedEps = 1e-6 * scale * scale;

            // Seed with parts ALREADY sitting inside this hole (sparrow's surrogate collision can nest a
            // few into thin-walled parts on its own) so we don't drop new parts on top of them.
            foreach (var existing in sheetLayouts[si].ToList())
            {
              var epath = DeepNestClipper.ScaleUpPath(existing.PlacedPart.Points, scale);
              if (ContainedIn(epath, hole, seedEps))
              {
                double eff = loadedById.TryGetValue(existing.Source, out var el) ? el.FitSpacing : 0;
                var grown = OffsetOutward(existing.PlacedPart, eff / 2.0);
                occupants.Add(DeepNestClipper.ScaleUpPath(grown.Points, scale));
              }
            }

            var hb = BoundsOfPath(hole);
            bool progress = true;
            while (progress)
            {
              progress = false;
              foreach (int src in pool.Where(k => k.Value > 0).OrderByDescending(k => Math.Abs(loadedById[k.Key].Nfp.Area)).Select(k => k.Key).ToList())
              {
                var loaded = loadedById[src];
                foreach (int rot in loaded.Angles)
                {
                  var rotated = Math.Abs(rot % 360) < 1e-9 ? loaded.Nfp : loaded.Nfp.Rotate(rot);

                  // Fit on the TOOLING footprint (kerf + lead-ins), not the bare outline â€” a part dropped
                  // into a hole has to keep its cut clear of the surrounding part just like any other.
                  var forFit = loaded.Tooling ?? loaded.Nfp;
                  // FitSpacing, not EffSpacing: like the engine, this grows ONE polygon and then measures it
                  // against whatever is already in the hole, so it cannot ask for a clearance that depends
                  // on the neighbour. Using the nominal spacing here would fit fewer parts into the holes of
                  // an all-common-cut job than before, which is a silent loss of material.
                  var dil = OffsetOutward(Math.Abs(rot % 360) < 1e-9 ? forFit : forFit.Rotate(rot), loaded.FitSpacing / 2.0);
                  if ((dil.MaxX - dil.MinX) > (hb.MaxX - hb.MinX) / scale || (dil.MaxY - dil.MinY) > (hb.MaxY - hb.MinY) / scale)
                  {
                    continue;
                  }

                  if (TryFitInHole(dil, hole, hb, occupants, scale, out double tx, out double ty))
                  {
                    sheetLayouts[si].Add(new PartPlacement(rotated) { X = tx, Y = ty, Rotation = rot, Source = src, IsMirrored = loaded.Mirrored });
                    occupants.Add(TranslatePath(DeepNestClipper.ScaleUpPath(dil.Points, scale), (long)Math.Round(tx * scale), (long)Math.Round(ty * scale)));
                    pool[src]--;
                    progress = true;
                    break;
                  }
                }

                if (progress)
                {
                  break;
                }
              }
            }
          }
        }
      }
      catch (OperationCanceledException)
      {
        throw;
      }
      catch (Exception)
      {
        // Hole-filling is a best-effort bonus â€” never let a geometry edge case fail the whole nest.
      }
    }

    /// <summary>Grid search for a translation that lands the dilated candidate fully inside the hole and
    /// clear of anything already placed there. Returns the shift (drawing units) for the ORIGINAL part.</summary>
    private static bool TryFitInHole(INfp dil, List<IntPoint> hole, (long MinX, long MinY, long MaxX, long MaxY) hb, List<List<IntPoint>> occupants, double scale, out double tx, out double ty)
    {
      tx = 0;
      ty = 0;
      var dilPath = DeepNestClipper.ScaleUpPath(dil.Points, scale);
      double dilW = dil.MaxX - dil.MinX;
      double dilH = dil.MaxY - dil.MinY;
      double hMinX = hb.MinX / scale, hMinY = hb.MinY / scale, hMaxX = hb.MaxX / scale, hMaxY = hb.MaxY / scale;
      double rangeX = Math.Max(0, hMaxX - dilW - hMinX);
      double rangeY = Math.Max(0, hMaxY - dilH - hMinY);
      const int steps = 10;
      double eps = 1e-6 * scale * scale;

      for (int iy = 0; iy <= steps; iy++)
      {
        double py = hMinY + (rangeY * iy / steps);
        for (int ix = 0; ix <= steps; ix++)
        {
          double px = hMinX + (rangeX * ix / steps);
          double txx = px - dil.MinX;
          double tyy = py - dil.MinY;
          var moved = TranslatePath(dilPath, (long)Math.Round(txx * scale), (long)Math.Round(tyy * scale));
          if (!ContainedIn(moved, hole, eps))
          {
            continue;
          }

          bool overlap = false;
          foreach (var occ in occupants)
          {
            if (Overlaps(moved, occ, eps))
            {
              overlap = true;
              break;
            }
          }

          if (!overlap)
          {
            tx = txx;
            ty = tyy;
            return true;
          }
        }
      }

      return false;
    }

    /// <summary>Robust area overlap via DIFFERENCE (handles coincident/contained polygons, where a plain
    /// Clipper intersection can return 0): overlap = area(a) âˆ’ area(a âˆ’ b).</summary>
    private static bool Overlaps(List<IntPoint> a, List<IntPoint> b, double eps)
    {
      var clipper = new Clipper();
      clipper.AddPath(a, PolyType.ptSubject, true);
      clipper.AddPath(b, PolyType.ptClip, true);
      var diff = new List<List<IntPoint>>();
      clipper.Execute(ClipType.ctDifference, diff, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
      double aArea = Math.Abs(Clipper.Area(a));
      double outsideArea = diff.Sum(p => Math.Abs(Clipper.Area(p)));
      return aArea - outsideArea > eps;
    }

    private static bool ContainedIn(List<IntPoint> candidate, List<IntPoint> hole, double eps)
    {
      var clipper = new Clipper();
      clipper.AddPath(candidate, PolyType.ptSubject, true);
      clipper.AddPath(hole, PolyType.ptClip, true);
      var sol = new List<List<IntPoint>>();
      clipper.Execute(ClipType.ctDifference, sol, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
      return sol.Sum(p => Math.Abs(Clipper.Area(p))) < eps; // nothing of the candidate lies outside the hole
    }

    private static (long MinX, long MinY, long MaxX, long MaxY) BoundsOfPath(List<IntPoint> path)
    {
      long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
      foreach (var p in path)
      {
        minX = Math.Min(minX, p.X);
        maxX = Math.Max(maxX, p.X);
        minY = Math.Min(minY, p.Y);
        maxY = Math.Max(maxY, p.Y);
      }

      return (minX, minY, maxX, maxY);
    }

    private static List<IntPoint> TranslatePath(List<IntPoint> path, long dx, long dy)
    {
      var moved = new List<IntPoint>(path.Count);
      foreach (var p in path)
      {
        moved.Add(new IntPoint(p.X + dx, p.Y + dy));
      }

      return moved;
    }

    /// <summary>Grows a polygon outward by <paramref name="offset"/> (drawing units) via a miter Clipper
    /// offset â€” the part-spacing halo. Orientation is normalised to CCW so a +delta always GROWS (a CW
    /// ring would shrink). offset â‰¤ 0 returns the polygon unchanged.</summary>
    /// <summary>
    /// The shape the engine must keep clear for one part. For a plain DXF part that is just its outline
    /// grown by half the spacing, exactly as before. A part from a SheetCam .nest is already toolpathed:
    /// the cut runs half a kerf outside the outline, and its lead-ins/outs reach further still â€” on a real
    /// job the outer lead pierced 2.9 mm beyond the contour, so packing to the outline drove leads straight
    /// through the neighbouring part. Growing the outline AND the lead paths in one offset pass unions them
    /// into a single footprint (the leads meet the contour, so the result stays connected).
    /// <para>Only the packing shape changes â€” the rendered, reported and exported geometry stays the part.</para>
    /// </summary>
    internal static INfp ToolingFootprint(INfp poly, IReadOnlyList<IReadOnlyList<SvgPoint>> toolPaths, double kerf, double spacing)
    {
      double delta = Math.Max(0, spacing / 2.0) + (Math.Max(0, kerf) / 2.0);
      bool hasPaths = toolPaths != null && toolPaths.Any(p => p != null && p.Count >= 2);
      if (!hasPaths)
      {
        return OffsetOutward(poly, delta);
      }

      double scale = SvgNest.Config.ClipperScale;
      var outline = DeepNestClipper.ScaleUpPath(poly.Points, scale);
      if (Clipper.Area(outline) < 0)
      {
        outline.Reverse(); // ClipperOffset shrinks clockwise rings, so normalise the winding first
      }

      var co = new ClipperOffset(4, SvgNest.Config.CurveTolerance * scale);
      co.AddPath(outline, JoinType.jtMiter, EndType.etClosedPolygon);
      foreach (var path in toolPaths)
      {
        if (path == null || path.Count < 2)
        {
          continue;
        }

        // Open path: a round cap models the pierce and the beam turning a corner.
        co.AddPath(DeepNestClipper.ScaleUpPath(path.ToArray(), scale), JoinType.jtRound, EndType.etOpenRound);
      }

      var solution = new List<List<IntPoint>>();
      co.Execute(ref solution, delta * scale);
      if (solution.Count == 0)
      {
        return OffsetOutward(poly, delta);
      }

      var biggest = solution.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First();
      return new NoFitPolygon(biggest.Select(ip => new SvgPoint(ip.X / scale, ip.Y / scale)));
    }

    private static INfp OffsetOutward(INfp poly, double offset)
    {
      if (offset <= 1e-9)
      {
        return poly;
      }

      double scale = SvgNest.Config.ClipperScale;
      var path = DeepNestClipper.ScaleUpPath(poly.Points, scale);
      if (Clipper.Area(path) < 0)
      {
        path.Reverse();
      }

      var co = new ClipperOffset(4, SvgNest.Config.CurveTolerance * scale);
      co.AddPath(path, JoinType.jtMiter, EndType.etClosedPolygon);
      var solution = new List<List<IntPoint>>();
      co.Execute(ref solution, offset * scale);
      if (solution.Count == 0)
      {
        return poly;
      }

      var biggest = solution.OrderByDescending(p => Math.Abs(Clipper.Area(p))).First();
      return new NoFitPolygon(biggest.Select(ip => new SvgPoint(ip.X / scale, ip.Y / scale)));
    }

    private static string BuildJaguaJson(string name, double stripHeight, List<Loaded> batch, Dictionary<int, int> batchQty)
    {
      // jagua requires item ids to be consecutive starting at 0, so use the batch INDEX as the id (not
      // the global Source, which is sparse once priority ordering drops parts from a batch). MapAndCompact
      // translates the index back to the real Source via this same `batch` list.
      var items = batch.Select((p, idx) =>
      {
        var pts = p.Dilated.Points;
        int n = pts.Length;
        if (n > 1 && Math.Abs(pts[0].X - pts[n - 1].X) < 1e-9 && Math.Abs(pts[0].Y - pts[n - 1].Y) < 1e-9)
        {
          n--; // jagua implies closure â€” drop a repeated closing vertex
        }

        var data = new List<double[]>(n);
        for (int i = 0; i < n; i++)
        {
          data.Add(new[] { pts[i].X, pts[i].Y });
        }

        // Omit allowed_orientations entirely for "Free" parts â†’ jagua treats it as RotationRange::Continuous
        // and sparrow rotates to any angle (denser). Restricted parts keep their discrete angle set.
        var item = new Dictionary<string, object>
        {
          ["id"] = idx,
          ["demand"] = batchQty[p.Source],
          ["shape"] = new { type = "simple_polygon", data },
        };
        if (!p.Continuous)
        {
          item["allowed_orientations"] = p.Angles.Select(a => (double)a).ToArray();
        }

        return item;
      }).ToArray();

      return JsonSerializer.Serialize(new { name, strip_height = stripHeight, items });
    }

    /// <summary>How many independent sparrow searches are raced in one wave. Fixed, because the wave's
    /// composition is part of the answer, not part of the scheduling.</summary>
    private const int WaveSize = 8;

    /// <summary>Search iterations per part in the batch. An "iteration" here is one pass of sparrow's outer
    /// loop, which is coarse â€” the reference two-sheet job (40 parts on its first sheet) reaches its best
    /// result by about 20 and does not improve at 40, 70, 100, 200 or 400, while the wall clock triples.
    /// Calibrated to land on that knee, which also puts a nest back at the time the clock budget took.</summary>
    private const double IterationsPerPart = 0.5;

    /// <summary>The floor for a tiny batch, which would otherwise get a budget too small to separate.</summary>
    private const int MinIterations = 15;

    /// <summary>How many searches run at the same instant. This is the ONLY place the machine is allowed
    /// to influence a nest, and it influences only how long it takes â€” a slower machine finishes the same
    /// wave later, it does not compute a different one. SHEETNEST_MAX_CONC overrides it, which is how the
    /// "any machine, same nest" claim is actually tested rather than asserted.</summary>
    private static int Concurrency()
    {
      var env = Environment.GetEnvironmentVariable("SHEETNEST_MAX_CONC");
      if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
      {
        return n;
      }

      return Environment.ProcessorCount / 2;
    }

    /// <summary>How long a sheet's search runs, in sparrow iterations. Deliberately NOT a function of the
    /// clock or of the machine: this is the number that has to match for two computers to agree on a
    /// nest. Overridable via SHEETNEST_NEST_ITERS for calibration.</summary>
    internal static int IterationBudget(int batchParts)
    {
      var env = Environment.GetEnvironmentVariable("SHEETNEST_NEST_ITERS");
      if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
      {
        return n;
      }

      return Math.Max(MinIterations, (int)Math.Ceiling(batchParts * IterationsPerPart));
    }

    /// <summary>How many independent sparrow searches to race per sheet (best-of-N). A constant: it used
    /// to be <c>ProcessorCount >= 12 ? 3 : 2</c>, which handed a smaller machine fewer attempts and so a
    /// different â€” and on average worse â€” nest for the very same job. Overridable via
    /// SHEETNEST_NEST_TRIES for experiments. Always â‰¥2 so a single unlucky run can't stand.</summary>
    private static int NestTries()
    {
      var env = Environment.GetEnvironmentVariable("SHEETNEST_NEST_TRIES");
      if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
      {
        return Math.Min(n, 8);
      }

      return 3;
    }

    /// <summary>Best-of-K count for a batch that will pattern-replicate onto `replicas` identical sheets.
    /// A one-off sheet uses the base <see cref="NestTries"/>; a template governing many clones gets more
    /// tries (up to 16) because that cost is amortized over all the clones and drives the layout to a
    /// denser result â€” a search is one draw from a wide spread (measured: 30 to 40 parts on the same
    /// sheet across seeds), so racing more of them is what raises the floor.</summary>
    internal static int TriesForReplicas(int replicas) => Math.Clamp(replicas, NestTries(), 16);

    /// <summary>Best-of-K for a sheet: scales with its clone count (<see cref="TriesForReplicas"/>), and the
    /// single final/tail sheet gets at least 8 tries so that leftover partial sheet lands consistently too
    /// (it doesn't amortize over clones, but it's only one sheet so the extra tries are cheap).</summary>
    internal static int TriesFor(int replicas, bool isFinalSheet)
    {
      int t = TriesForReplicas(replicas);
      return isFinalSheet ? Math.Max(t, 8) : t;
    }

    /// <summary>The bounding box the placed parts actually occupy. This is what decides the offcut the
    /// operator gets, and it is measured AFTER the gravity compaction â€” sparrow's own density is measured
    /// before it, on its own strip.</summary>
    internal static (double W, double H) Extent(IReadOnlyList<IPartPlacement> placements)
    {
      if (placements == null || placements.Count == 0)
      {
        return (0, 0);
      }

      double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
      foreach (var p in placements)
      {
        minX = Math.Min(minX, p.PlacedPart.MinX);
        minY = Math.Min(minY, p.PlacedPart.MinY);
        maxX = Math.Max(maxX, p.PlacedPart.MaxX);
        maxY = Math.Max(maxY, p.PlacedPart.MaxY);
      }

      return (maxX - minX, maxY - minY);
    }

    /// <summary>Reads the strip density (0..1) from a sparrow solution JSON; 0 if it can't be parsed.</summary>
    private static double ParseDensity(string json)
    {
      try
      {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("solution").GetProperty("layout").GetProperty("density").GetDouble();
      }
      catch
      {
        return 0;
      }
    }

    /// <summary>
    /// Picks the best sheet candidate: most parts placed wins, then the SHORTEST extent along the sheet's
    /// long axis, then higher strip density, then lower seed (stable, repeatable).
    /// <para>
    /// Extent beats density because they are measured at different moments: density is sparrow's, taken on
    /// its own strip BEFORE the gravity compaction, while the extent is what is left after it â€” and the
    /// extent is what the operator sees as the leftover. Ranking by density discarded real material: on a
    /// measured sheet where all eight candidates placed the same ten parts, the density winner (0.94448)
    /// left an extent of 118.72 while a candidate 0.00002 less dense left 115.79. Worse, two candidates
    /// separated in the fifth decimal of density trade places between runs, so the same job produced a
    /// different leftover every time (reported in issue #2).
    /// </para>
    /// Density stays as the next tie-break: it still discriminates when the extents match, which is the
    /// normal case on a sheet packed full. Pure â€” unit-testable. Returns the winner's index in
    /// <paramref name="cands"/>, or -1 if empty.
    /// </summary>
    internal static int PickBest(IReadOnlyList<(int Count, double Extent, double Density, int Seed)> cands)
    {
      int best = -1;
      foreach (var (c, i) in cands.Select((v, i) => (v, i)))
      {
        if (best < 0)
        {
          best = i;
          continue;
        }

        var b = cands[best];
        if (c.Count != b.Count)
        {
          if (c.Count > b.Count)
          {
            best = i;
          }

          continue;
        }

        // Extents that differ only in float noise are a tie, so the ordering falls cleanly through to
        // density instead of being decided by the last bits of two equivalent layouts.
        double eps = 1e-6 * Math.Max(1, Math.Max(Math.Abs(c.Extent), Math.Abs(b.Extent)));
        if (Math.Abs(c.Extent - b.Extent) > eps)
        {
          if (c.Extent < b.Extent)
          {
            best = i;
          }

          continue;
        }

        if (c.Density > b.Density
          || (c.Density == b.Density && c.Seed < b.Seed))
        {
          best = i;
        }
      }

      return best;
    }

    /// <summary>Runs one sparrow search. The search is bounded by a fixed number of iterations, NEVER by
    /// the clock: a wall-clock deadline makes the same job land differently depending on how busy the
    /// machine is, which is what made a repeated nest come back with a different offcut every time.
    /// <paramref name="hardTimeoutSec"/> is only a watchdog for a hung process â€” if it ever fires the
    /// result is discarded rather than used, because a truncated search is not reproducible.</summary>
    private static string RunSparrowOnce(string exe, string workDir, string inputPath, int iterations, int hardTimeoutSec, int seed, CancellationToken cancel, Action<double>? onDensity, out string error)
    {
      error = null;
      var psi = new ProcessStartInfo
      {
        FileName = exe,
        WorkingDirectory = workDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      psi.ArgumentList.Add("-i");
      psi.ArgumentList.Add(inputPath);
      psi.ArgumentList.Add("--max-iterations"); // deterministic budget; also forces the failure-based
      psi.ArgumentList.Add(iterations.ToString(CultureInfo.InvariantCulture)); // compression decay
      psi.ArgumentList.Add("-x"); // stop a search that is stuck (counts failures, not seconds)
      psi.ArgumentList.Add("-s"); // fixed RNG seed â†’ repeatable per-seed run for best-of-N
      psi.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));

      Process proc;
      try
      {
        proc = Process.Start(psi);
      }
      catch (Exception ex)
      {
        error = "Could not start the nesting engine: " + ex.Message;
        return null;
      }

      // Stream both streams line by line and pull the live density ("... dens: 88.9% ...") out so the
      // UI bar can fill DURING the run (each sheet is otherwise an opaque several-second black box).
      void OnLine(string line)
      {
        if (line == null || onDensity == null)
        {
          return;
        }

        int i = line.IndexOf("dens:", StringComparison.Ordinal);
        if (i < 0)
        {
          return;
        }

        i += 5;
        int end = line.IndexOf('%', i);
        if (end > i && double.TryParse(line.Substring(i, end - i).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
          onDensity(Math.Max(0, Math.Min(1, d / 100.0)));
        }
      }

      using (proc)
      {
        proc.OutputDataReceived += (s, e) => OnLine(e.Data);
        proc.ErrorDataReceived += (s, e) => OnLine(e.Data);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        int waitMs = Math.Max(60, hardTimeoutSec) * 1000;
        using (cancel.Register(() => TryKill(proc)))
        {
          bool exited = proc.WaitForExit(waitMs);
          if (cancel.IsCancellationRequested)
          {
            error = "Cancelled.";
            return null;
          }

          if (!exited)
          {
            TryKill(proc);
            error = "The nesting engine timed out.";
            return null;
          }
        }

        string outFile = Path.Combine(workDir, "output", "final_job.json");
        if (!File.Exists(outFile))
        {
          error = $"The nesting engine produced no solution (exit {proc.ExitCode}).";
          return null;
        }

        return File.ReadAllText(outFile);
      }
    }

    private static void TryKill(Process proc)
    {
      try
      {
        proc.Kill(true);
      }
      catch (InvalidOperationException)
      {
      }
    }
  }
}
