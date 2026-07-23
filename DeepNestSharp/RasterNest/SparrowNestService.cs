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
    private sealed class Loaded
    {
      public int Source;
      public INfp Nfp;        // original (rendered) geometry
      public INfp Dilated;    // grown by spacing/2 — what sparrow packs
      public int[] Angles;    // allowed orientations (discrete fallback + used by hole-filling)
      public bool Continuous; // "Free" rotation → send NO orientation list so sparrow rotates continuously
      public double EffSpacing;
      public int Qty;
      public bool Mirrored;   // this population is X-flipped — placements carry IsMirrored for the exporter
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
    /// the stock is exhausted; whatever cannot fit is returned as unplaced.</summary>
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

      var loaded = LoadAll(parts, rotations, spacing, out error);
      if (loaded == null || loaded.Count == 0)
      {
        error ??= "No valid parts to nest.";
        return null;
      }

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

        // Adaptive time budget: a sheet with few parts converges quickly, so don't spend the full cap on
        // it (matters for the sparse tail sheet and for many small sheets). Early termination (-x) still
        // trims sheets that plateau even sooner. The density curve knees ~6s for a full sheet (~26 parts).
        int batchParts = batchQty.Values.Sum();
        int budget = Math.Max(3, Math.Min(perSheetBudgetSec, (int)Math.Ceiling(batchParts * 0.22)));

        // How many identical sheets this exact batch will tile (it is packed ONCE then pattern-replicated).
        // A batch that governs many clones is worth many best-of-K tries — the cost is amortized over all
        // the clones — which makes the template sheet converge to a consistent, dense layout (more time
        // does NOT reduce sparrow's run-to-run variance, but more tries do).
        int replicas = batchQty.Count == 0 ? 1 : batchQty.Min(kv => pool[kv.Key] / Math.Max(1, kv.Value));

        // The final/tail sheet takes the whole remaining pool (a leftover count that won't fill a sheet).
        // It is a single one-off — replicas=1 → base tries → it varies run-to-run. Since it's just ONE
        // sheet, invest more best-of-K in it so it lands consistently, like the replicated body already does.
        bool isFinalSheet = batchParts == pool.Values.Sum();
        int tries = TriesFor(replicas, isFinalSheet);

        // packW/packH = the sheet dims PackOneSheet packed in (always the stock slot's own orientation — the
        // sheet is never auto-rotated). Kept as a return value so the render matches what was packed.
        var (placements, placedBySrc, packW, packH) = PackOneSheet(loaded, batchQty, w, h, margin, budget, tries, sparrowExePath, cancel, onDensity, out string perr);
        if (cancel.IsCancellationRequested)
        {
          error = "Cancelled.";
          return null;
        }

        if (placements == null || placements.Count == 0)
        {
          // Nothing fit this sheet size (a part is bigger than it) — try the next slot/size.
          slot++;
          continue;
        }

        sheetLayouts.Add(placements);
        sheetSizes.Add((packW, packH));
        Deduct(pool, placedBySrc);
        slot++;

        // Pattern replication: while the pool still holds the SAME composition and the next slots are
        // the same size, clone this layout instead of re-nesting (uniform jobs → 1 run + replicate).
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
      FillHoles(sheetLayouts, pool, loadedById, cancel);

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

    /// <summary>Loads each part's contour once (arcs tessellated by the app pipeline) and pre-computes its
    /// spacing-dilated shell + allowed orientations. Holes (INfp.Children) are not yet forwarded.</summary>
    private static List<Loaded> LoadAll(IReadOnlyList<RasterPartInfo> parts, int rotations, double spacing, out string error)
    {
      error = null;
      var helper = new NestExecutionHelper();
      var loaded = new List<Loaded>();
      int source = 0;
      foreach (var part in parts)
      {
        if (part == null || string.IsNullOrWhiteSpace(part.Path) || part.Quantity <= 0)
        {
          continue;
        }

        var det = helper.LoadRawDetail(new FileInfo(part.Path));
        if (det != null && det.TryConvertToNfp(source, out INfp nfp) && nfp.Points.Length > 2)
        {
          if (part.Mirrored)
          {
            // Bake the mirror into the RENDERED geometry; the placement also carries IsMirrored so the
            // DXF exporter (which reloads the ORIGINAL file) mirrors it once too — same order the
            // exporter uses (MirrorX about origin → rotate → translate), so both paths agree.
            nfp = nfp.MirrorX();
          }

          int code = part.Rotations > 0 ? part.Rotations : rotations;
          double effSpacing = part.Spacing >= 0 ? part.Spacing : Math.Max(0, spacing);
          loaded.Add(new Loaded
          {
            Source = source,
            Nfp = nfp,
            Dilated = OffsetOutward(nfp, effSpacing / 2.0),
            Angles = RotationCodes.PermittedSet(code),
            Continuous = code == 36, // only "Free" (sentinel 36) is continuous; 1001/1002/1003 are discrete sets
            EffSpacing = effSpacing,
            Qty = part.Quantity,
            Mirrored = part.Mirrored,
            Priority = part.Priority,
          });
          source++;
        }
      }

      return loaded;
    }

    /// <summary>Picks a batch from the pool up to ~1.3× the sheet area (extra choice for sparrow), keeping
    /// source variety for mixed jobs. Always returns at least one part so an oversize part gets a try.</summary>
    private static Dictionary<int, int> SelectBatch(Dictionary<int, int> pool, Dictionary<int, Loaded> loadedById, int w, int h)
    {
      double cap = 1.3 * w * h;
      double sheetArea = (double)w * h;
      var batch = new Dictionary<int, int>();
      double acc = 0;
      int prevPriority = int.MinValue;

      // Highest priority first (1 = highest, lower number wins) so preferred parts fill earlier
      // sheets and the leftover tail — what ends up unplaced when material runs out — is lowest priority.
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
        // Nothing fit the area cap — force the single highest-priority remaining part onto the sheet.
        var first = pool.Where(k => k.Value > 0)
          .OrderBy(k => loadedById[k.Key].Priority).ThenBy(k => k.Key).First();
        batch[first.Key] = 1;
      }

      return batch;
    }

    /// <summary>Packs one batch onto one sheet, best-of-K with EARLY-STOP: races sparrow searches (fixed seeds)
    /// in waves and keeps the candidate that PLACES THE MOST parts (tie: denser, then lower seed). Stops
    /// launching more tries as soon as a whole wave fails to improve the best part-count — easy/uniform sheets
    /// converge in a wave or two, so they don't burn all `tries`; hard/variable sheets keep going up to `tries`.
    /// Packs ONLY in the user's chosen sheet orientation — the sheet is never auto-rotated (a 120×60 preset must
    /// render 120×60); to use the other orientation the operator picks that preset.</summary>
    private static (List<IPartPlacement> Placements, Dictionary<int, int> PlacedBySource, int PackW, int PackH) PackOneSheet(
      List<Loaded> loaded, Dictionary<int, int> batchQty, int sheetW, int sheetH, double margin, int budget, int tries, string exe, CancellationToken cancel, Action<double> onDensity, out string error)
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

        // Concurrency cap: sparrow is multi-threaded but scales sub-linearly, so several run at once for
        // almost the same wall-clock (measured on 20 cores: 1 run 4.3s, 8 parallel 5.2s). One wave = maxConc.
        int maxConc = Math.Max(2, Math.Min(tries, Environment.ProcessorCount / 3));

        var winners = new List<(List<IPartPlacement> Pl, Dictionary<int, int> By, int Count, double Density, int Seed)>();
        int bestCount = -1, launched = 0;

        while (launched < tries && !cancel.IsCancellationRequested)
        {
          int waveSize = Math.Min(maxConc, tries - launched);
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
            string outJson = RunSparrowOnce(exe, tryDir, inputPath, budget, seed, cancel, aggDensity, out _);
            if (outJson == null || cancel.IsCancellationRequested)
            {
              return;
            }

            var (pl, by) = MapAndCompact(outJson, batch, sheetW, sheetH, margin, cancel);
            wPl[j] = pl;
            wBy[j] = by;
            wSt[j] = (pl.Count, ParseDensity(outJson), seed);
          });

          launched += waveSize;
          for (int j = 0; j < waveSize; j++)
          {
            if (wSt[j].HasValue)
            {
              winners.Add((wPl[j], wBy[j], wSt[j].Value.Count, wSt[j].Value.Density, wSt[j].Value.Seed));
            }
          }

          // Plateau: stop once a full wave fails to beat the best part-count so far (≥1 wave already ran).
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

        int bi = PickBest(winners.Select(w => (w.Count, w.Density, w.Seed)).ToList());
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

      // Pre-anchor min → margin so compaction starts on-sheet, then gravity-compact to the corner.
      double minX = all.Min(p => p.PlacedPart.MinX);
      double minY = all.Min(p => p.PlacedPart.MinY);
      foreach (var pp in all)
      {
        pp.X += margin - minX;
        pp.Y += margin - minY;
      }

      var compactItems = all.Select(pp => new CompactItem
      {
        Poly = pp.Part,
        X = pp.X,
        Y = pp.Y,
        Spacing = spacingById.TryGetValue(pp.Source, out double sp) ? sp : 0,
      }).ToList();
      RasterCompact.Compact(compactItems, sheetWin, sheetHin, margin, cancel: cancel);
      for (int k = 0; k < all.Count; k++)
      {
        all[k].X = compactItems[k].X;
        all[k].Y = compactItems[k].Y;
      }

      const double tol = 1e-3;
      var placements = new List<IPartPlacement>();
      var placedBySource = new Dictionary<int, int>();
      foreach (var pp in all)
      {
        var ppp = pp.PlacedPart;
        if (ppp.MinX < -tol || ppp.MinY < -tol || ppp.MaxX > sheetWin + tol || ppp.MaxY > sheetHin + tol)
        {
          continue; // past the sheet edge → not placed on this sheet (stays in the pool)
        }

        placements.Add(pp);
        placedBySource[pp.Source] = placedBySource.TryGetValue(pp.Source, out int n) ? n + 1 : 1;
      }

      return (placements, placedBySource);
    }

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
    /// (so sparrow never nests inside them); this recovers that density with exact Clipper geometry —
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
          // Every hole on this sheet (sheet coords, biggest first — big parts into big holes).
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
                double eff = loadedById.TryGetValue(existing.Source, out var el) ? el.EffSpacing : 0;
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
                  var dil = OffsetOutward(rotated, loaded.EffSpacing / 2.0);
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
        // Hole-filling is a best-effort bonus — never let a geometry edge case fail the whole nest.
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
    /// Clipper intersection can return 0): overlap = area(a) − area(a − b).</summary>
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
    /// offset — the part-spacing halo. Orientation is normalised to CCW so a +delta always GROWS (a CW
    /// ring would shrink). offset ≤ 0 returns the polygon unchanged.</summary>
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
          n--; // jagua implies closure — drop a repeated closing vertex
        }

        var data = new List<double[]>(n);
        for (int i = 0; i < n; i++)
        {
          data.Add(new[] { pts[i].X, pts[i].Y });
        }

        // Omit allowed_orientations entirely for "Free" parts → jagua treats it as RotationRange::Continuous
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

    /// <summary>How many independent sparrow searches to race per sheet (best-of-N). ≥12 logical cores →
    /// 3, otherwise 2; overridable via SHEETNEST_NEST_TRIES. Always ≥2 so a single unlucky run can't stand.</summary>
    private static int NestTries()
    {
      var env = Environment.GetEnvironmentVariable("SHEETNEST_NEST_TRIES");
      if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
      {
        return Math.Min(n, 8);
      }

      return Environment.ProcessorCount >= 12 ? 3 : 2;
    }

    /// <summary>Best-of-K count for a batch that will pattern-replicate onto `replicas` identical sheets.
    /// A one-off sheet uses the base <see cref="NestTries"/>; a template governing many clones gets more
    /// tries (up to 16) because that cost is amortized over all the clones and drives the layout to a
    /// consistent, dense result (more tries reduce sparrow's variance where more TIME does not).</summary>
    internal static int TriesForReplicas(int replicas) => Math.Clamp(replicas, NestTries(), 16);

    /// <summary>Best-of-K for a sheet: scales with its clone count (<see cref="TriesForReplicas"/>), and the
    /// single final/tail sheet gets at least 8 tries so that leftover partial sheet lands consistently too
    /// (it doesn't amortize over clones, but it's only one sheet so the extra tries are cheap).</summary>
    internal static int TriesFor(int replicas, bool isFinalSheet)
    {
      int t = TriesForReplicas(replicas);
      return isFinalSheet ? Math.Max(t, 8) : t;
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

    /// <summary>Picks the best sheet candidate: most parts placed wins, then higher strip density, then lower
    /// seed (stable, repeatable). Pure — unit-testable. Returns the winner's index in <paramref name="cands"/>,
    /// or -1 if empty.</summary>
    internal static int PickBest(IReadOnlyList<(int Count, double Density, int Seed)> cands)
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
        if (c.Count > b.Count
          || (c.Count == b.Count && c.Density > b.Density)
          || (c.Count == b.Count && c.Density == b.Density && c.Seed < b.Seed))
        {
          best = i;
        }
      }

      return best;
    }

    private static string RunSparrowOnce(string exe, string workDir, string inputPath, int timeLimitSec, int seed, CancellationToken cancel, Action<double>? onDensity, out string error)
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
      psi.ArgumentList.Add("-t");
      psi.ArgumentList.Add(timeLimitSec.ToString(CultureInfo.InvariantCulture));
      psi.ArgumentList.Add("-x"); // early termination: stop as soon as the search plateaus
      psi.ArgumentList.Add("-s"); // fixed RNG seed → repeatable per-seed run for best-of-N
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

        int waitMs = (timeLimitSec + 60) * 1000;
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
