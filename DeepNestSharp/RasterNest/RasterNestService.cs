namespace DeepNestSharp.RasterNest
{
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.Placement;

  /// <summary>Plain per-part nesting data handed to the raster engine (background-thread safe).</summary>
  internal sealed class RasterPartInfo
  {
    public string Path;
    public int Quantity;        // total to nest (required + extra)
    public int Rotations = -1;  // per-part allowed rotations; -1 = use the job's global setting
    public int Priority = 5;    // 0-10, higher nests first
    public double Spacing = -1; // per-part gap to neighbours (in); 0 = common-line (touching); -1 = job default
  }

  /// <summary>
  /// Raster nesting wired into the app: nest the given parts + sheet with the raster engine and build a
  /// standard <see cref="INestResult"/> so it shows in the existing viewer and production plan. Takes
  /// plain data (not UI-bound objects) so it can run on a background thread. The NFP engine is untouched.
  /// OPTIMIZATION — best-of rotation profiles: greedy bottom-left packing is sensitive to WHICH rotation
  /// set it may use (e.g. interlocking parts pack ~10% denser at {0,180} than at 90° steps, and a part
  /// drawn at an odd angle packs tighter when first straightened to its min-bounding-box). The raster
  /// engine is fast, so we nest the SAME job with several candidate profiles — each bounded by every
  /// part's own permitted rotations from Edit Part, so a grain-restricted part is never rotated beyond
  /// what the operator allowed — in parallel, and keep the best result (fewest unplaced, then fewest
  /// sheets, then the lowest strip on the last sheet = biggest reusable remnant). Never worse than the
  /// plain single-profile nest, which is always one of the candidates.
  /// </summary>
  internal static class RasterNestService
  {
    public static INestResult Nest(
      IReadOnlyList<RasterPartInfo> partInfos,
      int sheetWin,
      int sheetHin,
      int sheetCount,
      PlacementTypeEnum placementType,
      int rotations,
      double spacing,
      double margin,
      double pxPerInch,
      out string error)
    {
      int maxSheets = System.Math.Max(0, sheetCount);
      error = null;
      var helper = new NestExecutionHelper();

      var parsed = new List<(int Src, INfp Nfp, int Qty, int[] Allowed, int Priority, double SpacingIn)>();
      int src = 0;
      foreach (var part in partInfos)
      {
        if (part == null || string.IsNullOrWhiteSpace(part.Path))
        {
          continue;
        }

        var det = helper.LoadRawDetail(new FileInfo(part.Path));
        if (det != null && det.TryConvertToNfp(src, out INfp nfp) && nfp.Points.Length > 2)
        {
          double effSpacing = part.Spacing >= 0 ? part.Spacing : System.Math.Max(0, spacing);
          parsed.Add((src, nfp, part.Quantity, PermittedSet(part.Rotations > 0 ? part.Rotations : rotations), part.Priority, effSpacing));
          src++;
        }
      }

      if (parsed.Count == 0)
      {
        error = "No valid parts to nest.";
        return null;
      }

      if (sheetWin <= 0 || sheetHin <= 0)
      {
        error = "Add a sheet size first.";
        return null;
      }

      // Mutable resolution: the selection machinery below reads `px` (and the px-derived sizes), so
      // the tight-pack retry can re-run everything at a higher resolution when the default is too
      // coarse to use a physically-fitting gap.
      double px = pxPerInch;
      int sw = (int)(sheetWin * px);
      int sh = (int)(sheetHin * px);

      // PER-PART spacing → each part's mask dilates by its own spacing/2, so two parts end up
      // (spacingA + spacingB)/2 apart. COMMON-LINE parts keep a MINIMUM 1px halo: placements born
      // literally touching can jam in chains anchored against both sheet edges that no later
      // separation can open — born 2px apart they always have slack, and the exact compaction then
      // closes them down to the CAM-safe mini-gap (RasterCompact.CommonLineGap). Clamps keep an
      // over-large value (units mistake) from blowing the mask size up to gigabytes.
      int marginPx = (int)System.Math.Round(margin * px);
      int halfSheet = System.Math.Max(1, System.Math.Min(sw, sh) / 2);
      marginPx = System.Math.Max(0, System.Math.Min(marginPx, halfSheet));
      var halos = parsed
        .Select(p => p.SpacingIn <= 0
          ? 1
          : System.Math.Max(1, System.Math.Min((int)System.Math.Round((p.SpacingIn / 2.0) * px), halfSheet)))
        .ToArray();

      // Straighten angle per part (min-area bounding box) — used by the straightened profiles. It
      // introduces OFF-AXIS angles, so (Radan semantics) only parts whose permission is 45°-step or
      // freer get it: "four orientations permitted" means literally those four of the drawing.
      var straighten = parsed.Select(p => p.Allowed.Length >= 8 ? MinBoundingBoxAngle(p.Nfp) : 0).ToArray();

      var candidates = BuildCandidates(parsed.Select(p => p.Allowed).ToArray(), straighten);

      bool growX = sw > sh; // pack grows along the longer sheet axis (keeps the remnant a short-dim strip)

      // Nest a set of quantities (one per parsed part, by index) with EVERY candidate profile in
      // parallel, on at most <paramref name="allowedSheets"/> sheets (the Sheets tab quantity — what
      // doesn't fit is reported unplaced). Used for the full job, the pattern search and the leftover.
      (JobResult Job, List<PartType> Types)[] NestAll(int[] quantities, int allowedSheets)
      {
        var results = new (JobResult Job, List<PartType> Types)[candidates.Count];
        System.Threading.Tasks.Parallel.For(0, candidates.Count, ci =>
        {
          var candidateTypes = parsed
            .Select((p, i) => new PartType
            {
              Source = p.Src,
              Poly = p.Nfp,
              Quantity = quantities[i],
              Priority = p.Priority,
              RotationsDeg = candidates[ci][i],
              HaloPx = halos[i],
            })
            .ToList();
          results[ci] = (RasterJobNester.Nest(candidateTypes, sw, sh, px, marginPx, allowedSheets), candidateTypes);
        });

        return results;
      }

      // Best result across the candidates: fewest unplaced → fewest sheets → shortest strip → tidiest.
      (JobResult Job, List<PartType> Types) NestBest(int[] quantities, int allowedSheets)
      {
        var results = NestAll(quantities, allowedSheets);
        int bestIdx = 0;
        for (int ci = 1; ci < results.Length; ci++)
        {
          if (IsBetter(results[ci], results[bestIdx], growX))
          {
            bestIdx = ci;
          }
        }

        return results[bestIdx];
      }

      // PATTERN MODE cap (used by SelectJob and by the escalation gate below): a big single-part run
      // is planned as "k × best pattern sheet + remainder", so the pattern search nests only ~2.5
      // sheets' worth of parts per candidate regardless of demand (greedy-nesting ALL of them per
      // candidate took minutes for an 800-part run).
      double partArea = System.Math.Abs(parsed[0].Nfp.NetArea);
      int patternCap = partArea <= 0 ? int.MaxValue : (int)System.Math.Ceiling(2.5 * sheetWin * sheetHin / partArea);
      bool patternMode = parsed.Count == 1 && parsed[0].Qty > patternCap && patternCap >= 2;

      // Full job selection (pattern mode + best-of candidates + replication) for the CURRENT
      // `halos` array. A local function so the tight-pack retry below can re-run the whole
      // selection with zero halos for the common-line parts. The tail sweep is NOT in here — it
      // runs once on the finally adopted result (see TailSweep below).
      (JobResult Job, List<PartType> Types) SelectJob()
      {
        JobResult job;
        List<PartType> types;

        // PATTERN MODE for big single-part production runs: for a single geometry the EXACT ranking
        // is simply "most parts per pattern sheet".
        if (patternMode)
        {
          int fullQty = parsed[0].Qty;

          // The probes are ranked purely by SHEET-0 count, and the greedy fills sheet 0 identically
          // as long as the probe quantity exceeds one sheet's capacity (the area bound guarantees
          // 1.3 sheets' worth does) — so probing 2.5 sheets' worth was oversized, ~2× the work for
          // the exact same pattern.
          int probeQty = partArea <= 0
            ? patternCap
            : System.Math.Min(patternCap, (int)System.Math.Ceiling(1.3 * sheetWin * sheetHin / partArea));
          var probes = NestAll(new[] { probeQty }, System.Math.Min(3, System.Math.Max(1, maxSheets)));

          int bestIdx = -1;
          int bestCount = -1;
          int bestVariety = int.MaxValue;
          for (int ci = 0; ci < probes.Length; ci++)
          {
            if (probes[ci].Job.NotPlaced > 0 && probes[ci].Job.Sheets == 0)
            {
              continue; // part doesn't fit the sheet at all under this profile
            }

            var s0 = probes[ci].Job.Placements.Where(p => p.Sheet == 0).ToList();
            int variety = s0.Select(p => p.RotationDeg).Distinct().Count();
            if (s0.Count > bestCount || (s0.Count == bestCount && variety < bestVariety))
            {
              bestIdx = ci;
              bestCount = s0.Count;
              bestVariety = variety;
            }
          }

          if (bestIdx >= 0 && bestCount > 0)
          {
            var pattern = probes[bestIdx];
            var sheet0 = pattern.Job.Placements.Where(p => p.Sheet == 0).ToList();
            int k = System.Math.Min(fullQty / bestCount, maxSheets);
            int leftover = fullQty - (k * bestCount);
            int tailSheets = maxSheets - k;
            JobResult tail = leftover > 0 && tailSheets > 0 ? NestBest(new[] { leftover }, tailSheets).Job : null;
            int notPlaced = tail != null ? tail.NotPlaced : leftover;

            job = ComposeReplicated(sheet0, k, tail, notPlaced);
            types = pattern.Types;
          }
          else
          {
            (job, types) = NestBest(new[] { fullQty }, maxSheets);
          }
        }
        else
        {
          (job, types) = NestBest(parsed.Select(p => p.Qty).ToArray(), maxSheets);

          // Industrial pattern replication: repeat sheet 0's layout as many whole times as the demand
          // allows and RE-NEST the leftover parts on their own sheet(s). The shop then cuts "k× the same
          // layout + 1 remainder" — and the remainder is a real dense nest, not a display trick. Only
          // kept when it needs no more sheets than the greedy result.
          job = TryReplicatePattern(job, types, parsed.Count, maxSheets, NestBest);
        }

        return (job, types);
      }

      // The user-visible remnant lives on the LAST sheet: re-pack it minimizing the used strip. The
      // greedy's lowest-corner rule happily stacks parts ON TOP of the pack even when that stretches
      // the strip — turning the last few parts 90° at the strip's end is often shorter (user-reported:
      // 20 rails at 108.75" vs 101.5"). Applied only when it strictly shortens the strip. Runs ONCE on
      // the finally ADOPTED result (the sweep never changes NotPlaced/Sheets — all the tight-pack
      // retry compares — so sweeping inside every selection pass was pure wasted wall-time).
      JobResult TailSweep(JobResult job)
      {
        if (job.Sheets > 0)
        {
          double ExtentIn(IEnumerable<JobPlacement> pls)
          {
            double max = 0;
            foreach (var p in pls)
            {
              var e = parsed.First(q => q.Src == p.Source);
              var r = p.RotationDeg == 0 ? e.Nfp : e.Nfp.Rotate(p.RotationDeg);
              double size = growX ? (r.MaxX - r.MinX) : (r.MaxY - r.MinY);
              max = System.Math.Max(max, (growX ? p.Xpx : p.Ypx) + (size * px));
            }

            return max;
          }

          int last = job.Sheets - 1;
          var tailPl = job.Placements.Where(p => p.Sheet == last).ToList();
          var tailSources = tailPl.Select(p => p.Source).Distinct().ToList();

          // Only a GENUINE remainder is repacked: reshaping a replicated pattern copy would break the
          // "cut k× the same layout" plan, and full sheets are already dense.
          bool genuineTail = job.Sheets == 1;
          if (!genuineTail)
          {
            var c0 = job.Placements.Where(p => p.Sheet == 0).GroupBy(p => p.Source).ToDictionary(g => g.Key, g => g.Count());
            var cL = tailPl.GroupBy(p => p.Source).ToDictionary(g => g.Key, g => g.Count());
            genuineTail = c0.Count != cL.Count || c0.Any(kv => !cL.TryGetValue(kv.Key, out int v) || v != kv.Value);
          }

          // Single-part tails admit an exhaustive orientation-split sweep: n−k parts in one axis pair +
          // k in the other, greedy-packed in that order, for every k — that finds "16 flat + 4 turned"
          // arrangements no single greedy pass can (its lowest-corner rule prefers stacking on top).
          if (genuineTail && tailPl.Count >= 2 && tailPl.Count <= 80 && tailSources.Count == 1)
          {
            // Axis pairs come from the part's PERMITTED rotations (Edit Part), not from whichever
            // profile won the job — the winner may have restricted itself to one axis.
            int tailIdx = parsed.FindIndex(q => q.Src == tailSources[0]);
            var pe = parsed[tailIdx];
            var t0 = new PartType { Source = pe.Src, Poly = pe.Nfp, HaloPx = halos[tailIdx] };
            int[] setA = pe.Allowed.Where(a => a == 90 || a == 270).ToArray();
            int[] setB = pe.Allowed.Where(a => a == 0 || a == 180).ToArray();

            // Rotation-symmetric parts (circles, squares) gain nothing from turning — the sweep would
            // burn ~2n nests for identical layouts. Compare the 0° and 90° masks once and skip if equal.
            bool rotationMatters = false;
            if (setA.Length > 0 && setB.Length > 0)
            {
              var m0 = RasterUtil.Dilate(RasterUtil.Rasterize(pe.Nfp, px), t0.HaloPx);
              var m90 = RasterUtil.Dilate(RasterUtil.Rasterize(pe.Nfp.Rotate(90), px), t0.HaloPx);
              rotationMatters = m0.W != m90.W || m0.H != m90.H
                || !System.MemoryExtensions.SequenceEqual<bool>(m0.Bits, m90.Bits);
            }

            if (setA.Length > 0 && setB.Length > 0 && rotationMatters)
            {
              int n = tailPl.Count;
              JobResult bestTail = null;
              double bestExtent = ExtentIn(tailPl) - 0.5;

              // The 2×(n+1) split attempts are independent — nest them in PARALLEL, then scan in the
              // original enumeration order so the winner is exactly the one the sequential loop kept
              // (first strict improvement). This was the dominant sequential cost of a nest run.
              var combos = new List<(int[] First, int[] Second, int K)>();
              foreach (var (firstSet, secondSet) in new[] { (setA, setB), (setB, setA) })
              {
                for (int k = 0; k <= n; k++)
                {
                  combos.Add((firstSet, secondSet, k));
                }
              }

              var attempts = new (JobResult Job, double Extent)[combos.Count];
              System.Threading.Tasks.Parallel.For(0, combos.Count, ci =>
              {
                var (firstSet, secondSet, k) = combos[ci];
                var splitTypes = new List<PartType>();
                if (n - k > 0)
                {
                  splitTypes.Add(new PartType { Source = t0.Source, Poly = t0.Poly, Quantity = n - k, RotationsDeg = firstSet, HaloPx = t0.HaloPx, Priority = 6 });
                }

                if (k > 0)
                {
                  splitTypes.Add(new PartType { Source = t0.Source, Poly = t0.Poly, Quantity = k, RotationsDeg = secondSet, HaloPx = t0.HaloPx, Priority = 5 });
                }

                var attempt = RasterJobNester.Nest(splitTypes, sw, sh, px, marginPx, 1);
                attempts[ci] = attempt.NotPlaced == 0 && attempt.Placements.Count == n
                  ? (attempt, ExtentIn(attempt.Placements))
                  : (null, double.MaxValue);
              });

              foreach (var (attemptJob, extent) in attempts)
              {
                if (attemptJob != null && extent < bestExtent)
                {
                  bestExtent = extent;
                  bestTail = attemptJob;
                }
              }

              if (bestTail != null)
              {
                var keep = job.Placements.Where(p => p.Sheet != last).ToList();
                foreach (var p in bestTail.Placements)
                {
                  keep.Add(new JobPlacement { Source = p.Source, Sheet = last, Xpx = p.Xpx, Ypx = p.Ypx, RotationDeg = p.RotationDeg });
                }

                job = new JobResult
                {
                  Placements = keep,
                  Sheets = job.Sheets,
                  NotPlaced = job.NotPlaced,
                  NoOverlap = job.NoOverlap && bestTail.NoOverlap,
                };
              }
            }
          }
        }

        return job;
      }

      // Gap vetting for tight-packed (halo-0) layouts — used by the retry adoption below AND to
      // re-verify the swept tail: compact a COPY of each sheet exactly like the final build will,
      // then require every common-line pair to keep at least half the CAM-safe mini-gap.
      bool TightGapsOk(JobResult tj, int onlySheet = -1)
      {
        var seenLayouts = new HashSet<string>();
        foreach (var sheetPl in tj.Placements.GroupBy(p => p.Sheet))
        {
          if (onlySheet >= 0 && sheetPl.Key != onlySheet)
          {
            continue;
          }

          // Replicated pattern sheets are placement-identical — vet each DISTINCT layout once (an
          // 800-part run is 30 copies + 1 tail: 2 compactions instead of 31).
          string layoutSig = string.Join("|", sheetPl.Select(p => $"{p.Source}:{p.Xpx}:{p.Ypx}:{p.RotationDeg}"));
          if (!seenLayouts.Add(layoutSig))
          {
            continue;
          }

          var vet = new List<CompactItem>();
          foreach (var jp in sheetPl)
          {
            var entry = parsed.First(p => p.Src == jp.Source);
            var rotated = jp.RotationDeg == 0 ? entry.Nfp : entry.Nfp.Rotate(jp.RotationDeg);
            vet.Add(new CompactItem
            {
              Poly = rotated,
              X = (jp.Xpx / px) - rotated.MinX,
              Y = (jp.Ypx / px) - rotated.MinY,
              Spacing = entry.SpacingIn,
            });
          }

          RasterCompact.Compact(vet, sheetWin, sheetHin, System.Math.Max(0, margin));
          if (!RasterCompact.CommonLineGapsOk(vet, RasterCompact.CommonLineGap / 2.0))
          {
            return false;
          }
        }

        return true;
      }

      var sel = SelectJob();
      bool tightAdopted = false;

      // TIGHT-PACK RETRY for common-line jobs. Placements are born >= 1px apart (the safe minimum —
      // see the halo comment above), which wastes up to a pixel per gap: enough to push the last
      // part(s) of a zero-spacing job onto an extra sheet even though the COMPACTED layout has room
      // (user-measured: the 26th 7"-wide rail vs a 7.94" free strip). When the safe result is
      // imperfect, re-run the selection with TRUE zero halos and adopt it only if it strictly places
      // more parts or saves sheets — and only if compaction can still open every common-line pair to
      // the CAM-safe mini-gap (born-touching chains can jam; a jammed pair would reintroduce the
      // coincident-line hazard, so those results are discarded).
      var tightHalos = parsed.Select((p, i) => p.SpacingIn <= 0 ? 0 : halos[i]).ToArray();
      if (!tightHalos.SequenceEqual(halos) && (sel.Job.NotPlaced > 0 || sel.Job.Sheets > 1))
      {
        bool TryAdopt((JobResult Job, List<PartType> Types) attempt)
        {
          bool better = attempt.Job.NotPlaced < sel.Job.NotPlaced
            || (attempt.Job.NotPlaced == sel.Job.NotPlaced && attempt.Job.Sheets < sel.Job.Sheets);
          if (better && TightGapsOk(attempt.Job))
          {
            sel = attempt;
            tightAdopted = true;
            return true;
          }

          return false;
        }

        var safeHalos = halos;
        halos = tightHalos;
        bool adopted = TryAdopt(SelectJob());
        halos = safeHalos;

        // RESOLUTION ESCALATION: even at halo 0 every mask is up to 1px wider than the true part
        // (conservative rasterization), so a row of N common-line parts drags N phantom pixels —
        // at 24 px/in that is 0.042"/part, enough to lose a physically-fitting part once the sheet
        // margin eats the slack (user case: 17 rails × 7" across 119.5" usable). Doubling the
        // resolution halves the phantom. Only for bounded workloads — the raster cost grows steeply
        // with resolution — and the result is still gap-verified before adoption. The workload of a
        // PATTERN job is the probe cap, not the demand (an 800-part run probes ~72 and replicates,
        // so it escalates just as cheaply as a small job — and a 1-part-per-sheet gain there
        // multiplies across every replicated sheet).
        int escalationWork = patternMode ? patternCap : parsed.Sum(p => p.Qty);
        if (!adopted && (sel.Job.NotPlaced > 0 || sel.Job.Sheets > 1) && escalationWork <= 100)
        {
          px = pxPerInch * 2.0;
          sw = (int)(sheetWin * px);
          sh = (int)(sheetHin * px);
          int halfSheet2 = System.Math.Max(1, System.Math.Min(sw, sh) / 2);
          marginPx = System.Math.Max(0, System.Math.Min((int)System.Math.Round(margin * px), halfSheet2));
          halos = parsed
            .Select(p => p.SpacingIn <= 0
              ? 0
              : System.Math.Max(1, System.Math.Min((int)System.Math.Round((p.SpacingIn / 2.0) * px), halfSheet2)))
            .ToArray();

          if (!TryAdopt(SelectJob()))
          {
            // Not adopted: restore the base-resolution world so the final build matches `sel`.
            px = pxPerInch;
            sw = (int)(sheetWin * px);
            sh = (int)(sheetHin * px);
            marginPx = System.Math.Max(0, System.Math.Min((int)System.Math.Round(margin * px), halfSheet));
            halos = safeHalos;
          }
        }
      }

      // Tail sweep on the adopted result only. A tight (halo-0) job was gap-verified BEFORE the
      // sweep; the sweep only reshapes the LAST sheet, so only that sheet needs re-verification —
      // if it can't be verified, keep the unswept, already-safe layout.
      var swept = TailSweep(sel.Job);
      if (!object.ReferenceEquals(swept, sel.Job) && (!tightAdopted || TightGapsOk(swept, swept.Sheets - 1)))
      {
        sel = (swept, sel.Types);
      }

      var collection = new SheetPlacementCollection();
      int id = 0;

      // Replicated pattern sheets are placement-identical, and compaction is deterministic — compact
      // each DISTINCT layout once and reuse the slid positions for every copy (30 identical sheets of
      // an 800-part run were re-compacted 30 times for the exact same answer).
      var compactCache = new Dictionary<string, (double X, double Y)[]>();
      foreach (var sheetGroup in sel.Job.Placements.GroupBy(p => p.Sheet).OrderBy(g => g.Key))
      {
        var sheet = Sheet.NewSheet(sheetGroup.Key + 1, sheetWin, sheetHin);

        var jps = sheetGroup.ToList();
        var items = new List<CompactItem>(jps.Count);
        foreach (var jp in jps)
        {
          var entry = parsed.First(p => p.Src == jp.Source);
          var rotated = jp.RotationDeg == 0 ? entry.Nfp : entry.Nfp.Rotate(jp.RotationDeg);
          items.Add(new CompactItem
          {
            Poly = rotated,
            X = (jp.Xpx / px) - rotated.MinX,
            Y = (jp.Ypx / px) - rotated.MinY,
            Spacing = entry.SpacingIn,
          });
        }

        // Common-line parts (spacing 0) must TOUCH, but the raster grid keeps them 1-2 pixels apart
        // (its masks are conservative and positions are pixel-quantized). Close that last gap with an
        // exact-geometry compaction pass: only the spacing-0 parts slide, and spaced neighbours are
        // respected at their own half-spacing (parts end up in true contact, 0.001" safety gap).
        if (items.Any(it => it.Spacing <= 0))
        {
          string layoutSig = string.Join("|", jps.Select(p => $"{p.Source}:{p.Xpx}:{p.Ypx}:{p.RotationDeg}"));
          if (compactCache.TryGetValue(layoutSig, out var slid))
          {
            for (int i = 0; i < items.Count; i++)
            {
              items[i].X = slid[i].X;
              items[i].Y = slid[i].Y;
            }
          }
          else
          {
            RasterCompact.Compact(items, sheetWin, sheetHin, System.Math.Max(0, margin));
            compactCache[layoutSig] = items.Select(it => (it.X, it.Y)).ToArray();
          }
        }

        var placements = new List<IPartPlacement>();
        for (int i = 0; i < items.Count; i++)
        {
          placements.Add(new PartPlacement(items[i].Poly)
          {
            X = items[i].X,
            Y = items[i].Y,
            Rotation = jps[i].RotationDeg,
            Source = jps[i].Source,
            Id = id++,
          });
        }

        collection.Add(new SheetPlacement(placementType, sheet, placements, 0, SvgNest.Config.ClipperScale));
      }

      if (collection.Count == 0)
      {
        error = "Nothing could be placed (parts larger than the sheet?).";
        return null;
      }

      // Surface any parts that could not be placed (sheet quota exhausted, or too big for the sheet in
      // every rotation) so the Unplaced count is honest. Counted against the ORIGINAL demand — in
      // pattern mode `types` carries the capped probe quantity, not the real one.
      var placedPerSource = sel.Job.Placements.GroupBy(p => p.Source).ToDictionary(g => g.Key, g => g.Count());
      var unplaced = new List<INfp>();
      foreach (var p in parsed)
      {
        placedPerSource.TryGetValue(p.Src, out int placedCount);
        for (int i = placedCount; i < p.Qty; i++)
        {
          unplaced.Add(p.Nfp);
        }
      }

      int totalParts = parsed.Sum(p => p.Qty);
      return new NestResult(totalParts, collection, unplaced, placementType, 0, 0);
    }

    /// <summary>
    /// Turns a greedy multi-sheet result into "k identical pattern sheets + a freshly nested
    /// remainder" when that costs no extra sheets. Identical sheets group naturally in the production
    /// plan, and the remainder is a REAL nest — re-optimized with the full best-of candidate search
    /// (via <paramref name="nestBest"/>), so it comes out as tidy as a first-class job.
    /// </summary>
    private static JobResult TryReplicatePattern(JobResult greedy, List<PartType> types, int partCount, int maxSheets, System.Func<int[], int, (JobResult Job, List<PartType> Types)> nestBest)
    {
      if (greedy.Sheets < 2 || greedy.NotPlaced > 0)
      {
        return greedy;
      }

      var sheet0 = greedy.Placements.Where(p => p.Sheet == 0).ToList();
      var comp = sheet0.GroupBy(p => p.Source).ToDictionary(g => g.Key, g => g.Count());
      if (comp.Count == 0)
      {
        return greedy;
      }

      // Whole copies of the sheet-0 pattern the total demand supports.
      int k = int.MaxValue;
      foreach (var kv in comp)
      {
        int demand = types.First(t => t.Source == kv.Key).Quantity;
        k = System.Math.Min(k, demand / kv.Value);
      }

      if (k < 2)
      {
        return greedy; // nothing repeats — the greedy result is already the plan
      }

      // Leftover demand after k pattern sheets (Source == parsed index), re-nested with the full
      // best-of search so the remainder sheet is as clean as possible.
      var leftoverQty = new int[partCount];
      bool anyLeft = false;
      foreach (var t in types)
      {
        comp.TryGetValue(t.Source, out int used);
        int rest = t.Quantity - (k * used);
        if (rest > 0)
        {
          leftoverQty[t.Source] = rest;
          anyLeft = true;
        }
      }

      JobResult tail = null;
      if (anyLeft)
      {
        int tailSheets = maxSheets - k;
        if (tailSheets <= 0)
        {
          return greedy;
        }

        tail = nestBest(leftoverQty, tailSheets).Job;
        if (tail.NotPlaced > 0)
        {
          return greedy;
        }
      }

      int totalSheets = k + (tail?.Sheets ?? 0);
      if (totalSheets > greedy.Sheets)
      {
        return greedy; // repeating the pattern would waste material — keep the greedy nest
      }

      return ComposeReplicated(sheet0, k, tail, 0);
    }

    /// <summary>Builds the final job as k copies of the pattern sheet followed by the tail's sheets.</summary>
    private static JobResult ComposeReplicated(List<JobPlacement> sheet0, int k, JobResult tail, int notPlaced)
    {
      var placements = new List<JobPlacement>();
      for (int c = 0; c < k; c++)
      {
        foreach (var p in sheet0)
        {
          placements.Add(new JobPlacement { Source = p.Source, Sheet = c, Xpx = p.Xpx, Ypx = p.Ypx, RotationDeg = p.RotationDeg });
        }
      }

      if (tail != null)
      {
        foreach (var p in tail.Placements)
        {
          placements.Add(new JobPlacement { Source = p.Source, Sheet = k + p.Sheet, Xpx = p.Xpx, Ypx = p.Ypx, RotationDeg = p.RotationDeg });
        }
      }

      return new JobResult
      {
        Placements = placements,
        Sheets = k + (tail?.Sheets ?? 0),
        NotPlaced = notPlaced,
        NoOverlap = tail?.NoOverlap ?? true,
      };
    }

    /// <summary>
    /// Ranks two candidate nests: fewest unplaced parts first (never drop a part to save material),
    /// then fewest sheets, then the shortest occupied extent along the GROWTH axis of the last sheet
    /// (a shorter extent = a bigger clean remnant strip). The half-pixel slack keeps ties stable.
    /// </summary>
    private static bool IsBetter((JobResult Job, List<PartType> Types) a, (JobResult Job, List<PartType> Types) b, bool growX)
    {
      if (a.Job.NotPlaced != b.Job.NotPlaced)
      {
        return a.Job.NotPlaced < b.Job.NotPlaced;
      }

      if (a.Job.Sheets != b.Job.Sheets)
      {
        return a.Job.Sheets < b.Job.Sheets;
      }

      double extentA = LastSheetExtentPx(a, growX);
      double extentB = LastSheetExtentPx(b, growX);
      if (System.Math.Abs(extentA - extentB) > 0.5)
      {
        return extentA < extentB;
      }

      // Material dead-tie: prefer the TIDIER layout — fewer distinct rotations per part type. The greedy
      // mixed-rotation profile often ties the uniform one exactly (same sheets, same strip) while
      // scattering a few odd-rotated parts around; at equal material the uniform nest is what a shop wants.
      return RotationVariety(a.Job) < RotationVariety(b.Job);
    }

    /// <summary>How many distinct rotations each part type uses, summed — lower = more uniform nest.</summary>
    private static int RotationVariety(JobResult job)
    {
      return job.Placements
        .GroupBy(p => p.Source)
        .Sum(g => g.Select(p => p.RotationDeg).Distinct().Count());
    }

    /// <summary>Highest occupied position (px, real part extents) along the growth axis on the last sheet.</summary>
    private static double LastSheetExtentPx((JobResult Job, List<PartType> Types) r, bool growX)
    {
      int last = r.Job.Sheets - 1;
      double max = 0;
      foreach (var p in r.Job.Placements)
      {
        if (p.Sheet != last)
        {
          continue;
        }

        var t = r.Types.First(x => x.Source == p.Source);
        int ri = System.Array.IndexOf(t.RotationsDeg, p.RotationDeg);
        if (ri >= 0)
        {
          double extent = growX
            ? p.Xpx + t.Masks[ri].W - (2.0 * t.HaloPx)
            : p.Ypx + t.Masks[ri].H - (2.0 * t.HaloPx);
          max = System.Math.Max(max, extent);
        }
      }

      return max;
    }

    /// <summary>
    /// Radan-style permitted-orientation codes → explicit angle sets. Legacy count codes keep their
    /// historical meaning (1 = as drawn, 2 = 0/180, 4 = four square orientations, 8 = 45° steps,
    /// bigger = any); the 100x codes are the orientation choices Radan offers that a plain count
    /// cannot express. "Any" maps to 15° steps — a superset of every candidate profile the nester
    /// actually tries (measured long ago: finer steps only slow the greedy down and pack worse).
    /// </summary>
    internal const int RotOnly90 = 1001;      // only 90° — always turned once
    internal const int RotZeroAnd90 = 1002;   // 0° and 90°
    internal const int Rot90And270 = 1003;    // 90° and 270°

    internal static int[] PermittedSet(int code)
    {
      switch (code)
      {
        case RotOnly90: return new[] { 90 };
        case RotZeroAnd90: return new[] { 0, 90 };
        case Rot90And270: return new[] { 90, 270 };
      }

      if (code <= 1)
      {
        return new[] { 0 };
      }

      if (code == 2)
      {
        return new[] { 0, 180 };
      }

      if (code <= 7)
      {
        return new[] { 0, 90, 180, 270 };
      }

      return code == 8 ? AnglesN(8) : AnglesN(24);
    }

    /// <summary>
    /// Candidate rotation profiles for the whole job. Each entry is one rotation set PER PART, always a
    /// subset of what that part's permitted orientations allow (a fixed/grain part keeps its own set in
    /// every profile). Duplicates collapse, so a job of fixed/flip-only parts runs exactly once.
    /// </summary>
    private static List<int[][]> BuildCandidates(int[][] allowed, int[] straighten)
    {
      int n = allowed.Length;
      var candidates = new List<int[][]>();
      var seen = new HashSet<string>();

      // Profile ∩ permitted; an empty intersection falls back to the part's square orientations (or,
      // failing that, its own full set) so no profile ever violates a part's permission.
      int[] Inter(int[] set, int[] profile)
      {
        var r = set.Where(profile.Contains).ToArray();
        return r.Length > 0 ? r : null;
      }

      int[] Base(int[] s) => Inter(s, new[] { 0, 90, 180, 270 }) ?? s;
      int[] Flip(int[] s) => Inter(s, new[] { 0, 180 }) ?? Base(s);
      int[] FlipOffset(int[] s) => Inter(s, new[] { 90, 270 }) ?? Base(s);
      int[] Eight(int[] s) => Inter(s, AnglesN(8)) ?? Base(s);

      void Add(System.Func<int[], int[]> setFor, bool applyStraighten)
      {
        var cand = new int[n][];
        for (int i = 0; i < n; i++)
        {
          int off = applyStraighten && allowed[i].Length >= 8 ? straighten[i] : 0;
          cand[i] = setFor(allowed[i]).Select(r => (((r + off) % 360) + 360) % 360).Distinct().ToArray();
        }

        string key = string.Join("|", cand.Select(c => string.Join(",", c)));
        if (seen.Add(key))
        {
          candidates.Add(cand);
        }
      }

      // Measured on real production parts (60x120 sheet): the coarse profiles find the wins — the
      // seed-offset {90,270} profile packed the same job in 2 sheets instead of 3 — while fine-step
      // profiles (45°/free) packed WORSE (greedy bottom-left can't exploit the extra freedom) at a much
      // higher scan cost. So: no full-free profile; 45° steps only for parts explicitly allowed >= 8.
      Add(Base, false);       // the plain single-profile nest — guaranteed floor
      Add(Flip, false);       // {0,180}: often packs interlocking shapes best
      Add(FlipOffset, false); // {90,270}: bottom-left never rotates the first part, so vary the seed
      Add(Eight, false);      // 45° steps where the part's own permission allows them
      Add(Base, true);        // straightened (min-bbox) variants of the strongest profiles
      Add(Flip, true);
      Add(Eight, true);

      return candidates;
    }

    /// <summary>Evenly spaced rotation angles: AnglesN(8) = {0, 45, 90, ...}.</summary>
    private static int[] AnglesN(int n)
    {
      var a = new int[n];
      for (int i = 0; i < n; i++)
      {
        a[i] = i * 360 / n;
      }

      return a;
    }

    /// <summary>
    /// The rotation (degrees, 1° sampling over 0-179 — bbox area has a 180° period) that orients the
    /// part to its minimum-area bounding box, i.e. straightens a part drawn at an odd angle to its
    /// tightest footprint before the rotation set is applied on top.
    /// </summary>
    private static int MinBoundingBoxAngle(INfp poly)
    {
      if (poly?.Points == null || poly.Points.Length < 3)
      {
        return 0;
      }

      int bestAngle = 0;
      double bestArea = double.MaxValue;
      for (int a = 0; a < 180; a++)
      {
        var r = a == 0 ? poly : poly.Rotate(a);
        double area = (r.MaxX - r.MinX) * (r.MaxY - r.MinY);
        if (area < bestArea - 1e-6)
        {
          bestArea = area;
          bestAngle = a;
        }
      }

      return bestAngle;
    }
  }
}
