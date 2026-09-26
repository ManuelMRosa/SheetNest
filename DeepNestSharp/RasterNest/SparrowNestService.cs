namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Collections.Concurrent;
  using System.Diagnostics;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text.Json;
  using System.Threading;
  using System.Threading.Tasks;
  using ClipperLib;
  using DeepNestLib;
  using DeepNestLib.IO;
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
    // is (waves x per-sheet budget) and everything else is noise — but only a breakdown proves that, so
    // keep these: they are what a "nesting is slow" report gets measured with. Caller resets them.
    internal static long DiagLoadMs;
    internal static long DiagSparrowMs;
    internal static long DiagMapMs;
    internal static long DiagHolesMs;
    internal static readonly ConcurrentQueue<string> DiagCommonLineTimings = new();

    /// <summary>Placements the engine made that fell outside the sheet and were thrown away, summed over
    /// every candidate. A strip packer is asked for a strip and handed a sheet, so some overhang is normal;
    /// a lot of it means the batch we fed it was the wrong size for the sheet.</summary>
    internal static long DiagCulled;
    internal static long DiagWaves;
    internal static long DiagTries;

    // Every best-of-K candidate as "sheet,seed,count,density,extentX,extentY", appended as each try lands.
    // A user reported the same job producing a different offcut on every run; this is what shows whether
    // the winner sparrow's own density picks is also the one that leaves the largest remnant. Caller resets.
    internal static readonly List<string> DiagCandidates = new List<string>();

    /// <summary>Seams welded on the last nest, and groups of them given up because a cut would have
    /// invaded a neighbour. The second number is the one worth showing: it is the difference between
    /// "there was nothing to share" and "there was, and it had to be abandoned".</summary>
    /// <summary>Parts a sheet had to give back because the finished layout would not have been cuttable.
    /// Zero on a healthy job; anything else is material the pack claimed and could not keep.</summary>
    internal static int DiagDeferred;

    internal static int DiagSeamsSnapped;

    internal static int DiagSeamsGivenUp;

    /// <summary>Direct-plan rejection stages from the most recent nest. This is diagnostic evidence only;
    /// it does not participate in candidate selection or acceptance.</summary>
    internal static readonly ConcurrentQueue<string> DiagDirectFailures = new();


    /// <summary>
    /// Per-drawing reasons why direct common-line relationships on the kept result fell back to normal
    /// spacing. Candidate searches run concurrently, so only <see cref="PackOneSheet"/>'s winner is added.
    /// </summary>
    internal static readonly List<CommonLineFallbackNotice> DiagCommonLineFallbacks = new List<CommonLineFallbackNotice>();

    private const double DirectSeamTolerance = 2.5e-6; // compaction/export coordinates are quantised to 1e-6

    private static int diagPackSeq;

    private sealed class Loaded
    {
      public int Source;
      public INfp Nfp;        // original (rendered) geometry
      public INfp Dilated;    // tooling footprint grown by FitSpacing/2 — what sparrow packs
      public int[] Angles;    // allowed orientations (discrete fallback + used by hole-filling)

      // Width and height of the NOMINAL outline at each of those orientations, measured once at load.
      // Whether a part can go on a sheet at all is asked for every stock size on every sheet of the job,
      // and the answer never changes, so it is not worth rotating the polygon again to find it out.
      public (double W, double H)[] AngleExtents;

      public bool Continuous; // "Free" rotation → send NO orientation list so sparrow rotates continuously
      public double EffSpacing;   // NOMINAL spacing: what this part keeps to anything it cannot share a cut with
      public CommonCuttingMode Cc; // who it may share a cut edge with; None = nobody
      public int ShareKey;    // identity for SamePart: same drawing AND same hand
      public string CommonCuttingDomain = string.Empty; // the contract this part shares under; never cross contracts
      public bool RequiresMergedLines;   // direct nominal contour opts into resolved cluster/midpoint DXF output

      // What to grow by where only ONE polygon per part can be handed over: always the full nominal
      // spacing. Those callers cannot express a clearance that depends on a concrete selected edge, so
      // compatibility alone must not create a tight layout.
      public double FitSpacing;
      public int Qty;
      public bool Mirrored;   // this population is X-flipped — placements carry IsMirrored for the exporter
      public int Priority;    // 1-10, LOWER nests first (1 = highest); decides which parts fill earlier sheets
    }

    /// <summary>
    /// One outside-edge interval relationship deliberately created by the direct common-line planner.
    /// References are kept until the final sheet ids exist; accidental contacts are never promoted into
    /// records by scanning the finished sheet.
    /// </summary>
    internal sealed class CommonEdgeIntent
    {
      public CommonEdgeIntent(
        IPartPlacement partA,
        int edgeAIndex,
        IPartPlacement partB,
        int edgeBIndex,
        double edgeAStart = 0,
        double edgeAEnd = 1,
        double edgeBStart = 0,
        double edgeBEnd = 1,
        CommonBoundarySpan spanA = null,
        CommonBoundarySpan spanB = null)
      {
        this.PartA = partA;
        this.EdgeAIndex = edgeAIndex;
        this.PartB = partB;
        this.EdgeBIndex = edgeBIndex;
        this.EdgeAStart = edgeAStart;
        this.EdgeAEnd = edgeAEnd;
        this.EdgeBStart = edgeBStart;
        this.EdgeBEnd = edgeBEnd;
        this.SpanA = spanA;
        this.SpanB = spanB;
      }

      public IPartPlacement PartA { get; }

      public int EdgeAIndex { get; }

      public IPartPlacement PartB { get; }

      public int EdgeBIndex { get; }

      /// <summary>Normalized edge interval consumed by the relationship. Legacy full-edge records use 0..1.</summary>
      public double EdgeAStart { get; }

      public double EdgeAEnd { get; }

      /// <summary>Normalized edge interval consumed by the relationship. Legacy full-edge records use 0..1.</summary>
      public double EdgeBStart { get; }

      public double EdgeBEnd { get; }

      /// <summary>
      /// Optional exact source-DXF intervals. Circular common lines always carry both spans; legacy
      /// polygon LINE candidates retain their indexed-edge representation above.
      /// </summary>
      public CommonBoundarySpan SpanA { get; }

      public CommonBoundarySpan SpanB { get; }
    }

    /// <summary>Result of creating rigid direct common-line clusters on one candidate layout.</summary>
    internal sealed class DirectCommonLineSnapResult
    {
      public List<CommonEdgeIntent> Edges { get; } = new List<CommonEdgeIntent>();

      public List<CommonLineFallbackNotice> Fallbacks { get; } = new List<CommonLineFallbackNotice>();

      public int[] Groups { get; set; } = Array.Empty<int>();

      public int GivenUp { get; set; }
    }

    /// <summary>
    /// Source geometry and its exact orientation policy for the direct common-line planner. Discrete
    /// profiles enumerate every legal pose; continuous profiles derive useful exact poses from facing
    /// source edges instead of rounding them to the fallback sample angles.
    /// </summary>
    internal sealed class DirectOrientationProfile
    {
      public DirectOrientationProfile(
        INfp nfp,
        IEnumerable<int> angles,
        bool continuous = false)
      {
        this.Nfp = nfp;
        this.Angles = (angles ?? Enumerable.Empty<int>()).ToArray();
        this.Continuous = continuous;
      }

      public INfp Nfp { get; }

      public IReadOnlyList<int> Angles { get; }

      /// <summary>
      /// True when every angle is permitted. <see cref="Angles"/> remains the deterministic fallback
      /// used by hole filling; it is not treated as a restriction by the direct common-line planner.
      /// </summary>
      public bool Continuous { get; }
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
      CancellationToken cancel = default,
      NestEffort effort = NestEffortDefaults.Default,
      double commonLineGap = CommonLineDefaults.DefaultGapInches,
      double geometryTolerance = CommonLineDefaults.DefaultGeometryToleranceInches,
      int commonLineMaxClusterParts = CommonLineDefaults.AutoMaxClusterParts)
      => Nest(
        parts,
        new[] { ((int)Math.Round(sheetWin), (int)Math.Round(sheetHin), 1) },
        rotations,
        spacing,
        margin,
        timeLimitSec,
        sparrowExePath,
        out error,
        cancel,
        null,
        effort,
        commonLineGap,
        geometryTolerance,
        commonLineMaxClusterParts);

    /// <summary>Multi-sheet nest: fills sheets from <paramref name="stock"/> until the pool is empty or
    /// the stock is exhausted; whatever cannot fit is returned as unplaced.
    /// <para><paramref name="perSheetBudgetSec"/> no longer bounds the search — how hard a sheet is
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
      System.IProgress<(int Placed, int Total, int Sheet, double Density)> progress = null,
      NestEffort effort = NestEffortDefaults.Default,
      double commonLineGap = CommonLineDefaults.DefaultGapInches,
      double geometryTolerance = CommonLineDefaults.DefaultGeometryToleranceInches,
      int commonLineMaxClusterParts = CommonLineDefaults.AutoMaxClusterParts)
      => Nest(
        parts,
        stock?.Select(s => (s.Win, s.Hin, s.Qty, false)).ToList(),
        rotations,
        spacing,
        margin,
        perSheetBudgetSec,
        sparrowExePath,
        out error,
        cancel,
        progress,
        effort,
        commonLineGap,
        geometryTolerance,
        commonLineMaxClusterParts);

    /// <summary>The same nest with stock that may declare a size UNLIMITED: one the job can keep taking
    /// sheets of until every part is placed, so its quantity is not a stock level and is ignored.</summary>
    internal static INestResult Nest(
      IReadOnlyList<RasterPartInfo> parts,
      IReadOnlyList<(int Win, int Hin, int Qty, bool Unlimited)> stock,
      int rotations,
      double spacing,
      double margin,
      int perSheetBudgetSec,
      string sparrowExePath,
      out string error,
      CancellationToken cancel = default,
      System.IProgress<(int Placed, int Total, int Sheet, double Density)> progress = null,
      NestEffort effort = NestEffortDefaults.Default,
      double commonLineGap = CommonLineDefaults.DefaultGapInches,
      double geometryTolerance = CommonLineDefaults.DefaultGeometryToleranceInches,
      int commonLineMaxClusterParts = CommonLineDefaults.AutoMaxClusterParts)
    {
      error = null;
      // These values are already expressed in the caller's active drawing units. Substituting inch
      // constants here would silently turn 0.010in into 0.010mm for an invalid metric call. Reject the
      // request instead; the UI supplies normalized, unit-scaled values and direct callers get an
      // actionable fail-closed result.
      if (!double.IsFinite(commonLineGap) || commonLineGap <= 0D)
      {
        error = "Common Line Gap must be a finite positive distance in the active drawing units.";
        return null;
      }

      if (!double.IsFinite(geometryTolerance)
          || geometryTolerance <= 0D
          || commonLineGap <= geometryTolerance)
      {
        error = "GeometryTolerance must be finite, positive, and smaller than Common Line Gap.";
        return null;
      }

      if (string.IsNullOrWhiteSpace(sparrowExePath) || !File.Exists(sparrowExePath))
      {
        error = "Nesting engine (sparrow.exe) was not found.";
        return null;
      }

      // The stock is kept AS SIZES with a remaining count, not expanded into a flat list of slots: an
      // unlimited size has no count to expand, and the loop below chooses which size to cut next rather
      // than walking a list in order, so there is no list to walk.
      var sizes = new List<StockSize>();
      if (stock != null)
      {
        foreach (var s in stock)
        {
          if (s.Win > 0 && s.Hin > 0 && (s.Unlimited || s.Qty > 0))
          {
            sizes.Add(new StockSize { W = s.Win, H = s.Hin, Remaining = s.Qty, Unlimited = s.Unlimited });
          }
        }
      }

      if (sizes.Count == 0)
      {
        error = "Add a sheet size first.";
        return null;
      }

      // Sparrow first preserves the ordinary spacing baseline. The schema-aware planner then evaluates
      // every permitted discrete pose (or bounded deterministic poses for Continuous rotation) and moves
      // only explicitly selected LINE/ARC spans to CommonLineGap.
      DiagDeferred = 0;
      DiagSeamsSnapped = 0;
      DiagSeamsGivenUp = 0;
      while (DiagDirectFailures.TryDequeue(out _))
      {
      }
      lock (DiagCommonLineFallbacks)
      {
        DiagCommonLineFallbacks.Clear();
      }

      var diagLoad = System.Diagnostics.Stopwatch.StartNew();
      var loaded = LoadAll(parts, rotations, spacing, out error);
      DiagLoadMs = diagLoad.ElapsedMilliseconds;
      if (loaded == null || loaded.Count == 0)
      {
        error ??= "No valid parts to nest.";
        return null;
      }

      return RunNestBody(
        loaded,
        sizes,
        margin,
        perSheetBudgetSec,
        sparrowExePath,
        cancel,
        progress,
        effort,
        commonLineGap,
        geometryTolerance,
        commonLineMaxClusterParts,
        out error);
    }

    /// <summary>One size of stock and how much of it is left. An unlimited size never runs down, so its
    /// <see cref="Remaining"/> is only the number the operator happened to leave in the box.</summary>
    private sealed class StockSize
    {
      public int W { get; set; }

      public int H { get; set; }

      public int Remaining { get; set; }

      public bool Unlimited { get; set; }

      public bool HasStock => this.Unlimited || this.Remaining > 0;

      public void TakeOne()
      {
        if (!this.Unlimited)
        {
          this.Remaining--;
        }
      }
    }

    /// <summary>The per-sheet nest loop: fills sheets from the stock (best-of-K sparrow packs, corner
    /// compaction, pattern replication) and maps to an <see cref="INestResult"/>.</summary>
    private static INestResult RunNestBody(
      List<Loaded> loaded,
      List<StockSize> sizes,
      double margin,
      int perSheetBudgetSec,
      string sparrowExePath,
      CancellationToken cancel,
      System.IProgress<(int Placed, int Total, int Sheet, double Density)> progress,
      NestEffort effort,
      double commonLineGap,
      double geometryTolerance,
      int commonLineMaxClusterParts,
      out string error)
    {
      error = null;
      var loadedById = loaded.ToDictionary(l => l.Source);
      var outlineById = loaded.ToDictionary(l => l.Source, l => l.Nfp);
      var pool = loaded.ToDictionary(l => l.Source, l => l.Qty);
      int totalParts = pool.Values.Sum();

      var sheetLayouts = new List<List<IPartPlacement>>();
      var sheetCommonEdges = new List<List<CommonEdgeIntent>>();
      string firstPackError = null;
      var sheetSizes = new List<(int W, int H)>();

      // A sheet that gets used places at least one part, so the job cannot ask for more sheets than it has
      // parts. The old flat slot list carried a MaxSheets = 1000 truncation; the bound is now the work
      // itself, which is why an unlimited size cannot run away.
      while (pool.Values.Sum() > 0 && sheetLayouts.Count < totalParts)
      {
        cancel.ThrowIfCancellationRequested();
        int placedSoFar = totalParts - pool.Values.Sum();
        int sheetNum = sheetLayouts.Count + 1;
        progress?.Report((placedSoFar, totalParts, sheetNum, 0));
        Action<double> onDensity = d => progress?.Report((placedSoFar, totalParts, sheetNum, d));

        // Pack the pending work onto EVERY size still in stock and cut the one that wastes least. With a
        // single size in the job this is one pack, exactly as before. The losers' work is thrown away,
        // which is the price of choosing on the real answer instead of guessing from areas.
        StockSize bestSize = null;
        List<IPartPlacement> bestPlacements = null;
        Dictionary<int, int> bestPlacedBySrc = null;
        int bestW = 0;
        int bestH = 0;
        double bestUtilization = -1;
        int bestSeamsSnapped = 0;
        int bestSeamsGivenUp = 0;
        List<CommonEdgeIntent> bestCommonEdges = null;
        IReadOnlyList<CommonLineFallbackNotice> bestCommonLineFallbacks = Array.Empty<CommonLineFallbackNotice>();

        foreach (var size in sizes)
        {
          if (!size.HasStock)
          {
            continue;
          }

          int w = size.W;
          int h = size.H;
          var batchQty = SelectBatch(pool, loadedById, sizes, w, h, margin);

          // Search budget, in ITERATIONS rather than seconds (see RunSparrowOnce), chosen by the operator's
          // effort setting. The wall clock a sheet takes is a consequence of the machine, not an input to the
          // answer, so two computers still agree on the nest.
          int batchParts = batchQty.Values.Sum();
          int budget = IterationBudget(effort);

          // How many identical sheets this exact batch will tile (it is packed ONCE then pattern-replicated).
          // A batch that governs many clones is worth many best-of-K tries — the cost is amortized over all
          // the clones — which makes the template sheet converge to a consistent, dense layout (more time
          // does NOT reduce sparrow's run-to-run variance, but more tries do).
          int replicas = batchQty.Count == 0 ? 1 : batchQty.Min(kv => pool[kv.Key] / Math.Max(1, kv.Value));

          // The final/tail sheet takes the whole remaining pool (a leftover count that won't fill a sheet).
          // It is a single one-off — replicas=1 → base tries → it varies run-to-run. Since it's just ONE
          // sheet, invest more best-of-K in it so it lands consistently, like the replicated body already does.
          bool isFinalSheet = batchParts == pool.Values.Sum();
          int tries = TriesFor(replicas, isFinalSheet, effort);

          // packW/packH = the sheet dims PackOneSheet packed in (always the stock size's own orientation — the
          // sheet is never auto-rotated). Kept as a return value so the render matches what was packed.
          // Watchdog only: a search must finish because it ran out of iterations, never because it ran out
          // of seconds. Scaled off the caller's time preference so a user who asks for longer nests also
          // gets a longer leash, but far above any healthy run — it exists to catch a hung process.
          int hardTimeoutSec = WatchdogSeconds(perSheetBudgetSec, effort);

          var (placements, placedBySrc, packW, packH, seamsSnapped, seamsGivenUp, commonEdges, commonLineFallbacks) = PackOneSheet(
            loaded,
            batchQty,
            w,
            h,
            margin,
            budget,
            SearchWorkers(effort),
            hardTimeoutSec,
            tries,
            sparrowExePath,
            cancel,
            onDensity,
            commonLineGap,
            geometryTolerance,
            effort,
            commonLineMaxClusterParts,
            out string perr);
          firstPackError ??= perr;
          if (cancel.IsCancellationRequested)
          {
            error = "Cancelled.";
            return null;
          }

          // LAST CHANCE. If this is the only sheet left and the search still has not taken everything, the
          // parts it leaves behind are not going anywhere else: they come back as unplaced. That is worth
          // paying for, and only there.
          //
          // Measured on the job from issue #2, fourteen parts that have to share one sheet. The default
          // budget is max(15, parts/2), so FIFTEEN iterations, and it seats twelve at 52.6%. Raise it and
          // all fourteen go on at 60.5% with nothing overlapping, which is the same figure the operator's
          // own screenshot reached while painting two of them red.
          //
          // Raising the default instead would be a poor trade, and the second of his jobs says so plainly:
          // twenty-seven parts across three sheets place fully at every setting, so the extra search buys
          // exactly nothing there and costs twenty-five times the wall clock, 2.7 seconds against 66.8.
          // The difference is not the size of the job, it is whether the parts have anywhere else to go.
          // Only when it NEARLY managed. "Are any parts left?" is the wrong question: a sheet is handed a
          // batch of about 1.3 times what it can hold, so something is left over almost every time, and
          // asking that spent the big budget on every last sheet in the job. Measured: the test suite went
          // from 2m13s to 26m23s for no better answer anywhere. What is worth paying for is the sheet that
          // came within a couple of parts, which is the one more searching can actually finish.
          int packedHere = placedBySrc?.Values.Sum() ?? 0;
          int leftOver = pool.Values.Sum() - packedHere;
          // Whatever this sheet leaves behind is about to be handed back as uncut, so search again, harder.
          // There used to be a `leftOver * 4 <= packedHere` clause here, which quietly made the retry refuse
          // the cases that need it most: a job that seats 11 of 14 has too much left over to qualify and gets
          // no second chance at all, while one that seats 13 of 14 gets one. It was there to keep the cost
          // down when a single budget served everybody; the operator picks the effort now, and giving parts
          // back uncut is worth more seconds than any of them.
          if (placements != null && packedHere > 0 && leftOver > 0 && IsLastChanceFor(size, sizes))
          {
            var (retryPl, retryBy, retryW, retryH, retrySeamsSnapped, retrySeamsGivenUp, retryCommonEdges, retryCommonLineFallbacks) = PackOneSheet(
              loaded,
              batchQty,
              w,
              h,
              margin,
              LastChanceBudget(effort),
              SearchWorkers(effort),
              hardTimeoutSec,
              tries,
              sparrowExePath,
              cancel,
              onDensity,
              commonLineGap,
              geometryTolerance,
              effort,
              commonLineMaxClusterParts,
              out _);

            if (!cancel.IsCancellationRequested && retryPl != null && retryBy != null
                && retryBy.Values.Sum() > placedBySrc.Values.Sum())
            {
              placements = retryPl;
              placedBySrc = retryBy;
              packW = retryW;
              packH = retryH;
              seamsSnapped = retrySeamsSnapped;
              seamsGivenUp = retrySeamsGivenUp;
              commonEdges = retryCommonEdges;
              commonLineFallbacks = retryCommonLineFallbacks;
            }
          }

          if (placements == null || placements.Count == 0)
          {
            continue; // nothing fits this size: it is not a candidate for this sheet
          }

          // Wastes least = fills most of its own sheet. Comparing the waste AREA instead would hand it to
          // the smallest size every time, for being small rather than for being well used.
          double sheetArea = Math.Max(1e-9, (double)packW * packH);
          double placedArea = placedBySrc.Sum(kv => kv.Value * Math.Abs(loadedById[kv.Key].Nfp.Area));
          double utilization = placedArea / sheetArea;
          if (utilization > bestUtilization)
          {
            bestUtilization = utilization;
            bestSize = size;
            bestPlacements = placements;
            bestPlacedBySrc = placedBySrc;
            bestW = packW;
            bestH = packH;
            bestSeamsSnapped = seamsSnapped;
            bestSeamsGivenUp = seamsGivenUp;
            bestCommonEdges = commonEdges;
            bestCommonLineFallbacks = commonLineFallbacks;
          }
        }

        if (bestSize == null)
        {
          // Every size still in stock packed NOTHING, so what is left over cannot be cut from any of them
          // and asking again would only ask the same question. This is what stops an unlimited size: with
          // no sheet placed the loop's own counter never advances, so without this break it would spin.
          break;
        }

        // Only the size/attempt that was actually kept ever gets cut — every other stock size tried for
        // this sheet, and the first attempt when the last-chance retry beat it, are thrown away just like
        // a losing search candidate, and their seam counts go with them.
        System.Threading.Interlocked.Add(ref DiagSeamsSnapped, bestSeamsSnapped);
        System.Threading.Interlocked.Add(ref DiagSeamsGivenUp, bestSeamsGivenUp);
        lock (DiagCommonLineFallbacks)
        {
          foreach (CommonLineFallbackNotice notice in bestCommonLineFallbacks ?? Array.Empty<CommonLineFallbackNotice>())
          {
            if (!DiagCommonLineFallbacks.Any(existing =>
              string.Equals(existing.Drawing, notice.Drawing, StringComparison.OrdinalIgnoreCase)
              && string.Equals(existing.Reason, notice.Reason, StringComparison.Ordinal)))
            {
              DiagCommonLineFallbacks.Add(notice);
            }
          }
        }

        // The sheet is judged by the rule that decides whether it may be cut, before it is handed over.
        // Whatever fails goes back in the pool and lands on the next sheet, so no layout ever leaves here
        // that the app would paint red. See DeferUnfit for why this is not the same as trusting the pack.
        int deferred = DeferUnfit(
          bestPlacements,
          bestPlacedBySrc,
          loadedById,
          bestCommonEdges,
          bestW,
          bestH,
          margin,
          commonLineGap,
          geometryTolerance);
        if (bestPlacements.Count == 0)
        {
          break; // nothing on this sheet survived the check, so another sheet of it would fare no better
        }

        if (deferred > 0)
        {
          DiagDeferred += deferred;
        }

        sheetLayouts.Add(bestPlacements);
        sheetCommonEdges.Add(bestCommonEdges ?? new List<CommonEdgeIntent>());
        sheetSizes.Add((bestW, bestH));
        Deduct(pool, bestPlacedBySrc);
        bestSize.TakeOne();

        // Pattern replication: while the pool still holds the SAME composition and that size still has
        // sheets, clone this layout instead of re-nesting (uniform jobs → 1 run + replicate).
        while (bestSize.HasStock && PoolContains(pool, bestPlacedBySrc))
        {
          var clone = ClonePlacements(bestPlacements, bestCommonEdges);
          sheetLayouts.Add(clone.Placements);
          sheetCommonEdges.Add(clone.CommonEdges);
          sheetSizes.Add((bestW, bestH));
          Deduct(pool, bestPlacedBySrc);
          bestSize.TakeOne();
          progress?.Report((totalParts - pool.Values.Sum(), totalParts, sheetLayouts.Count, 1.0));
        }
      }

      progress?.Report((totalParts - pool.Values.Sum(), totalParts, sheetLayouts.Count, 1.0));

      if (sheetLayouts.Count == 0)
      {
        error = firstPackError == null
          ? "The nesting engine returned no placements."
          : "The nesting engine returned no placements: " + firstPackError;
        return null;
      }

      // jagua ignores item holes, so sparrow never nests inside them. Recover that density ourselves:
      // drop leftover parts into the holes of already-placed parts (exact geometry, spacing preserved).
      var diagHoles = System.Diagnostics.Stopwatch.StartNew();
      FillHoles(sheetLayouts, pool, loadedById, cancel);
      DiagHolesMs = diagHoles.ElapsedMilliseconds;

      // Cloning and hole filling are intentionally incapable of moving a cluster, but the persisted
      // metadata is safety-critical: prove that assumption on the exact final layouts before ids/export.
      for (int sheetIndex = 0; sheetIndex < sheetLayouts.Count; sheetIndex++)
      {
        for (int placementIndex = 0; placementIndex < sheetLayouts[sheetIndex].Count; placementIndex++)
        {
          if (sheetLayouts[sheetIndex][placementIndex] is PartPlacement placement)
          {
            placement.Id = placementIndex;
          }
        }

        // A sheet with no recorded cluster has nothing here to prove: there is no shrunk spacing to
        // protect, and the acceptance clearance is not the rule the rest of the sheet was built to.
        // FillHoles above deliberately seats parts INSIDE another part's hole, granting each one
        // FitSpacing/2 around the polygon it grows; judging those pairs by the neighbour-dependent
        // AcceptanceClearanceOf condemns them and takes the whole nest down with them.
        if (sheetCommonEdges[sheetIndex].Count > 0)
        {
          bool edgesValid = sheetCommonEdges[sheetIndex].All(edge => TryCreateCommonEdgeRecord(
            edge,
            commonLineGap,
            geometryTolerance,
            out _));
          if (!edgesValid
              || !OutlinesClearEveryone(sheetLayouts[sheetIndex], outlineById, null)
              || !FinalLayoutIsValid(
                sheetLayouts[sheetIndex],
                sheetCommonEdges[sheetIndex],
                loadedById,
                sheetSizes[sheetIndex].W,
                sheetSizes[sheetIndex].H,
                margin,
                commonLineGap,
                geometryTolerance,
                cancel))
          {
            error = "A common-line cluster did not survive final sheet validation.";
            return null;
          }
        }
      }

      var collection = new SheetPlacementCollection();
      int id = 0;
      for (int i = 0; i < sheetLayouts.Count; i++)
      {
        foreach (var pp in sheetLayouts[i])
        {
          ((PartPlacement)pp).Id = id++;
        }

        var commonEdges = new List<CommonEdge>();
        foreach (CommonEdgeIntent intent in sheetCommonEdges[i])
        {
          if (!sheetLayouts[i].Contains(intent.PartA)
              || !sheetLayouts[i].Contains(intent.PartB)
              || !TryCreateCommonEdgeRecord(
                intent,
                commonLineGap,
                geometryTolerance,
                out CommonEdge commonEdge))
          {
            error = "A common-line cluster changed after final placement and was rejected.";
            return null;
          }

          commonEdges.Add(commonEdge);
        }

        // Intent count is the seam count. A physical scan here would silently promote accidental contacts
        // made by compaction or hole filling into cuts that the planner never selected.
        System.Threading.Interlocked.Add(ref DiagSeamsSnapped, commonEdges.Count);

        var sheet = Sheet.NewSheet(i + 1, sheetSizes[i].W, sheetSizes[i].H);
        var sheetPlacement = new SheetPlacement(
          placementType: PlacementTypeEnum.BoundingBox,
          sheet: sheet,
          partPlacements: sheetLayouts[i],
          mergedLength: 0,
          clipperScale: SvgNest.Config.ClipperScale,
          // CommonEdges makes the constructor opt into merged cluster output. Merely enabling the
          // feature on a source is not enough when this sheet produced no valid seam.
          requiresMergedLines: false,
          commonEdges: commonEdges,
          commonLineGap: commonLineGap,
          geometryTolerance: geometryTolerance);
        try
        {
          _ = CommonLineGeometryResolver.Resolve(sheetPlacement);
        }
        catch (CommonLineValidationException ex)
        {
          error = "A common-line cluster failed final topology validation: " + ex.Message;
          return null;
        }

        collection.Add(sheetPlacement);
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

      // "The same part" means the same DRAWING, not the same row: the list may legitimately hold one file
      // twice (once common-cut, once spaced), and two rows of the same file are still the same part on the
      // sheet. A MIRRORED population is a different part, though � its shared edges are the other hand's.
      // The comparer is case-insensitive because a drawing path is a Windows identity.
      var shareKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      var shareKeyByPart = new Dictionary<RasterPartInfo, int>();
      foreach (var part in parts.Where(p => p != null))
      {
        string identity = (part.Mirrored ? "mirrored\0" : "regular\0") + (part.Path ?? string.Empty);
        if (!shareKeys.TryGetValue(identity, out int key))
        {
          shareKeys[identity] = key = shareKeys.Count;
        }

        shareKeyByPart[part] = key;
      }

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
            // DXF exporter (which reloads the ORIGINAL file) mirrors it once too — same order the
            // exporter uses (MirrorX about origin → rotate → translate), so both paths agree.
            nfp = nfp.MirrorX();
          }

          int code = part.Rotations > 0 ? part.Rotations : rotations;

          // Common-line eligibility never narrows the user's rotation policy. The direct planner validates
          // the exact transformed edges it selects, so narrowing rotations would lose legal triangle and
          // oblique layouts before geometry had a chance to judge them.
          code = CommonLineRotationCode(code, part.RequiresMergedLines);

          double effSpacing = part.Spacing >= 0 ? part.Spacing : Math.Max(0, spacing);
          // The native packer cannot express a pair-specific clearance, so it ALWAYS receives the normal
          // spacing shell. Only the explicit direct planner may later pull one selected full edge down to
          // CommonLineGap along a recorded source span; compatibility alone never removes the baseline halo.
          double fitSpacing = effSpacing;

          // The file path is what "the same part" means: two rows of the same drawing are the same part
          // on the sheet, and a mirrored population is the other hand, so a different one.
          int shareKey = shareKeyByPart[part];
          int[] angles = RotationCodes.PermittedSet(code);

          loaded.Add(new Loaded
          {
            Source = source,
            Nfp = nfp,
            // Half the spacing on each of the pair is the whole gap between them.
            Dilated = OffsetOutward(nfp, fitSpacing / 2.0),

            // The same shape without the spacing shell: compaction adds the spacing itself, so handing
            // it the dilated one would double-count.
            Angles = angles,
            AngleExtents = angles
              .Select(angle =>
              {
                INfp turned = angle == 0 ? nfp : nfp.Rotate(angle);
                return (turned.MaxX - turned.MinX, turned.MaxY - turned.MinY);
              })
              .ToArray(),
            Continuous = code == 36, // only "Free" (sentinel 36) is continuous; 1001/1002/1003 are discrete sets
            EffSpacing = effSpacing,
            FitSpacing = fitSpacing,
            Cc = part.Cc,
            ShareKey = shareKey,
            CommonCuttingDomain = part.CommonCuttingDomain,
            RequiresMergedLines = part.RequiresMergedLines,
            Qty = part.Quantity,
            Mirrored = part.Mirrored,
            Priority = part.Priority,
          });
          source++;
        }
      }

      return loaded;
    }

    /// <summary>
    /// Preserves the complete rotation policy for every route. Kept as a named seam for callers/tests that
    /// previously verified the retired rotation clamp; a common-line option never invents or removes policy.
    /// </summary>
    internal static int CommonLineRotationCode(int code, bool requiresMergedLines)
      => code;

    /// <summary>Can the engine fit this part inside a strip of this height? What it has to fit is the GROWN
    /// outline, in whichever of the part's orientations presents the smallest height.</summary>
    private static bool FitsStrip(Loaded part, double stripHeight)
    {
      double shortest = double.MaxValue;
      foreach ((double W, double H) extent in part.AngleExtents)
      {
        shortest = Math.Min(shortest, extent.H);
      }

      return shortest + part.FitSpacing <= stripHeight + 0.002;
    }

    /// <summary>Can this part go on a sheet of this size at all? The margin is measured against the part's
    /// own outline, never against the spacing shell it keeps for its neighbours, which is the rule the
    /// compactor, the acceptance check and the editor all use. For a discrete rotation set the answer is
    /// exact: the outline has to fit the usable rectangle in one of the orientations it is allowed, so a
    /// part refused here had no legal placement to lose. A free-rotation part is never refused, because the
    /// orientations it may take are not a list, and the sampled ones say nothing about the angle that would
    /// have fitted.</summary>
    private static bool FitsOnSheet(Loaded part, int w, int h, double margin)
    {
      if (part.Continuous)
      {
        return true;
      }

      // The same slack the acceptance rule allows, so the two cannot disagree about a part that lands
      // exactly on the margin.
      const double Tol = 0.002;
      double usableW = w - (2 * Math.Max(0, margin)) + Tol;
      double usableH = h - (2 * Math.Max(0, margin)) + Tol;
      foreach ((double W, double H) extent in part.AngleExtents)
      {
        if (extent.W <= usableW && extent.H <= usableH)
        {
          return true;
        }
      }

      return false;
    }

    /// <summary>Picks a batch from the pool up to ~1.3× the sheet area (extra choice for sparrow), keeping
    /// source variety for mixed jobs. Offers only parts that can actually go on this size of sheet, the
    /// ones with the fewest sizes left to choose from first, and returns an empty batch when none of them
    /// can go on it at all.</summary>
    private static Dictionary<int, int> SelectBatch(
      Dictionary<int, int> pool,
      Dictionary<int, Loaded> loadedById,
      IReadOnlyList<StockSize> sizes,
      int w,
      int h,
      double margin)
    {
      // Area of parts offered to the engine for one sheet. MORE than a sheetful on purpose: the engine is a
      // strip packer, so it arranges everything it is given and we keep whatever landed inside the sheet.
      // It was 1.3, which was calibrated against a search that packed loosely; a tighter search fits more per
      // unit of width and RUNS OUT OF PARTS before the sheet edge, which is how a longer search came to place
      // FEWER parts. Measured on the common-line strip job (7 x 36 parts, 120 x 60 sheet): at 1.3 the full
      // sheets took 26 each at the old budget and 25 at the new one, at 1.1 they took 25 either way, and at
      // 1.5 they take 26 at every budget tried. 1.7 is no better than 1.5 and costs a bigger pack.
      double cap = 1.5 * w * h;
      double sheetArea = (double)w * h;
      var batch = new Dictionary<int, int>();
      double acc = 0;
      int prevPriority = int.MinValue;
      int prevHomes = int.MinValue;

      // How many of the sizes STILL IN STOCK could hold each part at all. A part with two sizes left to
      // choose from has to be offered this sheet before one with three, or the sheet it depended on gets
      // cut with a part that had somewhere else to go and it comes back uncut: the operator's job lost two
      // parts to a sheet size they were the only ones that fitted, with two sheets of another size left
      // over. Without it the answer depended on which drawing happened to be imported first, which is not
      // something the row order should decide.
      var homesBySource = pool.ToDictionary(
        kv => kv.Key,
        kv => sizes.Count(size => size.HasStock && FitsOnSheet(loadedById[kv.Key], size.W, size.H, margin)));

      // Highest priority first (1 = highest, lower number wins) so preferred parts fill earlier
      // sheets and the leftover tail — what ends up unplaced when material runs out — is lowest priority.
      foreach (var kv in pool
        .OrderBy(kv => loadedById[kv.Key].Priority)
        .ThenBy(kv => homesBySource[kv.Key])
        .ThenBy(kv => kv.Key))
      {
        // A part that cannot go on THIS size of sheet must not spend its area budget either. The batch
        // used to be picked on area alone, so one part too big for the sheet in front of it filled the
        // batch of every candidate size, all of them packed nothing, and the sheet loop stopped with
        // parts that fit and sheets left to cut them on: the operator got seven parts back uncut when
        // only one of them was impossible.
        if (kv.Value <= 0 || !FitsOnSheet(loadedById[kv.Key], w, h, margin))
        {
          continue;
        }

        // Don't let a lower-priority part share a sheet that higher-priority parts already fill: once a
        // full sheet's worth of higher-priority area is queued, stop before crossing to a lower priority.
        // Keeps priority parts off the "left over" pile even though sparrow itself is priority-blind.
        // The same stop guards the parts with fewer sizes to choose from, and that is what RESERVES a
        // scarce size: while a sheetful of them is waiting, the ones with somewhere else to go are not
        // offered this sheet at all.
        int pr = loadedById[kv.Key].Priority;
        int homes = homesBySource[kv.Key];
        if ((pr > prevPriority || (pr == prevPriority && homes > prevHomes)) && acc >= sheetArea)
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
          prevHomes = homes;
        }

        if (acc >= cap)
        {
          break;
        }
      }

      // An empty batch now means one thing only: nothing left in the pool can go on this size of sheet.
      // Any part that fits is smaller than the sheet and so is always inside an area cap of one and a half
      // sheetfuls, which is why it can never be the cap that empties the batch. This used to force the
      // first part on anyway "so an oversize part gets a try", and that try cannot succeed: the caller
      // skips a size that places nothing either way, and now it does so without launching the engine.
      return batch;
    }

    /// <summary>Packs one batch onto one sheet, best-of-K with EARLY-STOP: races sparrow searches (fixed seeds)
    /// in waves and keeps the candidate that PLACES THE MOST parts (tie: denser, then lower seed). Stops
    /// launching more tries as soon as a whole wave fails to improve the best part-count — easy/uniform sheets
    /// converge in a wave or two, so they don't burn all `tries`; hard/variable sheets keep going up to `tries`.
    /// Packs ONLY in the user's chosen sheet orientation — the sheet is never auto-rotated (a 120×60 preset must
    /// render 120×60); to use the other orientation the operator picks that preset.</summary>
    /// <summary>
    /// <paramref name="error"/> aside, the seam counts in the return tuple are the WINNING candidate's
    /// only — every other candidate this call raced also snapped its own common-line edges, but was
    /// thrown away, and its counts have to go with it. The caller decides whether to add them to the
    /// running <see cref="DiagSeamsSnapped"/> / <see cref="DiagSeamsGivenUp"/> totals, because a caller
    /// that discards this result (the last-chance retry, when the first attempt placed more) must not
    /// add it either.
    /// </summary>
    private static (List<IPartPlacement> Placements, Dictionary<int, int> PlacedBySource, int PackW, int PackH, int SeamsSnapped, int SeamsGivenUp, List<CommonEdgeIntent> CommonEdges, IReadOnlyList<CommonLineFallbackNotice> CommonLineFallbacks) PackOneSheet(
      List<Loaded> loaded, Dictionary<int, int> batchQty, int sheetW, int sheetH, double margin, int budget, int workers, int hardTimeoutSec, int tries, string exe, CancellationToken cancel, Action<double> onDensity, double commonLineGap, double geometryTolerance, NestEffort effort, int commonLineMaxClusterParts, out string error)
    {
      error = null;
      var batch = loaded.Where(l => batchQty.TryGetValue(l.Source, out int q) && q > 0).ToList();
      if (batch.Count == 0)
      {
        return (null, null, sheetW, sheetH, 0, 0, new List<CommonEdgeIntent>(), Array.Empty<CommonLineFallbackNotice>());
      }

      // Capture once for the complete seed race. Each seed owns its mutable lookup dictionary,
      // while the captured catalog values are immutable and shared. Normal-only jobs do no DXF I/O.
      var capturedSources = new Dictionary<string, DxfContourCatalog>(StringComparer.OrdinalIgnoreCase);
      foreach (string source in batch.Where(part => part.RequiresMergedLines)
        .Select(part => part.Nfp.Name).Where(path => !string.IsNullOrWhiteSpace(path)
          && path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
      {
        cancel.ThrowIfCancellationRequested();
        try
        {
          string path = Path.GetFullPath(source);
          capturedSources[path] = DxfContourCatalog.Load(path, geometryTolerance);
        }
        catch (Exception ex) when (ex is IOException || ex is CommonLineValidationException || ex is ArgumentException)
        {
          // Preserve the existing fail-closed candidate/fallback handling for unsupported sources.
        }
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
        //
        // What is packed is not the part, it is the part GROWN by half its spacing on every side, and that
        // shell is the gap between two parts, not a second sheet margin. So a part within half a spacing of
        // filling the usable height cannot be packed at all, even though it fits the sheet by the rule
        // everything else here measures with (RasterCompact: "the sheet margin applies to the PART, not to
        // its clearance shell", and PlacementAcceptance.IsOutsideUsableArea, which is what the editor paints
        // red with). An operator's thirteen parts came back with seven uncut and seven sheets untouched
        // because parts 1/16in inside the usable height were turned away for their own neighbour clearance.
        //
        // Give that shell back, and the two ends land exactly on the margin: the bottom part anchors its
        // nominal contour at margin (see MapAndCompact) and the top one finishes at sheetH - margin.
        //
        // Only when the batch holds a part that needs it, though, because the strip height is an input to
        // the search and a taller one is a different nest for everybody, not just for the part that needed
        // the room. Measured on the mixed common-line job, 30 parts on a 120x60 sheet: giving the shell back
        // unconditionally leaves the pack flush against the margin, the direct planner then has nowhere to
        // put a cluster, and every one of its plans is rolled back. Twenty-one shared cuts became zero, and
        // stayed zero at five different sheet heights, so it is the flush pack and not one unlucky layout.
        // Mixed spacings take the largest shell; a part with a smaller one can then come out a fraction
        // proud of the margin, which DeferUnfit already judges by the real rule and holds over.
        double usableHeight = Math.Max(1.0, sheetH - (2 * margin));
        double edgeShell = batch.Max(l => l.FitSpacing);
        bool needsEdgeShell = batch.Any(l => !FitsStrip(l, usableHeight) && FitsStrip(l, usableHeight + edgeShell));
        double stripHeight = needsEdgeShell ? usableHeight + edgeShell : usableHeight;
        string inputPath = Path.Combine(workDir, "job.json");
        File.WriteAllText(inputPath, BuildJaguaJson("job", stripHeight, batch, batchQty));

        // WHICH seeds are raced against each other, and how many, decides the result — so the wave is a
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
        var winners = new List<(List<IPartPlacement> Pl, Dictionary<int, int> By, int Count, double SharedLength, double Extent, double Density, int Seed, int SeamsSnapped, int SeamsGivenUp, List<CommonEdgeIntent> CommonEdges, IReadOnlyList<CommonLineFallbackNotice> CommonLineFallbacks)>();
        int bestIndex = -1, launched = 0;
        string firstTryError = null;
        int packIndex = System.Threading.Interlocked.Increment(ref diagPackSeq);

        while (launched < tries && !cancel.IsCancellationRequested)
        {
          int waveSize = Math.Min(waveTries, tries - launched);
          var wPl = new List<IPartPlacement>[waveSize];
          var wBy = new Dictionary<int, int>[waveSize];
          var wEdges = new List<CommonEdgeIntent>[waveSize];
          var wFallbacks = new IReadOnlyList<CommonLineFallbackNotice>[waveSize];
          var wSt = new (int Count, double Density, int Seed, int SeamsSnapped, int SeamsGivenUp)?[waveSize];
          int startSeed = launched + 1;
          var options = new ParallelOptions { MaxDegreeOfParallelism = maxConc, CancellationToken = cancel };
          Parallel.For(0, waveSize, options, j =>
          {
            int seed = startSeed + j;
            string tryDir = Path.Combine(workDir, "try" + seed.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(tryDir);
            var diagRun = System.Diagnostics.Stopwatch.StartNew();
            string outJson = RunSparrowOnce(exe, tryDir, inputPath, budget, workers, hardTimeoutSec, seed, cancel, aggDensity, out string tryErr);
            if (tryErr != null)
            {
              // Not fatal on its own: the other races may still return something. But when they ALL fail the
              // caller used to be told only "no solution", which is the least useful thing that could be said
              // about a watchdog kill or a crashed engine, so keep the first reason to hand.
              System.Threading.Interlocked.CompareExchange(ref firstTryError, tryErr, null);
            }

            System.Threading.Interlocked.Add(ref DiagSparrowMs, diagRun.ElapsedMilliseconds);
            if (outJson == null || cancel.IsCancellationRequested)
            {
              return;
            }

            var diagMap = System.Diagnostics.Stopwatch.StartNew();
            var (pl, by, seamsSnapped, seamsGivenUp, commonEdges, commonLineFallbacks) = MapAndCompact(
              outJson,
              batch,
              sheetW,
              sheetH,
              margin,
              commonLineGap,
              geometryTolerance,
              effort,
              commonLineMaxClusterParts,
              cancel,
              capturedSources,
              $"pack={packIndex} seed={seed}");
            System.Threading.Interlocked.Add(ref DiagMapMs, diagMap.ElapsedMilliseconds);
            wPl[j] = pl;
            wBy[j] = by;
            wEdges[j] = commonEdges;
            wFallbacks[j] = commonLineFallbacks;
            wSt[j] = (pl.Count, ParseDensity(outJson), seed, seamsSnapped, seamsGivenUp);
          });

          launched += waveSize;
          System.Threading.Interlocked.Increment(ref DiagWaves);
          System.Threading.Interlocked.Add(ref DiagTries, waveSize);
          for (int j = 0; j < waveSize; j++)
          {
            if (wSt[j].HasValue)
            {
              var (ex, ey) = Extent(wPl[j]);
              double sharedLength = CommonLineLength(wEdges[j], commonLineGap, geometryTolerance, capturedSources);
              winners.Add((wPl[j], wBy[j], wSt[j].Value.Count, sharedLength, longAxisIsX ? ex : ey, wSt[j].Value.Density, wSt[j].Value.Seed, wSt[j].Value.SeamsSnapped, wSt[j].Value.SeamsGivenUp, wEdges[j], wFallbacks[j]));
              lock (DiagCandidates)
              {
                DiagCandidates.Add(FormattableString.Invariant(
                  $"{packIndex},{wSt[j].Value.Seed},{wSt[j].Value.Count},{wSt[j].Value.Density:F5},{ex:F3},{ey:F3}"));
              }
            }
          }

          // Plateau: stop once a full wave fails to produce a better candidate than the ones already in
          // hand, judged by the SAME ordering that will pick the winner (parts, common-line length,
          // then extent and density).
          // It used to count parts only, and parts placed is a metric that stops moving almost at once: a
          // wave that was still tightening the pack read as no progress, so the search was cut while the
          // block was still too wide, which is the whole complaint this change exists to answer.
          int waveBest = winners.Count == 0
            ? -1
            : PickBest(winners.Select(w => (w.Count, w.SharedLength, w.Extent, w.Density, w.Seed)).ToList());
          if (bestIndex >= 0 && waveBest == bestIndex)
          {
            break;
          }

          bestIndex = waveBest;
        }

        if (cancel.IsCancellationRequested)
        {
          error = "Cancelled.";
          return (null, null, sheetW, sheetH, 0, 0, new List<CommonEdgeIntent>(), Array.Empty<CommonLineFallbackNotice>());
        }

        if (winners.Count == 0)
        {
          error = firstTryError == null
            ? "The nesting engine produced no solution."
            : "The nesting engine produced no solution: " + firstTryError;
          return (null, null, sheetW, sheetH, 0, 0, new List<CommonEdgeIntent>(), Array.Empty<CommonLineFallbackNotice>());
        }

        int bi = PickBest(winners.Select(w => (w.Count, w.SharedLength, w.Extent, w.Density, w.Seed)).ToList());
        var sel = winners[bi];
        return (sel.Pl, sel.By, sheetW, sheetH, sel.SeamsSnapped, sel.SeamsGivenUp, sel.CommonEdges, sel.CommonLineFallbacks);
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
    private static (List<IPartPlacement> Placements, Dictionary<int, int> PlacedBySource, int SeamsSnapped, int SeamsGivenUp, List<CommonEdgeIntent> CommonEdges, IReadOnlyList<CommonLineFallbackNotice> CommonLineFallbacks) MapAndCompact(
      string outputJson, List<Loaded> batch, double sheetWin, double sheetHin, double margin, double commonLineGap, double geometryTolerance, NestEffort effort, int commonLineMaxClusterParts, CancellationToken cancel,
      IReadOnlyDictionary<string, DxfContourCatalog> capturedSources,
      string diagnosticLabel)
    {
      cancel.ThrowIfCancellationRequested();
      var sourceCatalogs = capturedSources.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
      using var doc = JsonDocument.Parse(outputJson);
      var placed = doc.RootElement.GetProperty("solution").GetProperty("layout").GetProperty("placed_items");
      var spacingById = batch.ToDictionary(l => l.Source, l => l.EffSpacing);
      var outlineById = batch.ToDictionary(l => l.Source, l => l.Nfp);
      var ccById = batch.ToDictionary(l => l.Source, l => (l.Cc, l.ShareKey, l.CommonCuttingDomain));
      var directRulesById = batch.ToDictionary(
        l => l.Source,
        l => (l.Cc, l.ShareKey, l.CommonCuttingDomain, l.RequiresMergedLines, l.EffSpacing));
      var directOrientationProfilesById = batch.ToDictionary(
        l => l.Source,
        l => new DirectOrientationProfile(l.Nfp, l.Angles, l.Continuous));

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
        return (new List<IPartPlacement>(), new Dictionary<int, int>(), 0, 0, new List<CommonEdgeIntent>(), Array.Empty<CommonLineFallbackNotice>());
      }

      // Pre-anchor min → margin so compaction starts on-sheet, then gravity-compact to the corner.
      double minX = all.Min(p => p.PlacedPart.MinX);
      double minY = all.Min(p => p.PlacedPart.MinY);
      foreach (var pp in all)
      {
        pp.X += margin - minX;
        pp.Y += margin - minY;
      }

      // Baseline compaction always keeps normal spacing. Compatibility alone is not a selected common
      // edge and therefore cannot collapse a pair's halo.
      var compactItems = all.Select(pp => new CompactItem
      {
        Poly = outlineById.TryGetValue(pp.Source, out var tooling) && tooling != null
          ? tooling.Rotate(pp.Rotation)   // same frame as pp.Part: rotated about the origin, mirror baked in
          : pp.Part,
        X = pp.X,
        Y = pp.Y,
        Spacing = spacingById.TryGetValue(pp.Source, out double sp) ? sp : 0,
        Cc = ccById.TryGetValue(pp.Source, out var cc) ? cc.Cc : CommonCuttingMode.None,
        ShareKey = ccById.TryGetValue(pp.Source, out var sk) ? sk.ShareKey : 0,
        CommonCuttingDomain = ccById.TryGetValue(pp.Source, out var domain) ? domain.CommonCuttingDomain : string.Empty,
      }).ToList();
      RasterCompact.Compact(compactItems, sheetWin, sheetHin, margin, cancel: cancel);
      for (int k = 0; k < all.Count; k++)
      {
        all[k].X = compactItems[k].X;
        all[k].Y = compactItems[k].Y;
      }

      IPartPlacement[] baselinePlacements = all.ToArray();
      double[] baselineX = baselinePlacements.Select(p => p.X).ToArray();
      double[] baselineY = baselinePlacements.Select(p => p.Y).ToArray();
      double[] baselineRotation = baselinePlacements.Select(p => p.Rotation).ToArray();
      DirectCommonLineSnapResult direct = SnapDirectCommonLineEdges(
        all,
        directRulesById,
        directOrientationProfilesById,
        sheetWin,
        sheetHin,
        margin,
        commonLineGap,
        geometryTolerance,
        effort,
        commonLineMaxClusterParts,
        cancel,
        sourceCatalogs,
        diagnosticLabel: diagnosticLabel);

      // Once an intentional graph exists, compact each connected component as one rigid body. Internal
      // edges keep CommonLineGap while every external pair still sees its ordinary EffSpacing.
      if (direct.Edges.Count > 0)
      {
        for (int k = 0; k < compactItems.Count; k++)
        {
          compactItems[k].X = all[k].X;
          compactItems[k].Y = all[k].Y;
          compactItems[k].Poly = outlineById.TryGetValue(all[k].Source, out var tooling) && tooling != null
            ? tooling.Rotate(all[k].Rotation)
            : all[k].Part;
          compactItems[k].PlacementId = k;
          compactItems[k].CommonEdgePeers = new HashSet<int>();
        }

        foreach (CommonEdgeIntent edge in direct.Edges)
        {
          int a = all.IndexOf(edge.PartA);
          int b = all.IndexOf(edge.PartB);
          if (a >= 0 && b >= 0)
          {
            compactItems[a].CommonEdgePeers.Add(b);
            compactItems[b].CommonEdgePeers.Add(a);
          }
        }

        RasterCompact.Compact(
          compactItems,
          sheetWin,
          sheetHin,
          margin,
          groups: direct.Groups,
          cancel: cancel,
          commonLineGap: commonLineGap);
        for (int k = 0; k < all.Count; k++)
        {
          all[k].X = compactItems[k].X;
          all[k].Y = compactItems[k].Y;
        }

        bool recordsStillValid = direct.Edges.All(edge => TryCreateCommonEdgeRecord(
          edge,
          commonLineGap,
          geometryTolerance,
          out _, sourceCatalogs));
        bool toolingStillValid = OutlinesClearEveryone(all, outlineById, null);
        bool layoutStillValid = DirectLayoutIsValid(
          all,
          direct.Edges,
          directRulesById,
          sheetWin,
          sheetHin,
          margin,
          commonLineGap,
          geometryTolerance,
          out _,
          out _,
          cancel,
          sourceCatalogs);
        bool topologyStillValid = CommonLineTopologyIsValid(
          all,
          direct.Edges,
          sheetWin,
          sheetHin,
          commonLineGap,
          geometryTolerance,
          out string topologyFailure,
          cancel,
          sourceCatalogs);
        if (!recordsStillValid
            || !toolingStillValid
            || !layoutStillValid
            || !topologyStillValid)
        {
          if (!topologyStillValid)
          {
            AddCommonLineFallback(
              direct,
              direct.Edges.SelectMany(edge => new[] { edge.PartA, edge.PartB }),
              topologyFailure);
          }

          // Never keep a tight pair after losing its record. Restore the ordinary-spacing baseline for
          // the whole direct pass; this candidate remains safe even though it gives up the optimisation.
          for (int k = 0; k < all.Count; k++)
          {
            IPartPlacement original = baselinePlacements[k];
            original.X = baselineX[k];
            original.Y = baselineY[k];
            original.Rotation = baselineRotation[k];
            all[k] = original;
            compactItems[k].X = baselineX[k];
            compactItems[k].Y = baselineY[k];
            compactItems[k].Poly = outlineById.TryGetValue(original.Source, out var tooling) && tooling != null
              ? tooling.Rotate(baselineRotation[k])
              : original.Part;
          }

          direct.GivenUp++;
          direct.Edges.Clear();
          Array.Clear(direct.Groups, 0, direct.Groups.Length);
        }
      }

      // Only the WINNING candidate's numbers reach the running totals. This runs once per racing seed in
      // the wave and only one candidate is ever kept, so counting every one of them would report seams
      // that were never actually cut. The counts go back through the return tuple and PackOneSheet adds
      // just the winner's.
      int seamsSnapped = 0, seamsGivenUp = direct.GivenUp;
      List<List<int>> chains = MergeCommonLineChains(all, direct.Edges);

      var placements = new List<IPartPlacement>();
      var placedBySource = new Dictionary<int, int>();
      foreach (var pp in all)
      {
        if (IsOutsideSheet(pp, sheetWin, sheetHin))
        {
          System.Threading.Interlocked.Increment(ref DiagCulled);
          continue; // past the sheet edge → not placed on this sheet (stays in the pool)
        }

        placements.Add(pp);
        placedBySource[pp.Source] = placedBySource.TryGetValue(pp.Source, out int n) ? n + 1 : 1;
      }

      RemoveOrphanedCommonLineGroups(placements, direct.Edges, placedBySource);

      return (placements, placedBySource, seamsSnapped, seamsGivenUp, direct.Edges, direct.Fallbacks);
    }

    /// <summary>
    /// Culling one endpoint invalidates its whole rigid cluster. Remove every surviving member too, so a
    /// part is never left at CommonLineGap after the relationship that authorises that gap disappeared.
    /// </summary>
    private static void RemoveOrphanedCommonLineGroups(
      List<IPartPlacement> placements,
      List<CommonEdgeIntent> intents,
      Dictionary<int, int> placedBySource)
    {
      if (intents == null || intents.Count == 0)
      {
        return;
      }

      var present = new HashSet<IPartPlacement>(placements);
      var invalid = new HashSet<IPartPlacement>();
      foreach (CommonEdgeIntent edge in intents)
      {
        if (!present.Contains(edge.PartA) || !present.Contains(edge.PartB))
        {
          invalid.Add(edge.PartA);
          invalid.Add(edge.PartB);
        }
      }

      bool grew;
      do
      {
        grew = false;
        foreach (CommonEdgeIntent edge in intents)
        {
          if (invalid.Contains(edge.PartA) || invalid.Contains(edge.PartB))
          {
            grew |= invalid.Add(edge.PartA);
            grew |= invalid.Add(edge.PartB);
          }
        }
      }
      while (grew);

      if (invalid.Count == 0)
      {
        return;
      }

      foreach (IPartPlacement placement in placements.Where(invalid.Contains).ToList())
      {
        placements.Remove(placement);
        if (placedBySource.TryGetValue(placement.Source, out int count))
        {
          if (count <= 1)
          {
            placedBySource.Remove(placement.Source);
          }
          else
          {
            placedBySource[placement.Source] = count - 1;
          }
        }

        System.Threading.Interlocked.Increment(ref DiagCulled);
      }

      intents.RemoveAll(e => invalid.Contains(e.PartA) || invalid.Contains(e.PartB));
    }

    private static List<List<int>> MergeCommonLineChains(
      IReadOnlyList<IPartPlacement> placements,
      IReadOnlyList<CommonEdgeIntent> intents)
    {
      int PlacementIndex(IPartPlacement candidate)
      {
        for (int i = 0; i < placements.Count; i++)
        {
          if (ReferenceEquals(placements[i], candidate))
          {
            return i;
          }
        }

        return -1;
      }

      var result = new List<List<int>>();
      var seen = new HashSet<IPartPlacement>();
      foreach (CommonEdgeIntent edge in intents ?? Array.Empty<CommonEdgeIntent>())
      {
        if (seen.Contains(edge.PartA))
        {
          continue;
        }

        HashSet<IPartPlacement> component = CommonEdgeComponent(edge.PartA, intents);
        foreach (IPartPlacement member in component)
        {
          seen.Add(member);
        }

        result.Add(component
          .Select(PlacementIndex)
          .Where(i => i >= 0)
          .Distinct()
          .ToList());
      }

      return result;
    }

    private sealed class DirectPlacementOption
    {
      public DirectPlacementOption(int part, int pose, INfp poly, double rotation, double x, double y)
      {
        this.Part = part;
        this.Pose = pose;
        this.Poly = poly;
        this.Rotation = rotation;
        this.X = x;
        this.Y = y;
      }

      public int Part { get; }

      public int Pose { get; }

      public INfp Poly { get; }

      public double Rotation { get; }

      public double X { get; }

      public double Y { get; }

      public List<DirectEdge> Edges { get; } = new List<DirectEdge>();
    }

    private readonly struct DirectEdge
    {
      public DirectEdge(
        int part,
        int pose,
        int index,
        double ax,
        double ay,
        double bx,
        double by,
        double ux,
        double uy,
        double nx,
        double ny,
        double length)
      {
        this.Part = part;
        this.Pose = pose;
        this.Index = index;
        this.Ax = ax;
        this.Ay = ay;
        this.Bx = bx;
        this.By = by;
        this.Ux = ux;
        this.Uy = uy;
        this.Nx = nx;
        this.Ny = ny;
        this.Length = length;
      }

      public int Part { get; }

      public int Pose { get; }

      public int Index { get; }

      public double Ax { get; }

      public double Ay { get; }

      public double Bx { get; }

      public double By { get; }

      public double Ux { get; }

      public double Uy { get; }

      public double Nx { get; }

      public double Ny { get; }

      public double Length { get; }
    }

    private readonly struct DirectCandidate
    {
      public DirectCandidate(
        int a,
        int b,
        int poseA,
        int poseB,
        int edgeA,
        int edgeB,
        double edgeAStart,
        double edgeAEnd,
        double edgeBStart,
        double edgeBEnd,
        double dx,
        double dy,
        double length,
        double travel,
        CommonBoundarySpan spanA = null,
        CommonBoundarySpan spanB = null)
      {
        this.A = a;
        this.B = b;
        this.PoseA = poseA;
        this.PoseB = poseB;
        this.EdgeA = edgeA;
        this.EdgeB = edgeB;
        this.EdgeAStart = edgeAStart;
        this.EdgeAEnd = edgeAEnd;
        this.EdgeBStart = edgeBStart;
        this.EdgeBEnd = edgeBEnd;
        this.Dx = dx;
        this.Dy = dy;
        this.Length = length;
        this.Travel = travel;
        this.SpanA = spanA;
        this.SpanB = spanB;
      }

      public int A { get; }

      public int B { get; }

      public int PoseA { get; }

      public int PoseB { get; }

      public int EdgeA { get; }

      public int EdgeB { get; }

      public double EdgeAStart { get; }

      public double EdgeAEnd { get; }

      public double EdgeBStart { get; }

      public double EdgeBEnd { get; }

      public double Dx { get; }

      public double Dy { get; }

      public double Length { get; }

      public double Travel { get; }

      public CommonBoundarySpan SpanA { get; }

      public CommonBoundarySpan SpanB { get; }

      public int PoseChanges => (this.PoseA == 0 ? 0 : 1) + (this.PoseB == 0 ? 0 : 1);

      public bool IsFullEdge => this.EdgeAStart <= 1e-9
        && this.EdgeAEnd >= 1.0 - 1e-9
        && this.EdgeBStart <= 1e-9
        && this.EdgeBEnd >= 1.0 - 1e-9;
    }

    /// <summary>
    /// Stable occupancy identity for one candidate boundary primitive. Polygon LINE edges use their NFP
    /// index; exact DXF spans use the source contour/primitive pair so unrelated primitives can never
    /// consume one another's parametric interval.
    /// </summary>
    private readonly struct DirectBoundaryKey : IEquatable<DirectBoundaryKey>
    {
      private DirectBoundaryKey(int part, int edge, int contour, int primitive, bool isSource)
      {
        this.Part = part;
        this.Edge = edge;
        this.Contour = contour;
        this.Primitive = primitive;
        this.IsSource = isSource;
      }

      internal int Part { get; }

      internal int Edge { get; }

      internal int Contour { get; }

      internal int Primitive { get; }

      internal bool IsSource { get; }

      internal static DirectBoundaryKey Create(int part, int edge, CommonBoundarySpan span)
        => span == null
          ? new DirectBoundaryKey(part, edge, -1, -1, false)
          : new DirectBoundaryKey(part, -1, span.ContourIndex, span.PrimitiveIndex, true);

      public bool Equals(DirectBoundaryKey other)
        => this.Part == other.Part
          && this.Edge == other.Edge
          && this.Contour == other.Contour
          && this.Primitive == other.Primitive
          && this.IsSource == other.IsSource;

      public override bool Equals(object obj) => obj is DirectBoundaryKey other && this.Equals(other);

      public override int GetHashCode()
        => HashCode.Combine(this.Part, this.Edge, this.Contour, this.Primitive, this.IsSource);
    }

    /// <summary>
    /// Immutable-on-branch search state for the deterministic direct-common-line beam. It owns pose
    /// assignments, a disjoint-set forest and interval occupancy independently of the live placements;
    /// losing branches therefore cannot leak a rotation, translation or tight clearance into the nest.
    /// </summary>
    private sealed class DirectPlanState
    {
      private readonly int[] parents;
      private readonly byte[] ranks;
      private readonly int[] componentSizes;
      private readonly int[] poses;
      private readonly Dictionary<DirectBoundaryKey, List<(double Start, double End)>> occupied;
      private readonly int maxClusterParts;

      internal DirectPlanState(int partCount, int maxClusterParts)
      {
        this.parents = Enumerable.Range(0, partCount).ToArray();
        this.ranks = new byte[partCount];
        this.componentSizes = Enumerable.Repeat(1, partCount).ToArray();
        this.poses = Enumerable.Repeat(-1, partCount).ToArray();
        this.occupied = new Dictionary<DirectBoundaryKey, List<(double, double)>>();
        this.maxClusterParts = Math.Max(1, maxClusterParts);
        this.Plan = new List<DirectCandidate>();
        this.CandidateIndexes = new List<int>();
      }

      private DirectPlanState(DirectPlanState source)
      {
        this.parents = (int[])source.parents.Clone();
        this.ranks = (byte[])source.ranks.Clone();
        this.componentSizes = (int[])source.componentSizes.Clone();
        this.poses = (int[])source.poses.Clone();
        this.occupied = source.occupied.ToDictionary(
          pair => pair.Key,
          pair => new List<(double, double)>(pair.Value));
        this.Plan = new List<DirectCandidate>(source.Plan);
        this.CandidateIndexes = new List<int>(source.CandidateIndexes);
        this.TotalLength = source.TotalLength;
        this.TotalTravel = source.TotalTravel;
        this.maxClusterParts = source.maxClusterParts;
      }

      internal List<DirectCandidate> Plan { get; }

      internal List<int> CandidateIndexes { get; }

      internal double TotalLength { get; private set; }

      internal double TotalTravel { get; private set; }

      internal int PoseChanges => this.poses.Count(pose => pose > 0);

      internal bool HasSamePoses(DirectPlanState other)
      {
        if (other == null || other.poses.Length != this.poses.Length)
        {
          return false;
        }

        for (int i = 0; i < this.poses.Length; i++)
        {
          if (this.poses[i] != other.poses[i])
          {
            return false;
          }
        }

        return true;
      }

      internal DirectPlanState Include(DirectCandidate candidate, int candidateIndex)
      {
        if ((this.poses[candidate.A] >= 0 && this.poses[candidate.A] != candidate.PoseA)
          || (this.poses[candidate.B] >= 0 && this.poses[candidate.B] != candidate.PoseB)
          || !this.SpanAvailable(
            DirectBoundaryKey.Create(candidate.A, candidate.EdgeA, candidate.SpanA),
            candidate.EdgeAStart,
            candidate.EdgeAEnd)
          || !this.SpanAvailable(
            DirectBoundaryKey.Create(candidate.B, candidate.EdgeB, candidate.SpanB),
            candidate.EdgeBStart,
            candidate.EdgeBEnd))
        {
          return null;
        }

        int rootA = this.Find(candidate.A);
        int rootB = this.Find(candidate.B);
        if (rootA == rootB
          || this.componentSizes[rootA] + this.componentSizes[rootB] > this.maxClusterParts)
        {
          // Cycles carry no additional rigid-motion constraint; TryApplyPlan discovers and records every
          // coincident cycle edge after the selected forest has placed the component.
          return null;
        }

        var next = new DirectPlanState(this);
        next.Join(candidate.A, candidate.B);
        next.poses[candidate.A] = candidate.PoseA;
        next.poses[candidate.B] = candidate.PoseB;
        next.Occupy(
          DirectBoundaryKey.Create(candidate.A, candidate.EdgeA, candidate.SpanA),
          candidate.EdgeAStart,
          candidate.EdgeAEnd);
        next.Occupy(
          DirectBoundaryKey.Create(candidate.B, candidate.EdgeB, candidate.SpanB),
          candidate.EdgeBStart,
          candidate.EdgeBEnd);
        next.Plan.Add(candidate);
        next.CandidateIndexes.Add(candidateIndex);
        next.TotalLength += candidate.Length;
        next.TotalTravel += candidate.Travel;
        return next;
      }

      private bool SpanAvailable(DirectBoundaryKey key, double start, double end)
        => !this.occupied.TryGetValue(key, out var spans)
          || spans.All(span => Math.Max(start, span.Start) >= Math.Min(end, span.End) - 1e-9);

      private void Occupy(DirectBoundaryKey key, double start, double end)
      {
        if (!this.occupied.TryGetValue(key, out var spans))
        {
          this.occupied[key] = spans = new List<(double, double)>();
        }

        spans.Add((start, end));
      }

      private int Find(int value)
      {
        while (this.parents[value] != value)
        {
          value = this.parents[value];
        }

        return value;
      }

      private void Join(int first, int second)
      {
        int a = this.Find(first);
        int b = this.Find(second);
        if (a == b)
        {
          return;
        }

        if (this.ranks[a] < this.ranks[b])
        {
          (a, b) = (b, a);
        }

        this.parents[b] = a;
        this.componentSizes[a] += this.componentSizes[b];
        this.componentSizes[b] = 0;
        if (this.ranks[a] == this.ranks[b])
        {
          this.ranks[a]++;
        }
      }
    }

    private enum DirectPlanFailure
    {
      None,
      EdgeGeometry,
      Layout,
      Topology,
    }

    /// <summary>
    /// Resolves the persisted common-line cluster policy for one candidate sheet. Auto is deliberately
    /// deterministic and large enough for the required 10x10 grid; a manual 2..500 value remains a hard
    /// upper bound. The sheet's own part count avoids allocating meaningless capacity.
    /// </summary>
    internal static int ResolveCommonLineMaxClusterParts(int configuredLimit, int partCount)
    {
      int normalized = CommonLineDefaults.NormalizeMaxClusterParts(configuredLimit);
      int requested = normalized == CommonLineDefaults.AutoMaxClusterParts ? 100 : normalized;
      return Math.Max(1, Math.Min(Math.Max(0, partCount), requested));
    }

    /// <summary>
    /// Creates direct common-line clusters from the placements exactly as supplied. This overload keeps
    /// the original translation-only contract for tests and callers that do not own source orientations.
    /// </summary>
    internal static DirectCommonLineSnapResult SnapDirectCommonLineEdges(
      List<IPartPlacement> all,
      IReadOnlyDictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> rulesBySource,
      double sheetW,
      double sheetH,
      double margin,
      double commonLineGap,
      double geometryTolerance,
      NestEffort effort = NestEffortDefaults.Default,
      int commonLineMaxClusterParts = CommonLineDefaults.AutoMaxClusterParts)
      => SnapDirectCommonLineEdges(
        all,
        rulesBySource,
        null,
        sheetW,
        sheetH,
        margin,
        commonLineGap,
        geometryTolerance,
        effort,
        commonLineMaxClusterParts);

    /// <summary>
    /// Creates direct common-line clusters deliberately from straight NFP intervals and exact circular
    /// source-DXF spans. Each placement contributes every explicitly permitted discrete pose, or bounded
    /// deterministic poses under a continuous policy. The beam ranks projected material footprint before
    /// saved cut length; every finalist is applied atomically and rejected candidates restore the baseline.
    /// </summary>
    internal static DirectCommonLineSnapResult SnapDirectCommonLineEdges(
      List<IPartPlacement> all,
      IReadOnlyDictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> rulesBySource,
      IReadOnlyDictionary<int, DirectOrientationProfile> orientationProfilesBySource,
      double sheetW,
      double sheetH,
      double margin,
      double commonLineGap,
      double geometryTolerance,
      NestEffort effort = NestEffortDefaults.Default,
      int commonLineMaxClusterParts = CommonLineDefaults.AutoMaxClusterParts,
      CancellationToken cancel = default,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null,
      Action<string> onStage = null,
      string diagnosticLabel = null)
    {
      cancel.ThrowIfCancellationRequested();
      IPartPlacement[] originals = all?.ToArray() ?? Array.Empty<IPartPlacement>();
      var transforms = originals.Select(p => (p.X, p.Y, p.Rotation, p.Id)).ToArray();
      sourceCatalogs ??= new Dictionary<string, DxfContourCatalog>(StringComparer.OrdinalIgnoreCase);
      var watch = Stopwatch.StartNew();
      var stages = new Dictionary<string, long>();
      var verdictCache = new CommonEdgeClearance.PairVerdictCache();
      string stage = "sources";
      long stageStart = 0;
      void ReportStage(string next)
      {
        long now = watch.ElapsedTicks;
        stages[stage] = stages.GetValueOrDefault(stage) + now - stageStart;
        stageStart = now;
        stage = next;
        onStage?.Invoke(next);
        cancel.ThrowIfCancellationRequested();
      }

      try
      {
        var result = SnapDirectCommonLineEdgesCore(all, rulesBySource, orientationProfilesBySource,
          sheetW, sheetH, margin, commonLineGap, geometryTolerance, effort, commonLineMaxClusterParts,
          cancel, sourceCatalogs, ReportStage, verdictCache);
        ReportStage("source-revalidation");
        if (result.Edges.Count > 0)
        {
          foreach (var source in sourceCatalogs)
          {
            cancel.ThrowIfCancellationRequested();
            if (DxfContourCatalog.Load(source.Key, geometryTolerance).SourceFingerprint != source.Value.SourceFingerprint)
            {
              throw new CommonLineValidationException("A common-line source changed during nesting; re-nest the updated drawing.");
            }
          }
        }

        return result;
      }
      catch
      {
        // Provisional poses must never escape on cancellation or an invalidated source.
        for (int i = 0; i < originals.Length; i++)
        {
          originals[i].X = transforms[i].X;
          originals[i].Y = transforms[i].Y;
          originals[i].Rotation = transforms[i].Rotation;
          if (originals[i] is PartPlacement concrete)
          {
            concrete.Id = transforms[i].Id;
          }

          all[i] = originals[i];
        }

        throw;
      }
      finally
      {
        stages[stage] = stages.GetValueOrDefault(stage) + watch.ElapsedTicks - stageStart;
        DiagCommonLineTimings.Enqueue(diagnosticLabel + " parts=" + originals.Length
          + " catalogs=" + sourceCatalogs.Count + " pair-hits=" + verdictCache.Hits
          + " pair-misses=" + verdictCache.Misses + " " + string.Join(" ",
          stages.Select(pair => pair.Key + "=" + (pair.Value * 1000 / Stopwatch.Frequency) + "ms")));
        while (DiagCommonLineTimings.Count > 64)
        {
          DiagCommonLineTimings.TryDequeue(out _);
        }
      }
    }

    private static DirectCommonLineSnapResult SnapDirectCommonLineEdgesCore(
      List<IPartPlacement> all,
      IReadOnlyDictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> rulesBySource,
      IReadOnlyDictionary<int, DirectOrientationProfile> orientationProfilesBySource,
      double sheetW,
      double sheetH,
      double margin,
      double commonLineGap,
      double geometryTolerance,
      NestEffort effort,
      int commonLineMaxClusterParts,
      CancellationToken cancel,
      IDictionary<string, DxfContourCatalog> sourceCatalogs,
      Action<string> reportStage,
      CommonEdgeClearance.PairVerdictCache verdictCache)
    {
      var result = new DirectCommonLineSnapResult { Groups = new int[all?.Count ?? 0] };
      if (all == null || all.Count < 2 || rulesBySource == null
          || !double.IsFinite(commonLineGap) || commonLineGap <= 0
          || !double.IsFinite(geometryTolerance) || geometryTolerance <= 0
          || commonLineGap <= geometryTolerance)
      {
        return result;
      }

      reportStage("poses");
      const double AngularEpsilonDegrees = 1e-7;
      int n = all.Count;
      int maxClusterParts = ResolveCommonLineMaxClusterParts(commonLineMaxClusterParts, n);
      for (int i = 0; i < n; i++)
      {
        cancel.ThrowIfCancellationRequested();
        // The core factory refuses duplicate ids. Candidate ids are local and are replaced with the final
        // result-wide ids immediately before SheetPlacement is built.
        if (all[i] is PartPlacement concrete)
        {
          concrete.Id = i;
        }
      }

      IPartPlacement[] originalPlacements = all.ToArray();
      double[] originalX = originalPlacements.Select(p => p.X).ToArray();
      double[] originalY = originalPlacements.Select(p => p.Y).ToArray();
      double[] originalRotation = originalPlacements.Select(p => p.Rotation).ToArray();

      if (!DirectLayoutIsValid(
            all,
            Array.Empty<CommonEdgeIntent>(),
            rulesBySource,
            sheetW,
            sheetH,
            margin,
            commonLineGap,
            geometryTolerance,
            out string baselineFailure,
            out _,
            cancel,
            sourceCatalogs,
            verdictCache))
      {
        RecordDirectFailure(0, DirectPlanFailure.Layout, "baseline " + baselineFailure);
      }

      double NormalizeAngle(double angle)
      {
        double normalized = angle % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
      }

      double AngleDistance(double first, double second)
      {
        double difference = Math.Abs(NormalizeAngle(first) - NormalizeAngle(second));
        return Math.Min(difference, 360.0 - difference);
      }

      bool MayShare(int a, int b)
      {
        if (!rulesBySource.TryGetValue(originalPlacements[a].Source, out var ra)
            || !rulesBySource.TryGetValue(originalPlacements[b].Source, out var rb)
            || !ra.RequiresMergedLines
            || !rb.RequiresMergedLines)
        {
          return false;
        }

        return CommonCuttingCompatibility.MayShare(
          ra.CommonCuttingDomain,
          ra.Cc,
          ra.ShareKey,
          rb.CommonCuttingDomain,
          rb.Cc,
          rb.ShareKey);
      }

      void RestoreOriginalPlacements()
      {
        for (int i = 0; i < n; i++)
        {
          cancel.ThrowIfCancellationRequested();
          IPartPlacement original = originalPlacements[i];
          original.X = originalX[i];
          original.Y = originalY[i];
          original.Rotation = originalRotation[i];
          all[i] = original;
        }
      }

      int maxContinuousPoses = effort switch
      {
        NestEffort.Best => 96,
        NestEffort.Normal => 48,
        _ => 24,
      };
      var optionsByPart = new List<DirectPlacementOption>[n];
      var orientationProfileByPart = new DirectOrientationProfile[n];
      var currentPosePermitted = new bool[n];
      var exactCatalogByPart = new DxfContourCatalog[n];
      var exactCatalogChecked = new bool[n];
      var exactArcsByPose = new Dictionary<(int Part, long Rotation), IReadOnlyList<(CommonBoundarySpan Span, DxfResolvedBoundarySpan Resolved)>>();

      bool TryGetExactCatalog(int partIndex, out DxfContourCatalog catalog)
      {
        if (!exactCatalogChecked[partIndex])
        {
          exactCatalogChecked[partIndex] = true;
          string sourcePath = originalPlacements[partIndex]?.Part?.Name;
          if (!string.IsNullOrWhiteSpace(sourcePath))
          {
            try
            {
              string fullPath = Path.GetFullPath(sourcePath);
              if (!sourceCatalogs.TryGetValue(fullPath, out DxfContourCatalog captured))
              {
                captured = DxfContourCatalog.Load(fullPath, geometryTolerance);
                sourceCatalogs[fullPath] = captured;
              }

              exactCatalogByPart[partIndex] = captured;
            }
            catch
            {
              // Exact source geometry is optional during proposal generation. A missing/stale catalog
              // cannot authorize a reduced gap and is reported as a fail-closed fallback below.
            }
          }
        }

        catalog = exactCatalogByPart[partIndex];
        return catalog != null;
      }

      IReadOnlyList<(CommonBoundarySpan Span, DxfResolvedBoundarySpan Resolved)> ExactArcsAt(
        int partIndex,
        double rotation)
      {
        long rotationKey = (long)Math.Round(NormalizeAngle(rotation) * 1_000_000D);
        if (exactArcsByPose.TryGetValue((partIndex, rotationKey), out var cached))
        {
          return cached;
        }

        if (!TryGetExactCatalog(partIndex, out DxfContourCatalog catalog))
        {
          return exactArcsByPose[(partIndex, rotationKey)] =
            Array.Empty<(CommonBoundarySpan, DxfResolvedBoundarySpan)>();
        }

        IPartPlacement original = originalPlacements[partIndex];
        var pose = new PartPlacement(original.Part)
        {
          Id = original.Id,
          IsDragging = original.IsDragging,
          IsMirrored = original.IsMirrored,
          Rotation = NormalizeAngle(rotation),
          Source = original.Source,
          X = 0D,
          Y = 0D,
        };
        var arcs = new List<(CommonBoundarySpan, DxfResolvedBoundarySpan)>();
        try
        {
          foreach (DxfSourceContour contour in catalog.Contours.Where(contour =>
            !contour.IsChild && contour.IsClosed))
          {
            foreach (DxfSourcePrimitive primitive in contour.Primitives.Where(primitive =>
              primitive.GeometryKind == CommonLineGeometryKind.CircularArc))
            {
              CommonBoundarySpan span = DxfCommonLineSpanResolver.CreateSpan(
                catalog,
                contour.Index,
                primitive.Index);
              arcs.Add((span, DxfCommonLineSpanResolver.Resolve(catalog, span, pose)));
            }
          }
        }
        catch (CommonLineValidationException)
        {
          return exactArcsByPose[(partIndex, rotationKey)] =
            Array.Empty<(CommonBoundarySpan, DxfResolvedBoundarySpan)>();
        }

        return exactArcsByPose[(partIndex, rotationKey)] = arcs;
      }

      void PopulateEdges(DirectPlacementOption option)
      {
        if (option == null || option.Edges.Count > 0)
        {
          return;
        }

        SvgPoint[] points = option.Poly?.Points;
        if (points == null || points.Length < 3)
        {
          return;
        }

        int count = points.Length;
        if (count > 1 && Distance(points[0].X, points[0].Y, points[count - 1].X, points[count - 1].Y) <= geometryTolerance)
        {
          count--;
        }

        bool[] curved = (option.Poly as NoFitPolygon)?.GetCurvedSegments();
        double winding = SignedArea(points) >= 0 ? 1.0 : -1.0;
        for (int edgeIndex = 0; edgeIndex < count; edgeIndex++)
        {
          cancel.ThrowIfCancellationRequested();
          if (curved != null && edgeIndex < curved.Length && curved[edgeIndex])
          {
            continue;
          }

          SvgPoint a = points[edgeIndex];
          SvgPoint b = points[(edgeIndex + 1) % count];
          double dx = b.X - a.X;
          double dy = b.Y - a.Y;
          double length = Math.Sqrt((dx * dx) + (dy * dy));
          if (length <= geometryTolerance)
          {
            continue;
          }

          double ux = dx / length;
          double uy = dy / length;
          option.Edges.Add(new DirectEdge(
            option.Part,
            option.Pose,
            edgeIndex,
            a.X + option.X,
            a.Y + option.Y,
            b.X + option.X,
            b.Y + option.Y,
            ux,
            uy,
            winding * uy,
            winding * -ux,
            length));
        }
      }

      bool AddOrientationOption(int partIndex, double requestedRotation, int limit = int.MaxValue)
      {
        DirectOrientationProfile profile = orientationProfileByPart[partIndex];
        if (profile?.Nfp == null || optionsByPart[partIndex].Count >= limit)
        {
          return false;
        }

        double rotation = NormalizeAngle(requestedRotation);
        if (optionsByPart[partIndex].Any(option => AngleDistance(option.Rotation, rotation) <= AngularEpsilonDegrees))
        {
          return false;
        }

        INfp rotated = rotation <= AngularEpsilonDegrees ? profile.Nfp : profile.Nfp.Rotate(rotation);
        IPartPlacement placement = originalPlacements[partIndex];
        double centreX = (placement.MinX + placement.MaxX) / 2.0;
        double centreY = (placement.MinY + placement.MaxY) / 2.0;
        double x = centreX - ((rotated.MinX + rotated.MaxX) / 2.0);
        double y = centreY - ((rotated.MinY + rotated.MaxY) / 2.0);
        var option = new DirectPlacementOption(
          partIndex,
          optionsByPart[partIndex].Count,
          rotated,
          rotation,
          x,
          y);
        optionsByPart[partIndex].Add(option);
        PopulateEdges(option);
        return true;
      }

      // Build every explicitly permitted discrete pose. Continuous profiles initially retain Sparrow's
      // exact pose; compatible edge directions below derive the additional arbitrary angles worth trying.
      for (int i = 0; i < n; i++)
      {
        cancel.ThrowIfCancellationRequested();
        IPartPlacement placement = originalPlacements[i];
        var current = new DirectPlacementOption(i, 0, placement.Part, originalRotation[i], originalX[i], originalY[i]);
        optionsByPart[i] = new List<DirectPlacementOption> { current };

        bool eligible = rulesBySource.TryGetValue(placement.Source, out var rule)
          && rule.Cc != CommonCuttingMode.None
          && rule.RequiresMergedLines
          && !string.IsNullOrWhiteSpace(rule.CommonCuttingDomain);
        if (!eligible)
        {
          continue;
        }

        if (orientationProfilesBySource != null
            && orientationProfilesBySource.TryGetValue(placement.Source, out DirectOrientationProfile profile)
            && profile?.Nfp != null)
        {
          orientationProfileByPart[i] = profile;
          currentPosePermitted[i] = profile.Continuous
            || profile.Angles.Any(angle => AngleDistance(angle, originalRotation[i]) <= AngularEpsilonDegrees);
          if (!profile.Continuous)
          {
            foreach (int angle in profile.Angles.Distinct().OrderBy(angle => NormalizeAngle(angle)))
            {
              cancel.ThrowIfCancellationRequested();
              _ = AddOrientationOption(i, angle);
            }
          }
        }
        else
        {
          // The compatibility-only overload owns no source rotation policy. Its historic contract is
          // translation-only, so the supplied pose is the sole legal one.
          currentPosePermitted[i] = true;
        }

        if (currentPosePermitted[i])
        {
          PopulateEdges(current);
        }
      }

      // A continuous policy cannot be enumerated. Derive deterministic poses from real compatible LINE
      // normals and exact source ARC spans. This retains arbitrary Sparrow angles and covers fully curved
      // parts without rounding either geometry kind to a coarse sample grid.
      for (int i = 0; i < n; i++)
      {
        cancel.ThrowIfCancellationRequested();
        DirectOrientationProfile profile = orientationProfileByPart[i];
        if (profile?.Continuous != true || optionsByPart[i].Count >= maxContinuousPoses)
        {
          continue;
        }

        var sourceFrame = new DirectPlacementOption(i, -1, profile.Nfp, 0, 0, 0);
        PopulateEdges(sourceFrame);
        var derived = new Dictionary<long, double>();
        void Propose(double angle, double potentialLength = 0D)
        {
          double normalized = NormalizeAngle(angle);
          long key = (long)Math.Round(normalized * 1_000_000.0);
          if (!derived.TryGetValue(key, out double existing)
            || potentialLength > existing)
          {
            derived[key] = potentialLength;
          }
        }

        Propose(originalRotation[i] + 180.0);
        IReadOnlyList<(CommonBoundarySpan Span, DxfResolvedBoundarySpan Resolved)> sourceArcs =
          ExactArcsAt(i, originalRotation[i]);
        for (int j = 0; j < n; j++)
        {
          cancel.ThrowIfCancellationRequested();
          if (i == j || !MayShare(i, j))
          {
            continue;
          }

          // Do not feed a pose derived for one continuous part into another. Its current Sparrow pose is
          // enough to derive an exact counterpart in both loop directions; discrete neighbours contribute
          // every explicitly permitted option.
          IEnumerable<DirectPlacementOption> targetOptions =
            orientationProfileByPart[j]?.Continuous == true
              ? optionsByPart[j].Take(1)
              : optionsByPart[j].Where(option => option.Pose != 0 || currentPosePermitted[j]);
          foreach (DirectEdge sourceEdge in sourceFrame.Edges)
          {
            cancel.ThrowIfCancellationRequested();
            double sourceNormal = Math.Atan2(sourceEdge.Ny, sourceEdge.Nx) * 180.0 / Math.PI;
            foreach (DirectPlacementOption targetOption in targetOptions)
            {
              cancel.ThrowIfCancellationRequested();
              foreach (DirectEdge targetEdge in targetOption.Edges)
              {
                cancel.ThrowIfCancellationRequested();
                double facingNormal = Math.Atan2(-targetEdge.Ny, -targetEdge.Nx) * 180.0 / Math.PI;
                Propose(facingNormal - sourceNormal, Math.Min(sourceEdge.Length, targetEdge.Length));
              }
            }
          }

          foreach (DirectPlacementOption targetOption in targetOptions)
          {
            cancel.ThrowIfCancellationRequested();
            IReadOnlyList<(CommonBoundarySpan Span, DxfResolvedBoundarySpan Resolved)> targetArcs =
              ExactArcsAt(j, targetOption.Rotation);
            foreach ((CommonBoundarySpan _, DxfResolvedBoundarySpan sourceArc) in sourceArcs)
            {
              cancel.ThrowIfCancellationRequested();
              foreach ((CommonBoundarySpan _, DxfResolvedBoundarySpan targetArc) in targetArcs)
              {
                cancel.ThrowIfCancellationRequested();
                if (Math.Abs(Math.Abs(sourceArc.Radius - targetArc.Radius) - commonLineGap)
                    > geometryTolerance
                  || IsFullCircle(sourceArc)
                  || IsFullCircle(targetArc))
                {
                  continue;
                }

                CommonLinePoint sourceRadial = RadialAt(sourceArc, 0.5D);
                CommonLinePoint targetRadial = RadialAt(targetArc, 0.5D);
                double sourcePolarity = Dot(sourceArc.OutwardNormalAtMidpoint, sourceRadial);
                double targetPolarity = Dot(targetArc.OutwardNormalAtMidpoint, targetRadial);
                if (Math.Abs(sourcePolarity + targetPolarity) > 1e-6D
                  || Math.Abs((targetArc.Radius - sourceArc.Radius)
                    - (commonLineGap * sourcePolarity)) > geometryTolerance)
                {
                  continue;
                }

                double potentialLength = Math.Min(sourceArc.Length, targetArc.Length);
                ProposeAligned(sourceArc, 0.5D, targetArc, 0.5D, potentialLength);
                ProposeAligned(sourceArc, 0D, targetArc, 1D, potentialLength);
                ProposeAligned(sourceArc, 1D, targetArc, 0D, potentialLength);
                // A mirrored placement can reverse the world-space contour orientation independently
                // of its physical radial face. Preserve same-end alignments too and let the exact
                // resolver decide which endpoint correspondence has opposite outward normals.
                ProposeAligned(sourceArc, 0D, targetArc, 0D, potentialLength);
                ProposeAligned(sourceArc, 1D, targetArc, 1D, potentialLength);
              }
            }
          }
        }

        foreach (int angle in profile.Angles.Distinct())
        {
          cancel.ThrowIfCancellationRequested();
          Propose(angle);
        }

        // Give the bounded continuous search independent material, seam-length and direction choices.
        // Ranking only by potential seam length can discard a short face before the material-first
        // planner ever evaluates it. The present Sparrow pose stays at index zero.
        var rankedPoses = derived.Select(pair =>
        {
          double angle = pair.Key / 1_000_000D;
          double radians = angle * Math.PI / 180D;
          double cosine = Math.Cos(radians);
          double sine = Math.Sin(radians);
          double minX = double.PositiveInfinity;
          double minY = double.PositiveInfinity;
          double maxX = double.NegativeInfinity;
          double maxY = double.NegativeInfinity;
          foreach (SvgPoint point in profile.Nfp.Points)
          {
            cancel.ThrowIfCancellationRequested();
            double x = (point.X * cosine) - (point.Y * sine);
            double y = (point.X * sine) + (point.Y * cosine);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
          }

          double width = maxX - minX;
          double height = maxY - minY;
          return (Angle: angle, Length: pair.Value, Area: width * height,
            Fits: width <= sheetW - (2D * margin) + geometryTolerance
              && height <= sheetH - (2D * margin) + geometryTolerance,
            Sector: Math.Min(7, (int)(angle / 45D)));
        }).ToArray();
        var materialPoses = rankedPoses
          .OrderByDescending(pose => pose.Fits)
          .ThenBy(pose => pose.Area)
          .ThenByDescending(pose => pose.Length)
          .ThenBy(pose => AngleDistance(pose.Angle, originalRotation[i]))
          .ThenBy(pose => pose.Angle)
          .ToArray();
        var seamPoses = rankedPoses
          .OrderByDescending(pose => pose.Length)
          .ThenBy(pose => AngleDistance(pose.Angle, originalRotation[i]))
          .ThenBy(pose => pose.Angle)
          .ToArray();
        var directionPoses = materialPoses.GroupBy(pose => pose.Sector)
          .Select(group => group.First()).OrderBy(pose => pose.Sector).ToArray();
        for (int choice = 0; choice < rankedPoses.Length
          && optionsByPart[i].Count < maxContinuousPoses; choice++)
        {
          _ = AddOrientationOption(i, materialPoses[choice].Angle, maxContinuousPoses);
          _ = AddOrientationOption(i, seamPoses[choice].Angle, maxContinuousPoses);
          if (choice < directionPoses.Length)
          {
            _ = AddOrientationOption(i, directionPoses[choice].Angle, maxContinuousPoses);
          }
        }

        bool IsFullCircle(DxfResolvedBoundarySpan arc)
          => Math.Abs(arc.SweepRadians) >= (2D * Math.PI) - 1e-9D;

        CommonLinePoint RadialAt(DxfResolvedBoundarySpan arc, double parameter)
        {
          CommonLinePoint point = arc.PointAt(parameter);
          double dx = point.X - arc.Center.X;
          double dy = point.Y - arc.Center.Y;
          double length = Math.Sqrt((dx * dx) + (dy * dy));
          return new CommonLinePoint(dx / length, dy / length);
        }

        double Dot(CommonLinePoint first, CommonLinePoint second)
          => (first.X * second.X) + (first.Y * second.Y);

        void ProposeAligned(
          DxfResolvedBoundarySpan sourceArc,
          double sourceParameter,
          DxfResolvedBoundarySpan targetArc,
          double targetParameter,
          double potentialLength)
        {
          CommonLinePoint sourcePoint = sourceArc.PointAt(sourceParameter);
          CommonLinePoint targetPoint = targetArc.PointAt(targetParameter);
          double sourceAngle = Math.Atan2(
            sourcePoint.Y - sourceArc.Center.Y,
            sourcePoint.X - sourceArc.Center.X);
          double targetAngle = Math.Atan2(
            targetPoint.Y - targetArc.Center.Y,
            targetPoint.X - targetArc.Center.X);
          double delta = targetAngle - sourceAngle;
          delta = Math.Atan2(Math.Sin(delta), Math.Cos(delta));
          Propose(originalRotation[i] + (delta * 180D / Math.PI), potentialLength);
        }
      }

      int CompareDirectCandidates(DirectCandidate x, DirectCandidate y)
      {
        // Tolerance is for geometric acceptance, not ranking: treating near values as equal here can
        // produce a non-transitive comparison and make Sort choose candidates unpredictably.
        int order = y.Length.CompareTo(x.Length);
        if (order != 0)
        {
          return order;
        }

        order = x.PoseChanges.CompareTo(y.PoseChanges);
        if (order != 0)
        {
          return order;
        }

        order = x.Travel.CompareTo(y.Travel);
        if (order != 0)
        {
          return order;
        }

        order = x.A.CompareTo(y.A);
        if (order != 0)
        {
          return order;
        }

        order = x.B.CompareTo(y.B);
        if (order != 0)
        {
          return order;
        }

        order = x.PoseA.CompareTo(y.PoseA);
        if (order != 0)
        {
          return order;
        }

        order = x.PoseB.CompareTo(y.PoseB);
        if (order != 0)
        {
          return order;
        }

        order = x.EdgeA.CompareTo(y.EdgeA);
        if (order != 0)
        {
          return order;
        }

        order = x.EdgeB.CompareTo(y.EdgeB);
        if (order != 0)
        {
          return order;
        }

        order = (x.SpanA?.ContourIndex ?? -1).CompareTo(y.SpanA?.ContourIndex ?? -1);
        if (order != 0)
        {
          return order;
        }

        order = (x.SpanA?.PrimitiveIndex ?? -1).CompareTo(y.SpanA?.PrimitiveIndex ?? -1);
        if (order != 0)
        {
          return order;
        }

        order = (x.SpanB?.ContourIndex ?? -1).CompareTo(y.SpanB?.ContourIndex ?? -1);
        if (order != 0)
        {
          return order;
        }

        order = (x.SpanB?.PrimitiveIndex ?? -1).CompareTo(y.SpanB?.PrimitiveIndex ?? -1);
        if (order != 0)
        {
          return order;
        }

        order = x.EdgeAStart.CompareTo(y.EdgeAStart);
        if (order != 0)
        {
          return order;
        }

        order = x.EdgeBStart.CompareTo(y.EdgeBStart);
        if (order != 0)
        {
          return order;
        }

        order = x.Dx.CompareTo(y.Dx);
        return order != 0 ? order : x.Dy.CompareTo(y.Dy);
      }

      int CompareCandidateFootprints(
        (double Area, double LongAxis, double ShortAxis) left,
        (double Area, double LongAxis, double ShortAxis) right)
      {
        int order = left.Area.CompareTo(right.Area);
        if (order != 0)
        {
          return order;
        }

        order = left.LongAxis.CompareTo(right.LongAxis);
        if (order != 0)
        {
          return order;
        }

        return left.ShortAxis.CompareTo(right.ShortAxis);
      }

      var candidateFootprints = new Dictionary<DirectCandidate, (double Area, double LongAxis, double ShortAxis)>();
      (double Area, double LongAxis, double ShortAxis) ProjectedCandidateFootprint(DirectCandidate candidate)
      {
        if (candidateFootprints.TryGetValue(candidate, out var cached))
        {
          return cached;
        }

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        for (int partIndex = 0; partIndex < n; partIndex++)
        {
          cancel.ThrowIfCancellationRequested();
          if (partIndex == candidate.A || partIndex == candidate.B)
          {
            DirectPlacementOption option = optionsByPart[partIndex][
              partIndex == candidate.A ? candidate.PoseA : candidate.PoseB];
            double offsetX = partIndex == candidate.B ? candidate.Dx : 0D;
            double offsetY = partIndex == candidate.B ? candidate.Dy : 0D;
            minX = Math.Min(minX, option.Poly.MinX + option.X + offsetX);
            minY = Math.Min(minY, option.Poly.MinY + option.Y + offsetY);
            maxX = Math.Max(maxX, option.Poly.MaxX + option.X + offsetX);
            maxY = Math.Max(maxY, option.Poly.MaxY + option.Y + offsetY);
          }
          else
          {
            minX = Math.Min(minX, originalPlacements[partIndex].Part.MinX + originalX[partIndex]);
            minY = Math.Min(minY, originalPlacements[partIndex].Part.MinY + originalY[partIndex]);
            maxX = Math.Max(maxX, originalPlacements[partIndex].Part.MaxX + originalX[partIndex]);
            maxY = Math.Max(maxY, originalPlacements[partIndex].Part.MaxY + originalY[partIndex]);
          }
        }

        double width = Math.Max(0D, maxX - minX);
        double height = Math.Max(0D, maxY - minY);
        var footprint = (
          width * height,
          sheetW >= sheetH ? width : height,
          sheetW >= sheetH ? height : width);
        candidateFootprints[candidate] = footprint;
        return footprint;
      }

      int matchesPerPair = effort switch
      {
        NestEffort.Best => 16,
        NestEffort.Normal => 8,
        _ => 4,
      };
      int proposalLimit = effort switch
      {
        NestEffort.Best => 250_000,
        NestEffort.Normal => 50_000,
        _ => 10_000,
      };
      int continuousArcPosePairLimit = effort switch
      {
        NestEffort.Best => 1024,
        NestEffort.Normal => 256,
        _ => 64,
      };

      List<DirectCandidate> RetainDiversePairCandidates(List<DirectCandidate> source)
      {
        if (source.Count == 0)
        {
          return source;
        }

        int retentionLimit = Math.Min(source.Count, matchesPerPair * 3);
        var materialRanked = source
          .Select(candidate => (
            Candidate: candidate,
            Footprint: ProjectedCandidateFootprint(candidate)))
          .ToList();
        materialRanked.Sort((left, right) =>
        {
          int order = CompareCandidateFootprints(left.Footprint, right.Footprint);
          return order != 0
            ? order
            : CompareDirectCandidates(left.Candidate, right.Candidate);
        });

        var retained = new List<DirectCandidate>(retentionLimit);
        void Add(DirectCandidate candidate)
        {
          if (retained.Count < retentionLimit && !retained.Contains(candidate))
          {
            retained.Add(candidate);
          }
        }

        // A union, not a single ordering: Fast keeps its best four material moves and its best four cut
        // savings. This prevents a fifth, shorter face that compacts the sheet from disappearing before
        // the material-first beam can compare it, while retaining the historic long-seam alternatives.
        foreach (var ranked in materialRanked.Take(matchesPerPair))
        {
          cancel.ThrowIfCancellationRequested();
          Add(ranked.Candidate);
        }

        foreach (DirectCandidate candidate in source
          .OrderBy(item => item, Comparer<DirectCandidate>.Create(CompareDirectCandidates))
          .Take(matchesPerPair))
        {
          Add(candidate);
        }

        int TranslationSector(DirectCandidate candidate)
        {
          if (candidate.Travel <= geometryTolerance)
          {
            return 0;
          }

          double angle = Math.Atan2(candidate.Dy, candidate.Dx) + Math.PI;
          return 1 + Math.Min(7, (int)Math.Floor(angle * 8D / (2D * Math.PI)));
        }

        // Reserve representatives from distinct pose/translation strategies before filling remaining
        // slots. A dense grid still contributes at most 3N LINE and 3N ARC proposals per physical pair.
        foreach (var representative in materialRanked
          .GroupBy(ranked => (
            ranked.Candidate.PoseA,
            ranked.Candidate.PoseB,
            Sector: TranslationSector(ranked.Candidate)))
          .Select(group => group.First()))
        {
          Add(representative.Candidate);
        }

        foreach (var ranked in materialRanked)
        {
          cancel.ThrowIfCancellationRequested();
          Add(ranked.Candidate);
        }

        return retained;
      }

      bool IsLegalOption(int partIndex, DirectPlacementOption option)
        => option != null && (option.Pose != 0 || currentPosePermitted[partIndex]);

      bool HasCurvedBoundary(DirectPlacementOption option)
        => (option?.Poly as NoFitPolygon)?.GetCurvedSegments()?.Any(curved => curved) == true;

      var exactCircularOutsideSource = new bool?[n];
      bool HasExactCircularOutsideSource(int partIndex)
      {
        if (exactCircularOutsideSource[partIndex].HasValue)
        {
          return exactCircularOutsideSource[partIndex].Value;
        }

        if (TryGetExactCatalog(partIndex, out DxfContourCatalog catalog))
        {
          bool supported = catalog.Contours.Any(contour =>
            !contour.IsChild
            && contour.IsClosed
            && contour.Primitives.Any(primitive =>
              primitive.GeometryKind == CommonLineGeometryKind.CircularArc));
          exactCircularOutsideSource[partIndex] = supported;
          return supported;
        }

        // Curved NFP chords are never enough evidence to grant reduced clearance. A missing, stale,
        // or unsupported source catalog remains a named fail-closed fallback for the operator.
        exactCircularOutsideSource[partIndex] = false;
        return false;
      }

      PartPlacement CandidatePlacement(int partIndex, DirectPlacementOption option)
      {
        IPartPlacement original = originalPlacements[partIndex];
        return new PartPlacement(option.Poly)
        {
          Id = original.Id,
          IsDragging = original.IsDragging,
          IsMirrored = original.IsMirrored,
          Rotation = option.Rotation,
          Source = original.Source,
          X = option.X,
          Y = option.Y,
        };
      }

      var centres = originalPlacements
        .Select(placement => (
          X: (placement.MinX + placement.MaxX) / 2.0,
          Y: (placement.MinY + placement.MaxY) / 2.0))
        .ToArray();
      var poseRadii = optionsByPart.Select((options, partIndex) => options
        .SelectMany(option => (option.Poly?.Points ?? Array.Empty<SvgPoint>())
          .Select(point => Distance(
            point.X + option.X,
            point.Y + option.Y,
            centres[partIndex].X,
            centres[partIndex].Y)))
        .DefaultIfEmpty(0)
        .Max()).ToArray();

      var candidates = new List<DirectCandidate>();
      var unsupportedCurvedPairs = new HashSet<(int A, int B)>();
      double minimumCommonSpan = Math.Max(2.0 * commonLineGap, 8.0 * geometryTolerance);

      // Where two facing edges overlap once B has slid tangentShift along A, as fractions of each edge.
      // The candidates ask it of positions they propose; the cycle pickup asks it again of where the
      // parts actually ended up, because the tree moves them.
      bool TryOverlap(
        DirectEdge ea,
        DirectEdge eb,
        double tangentShift,
        out double edgeAStart,
        out double edgeAEnd,
        out double edgeBStart,
        out double edgeBEnd,
        out double overlapLength)
      {
        edgeAStart = edgeAEnd = edgeBStart = edgeBEnd = 0;
        double b0 = ((eb.Ax - ea.Ax) * ea.Ux) + ((eb.Ay - ea.Ay) * ea.Uy);
        double b1 = ((eb.Bx - ea.Ax) * ea.Ux) + ((eb.By - ea.Ay) * ea.Uy);
        double overlapStart = Math.Max(0, Math.Min(b0, b1) + tangentShift);
        double overlapEnd = Math.Min(ea.Length, Math.Max(b0, b1) + tangentShift);
        overlapLength = overlapEnd - overlapStart;
        double bDenominator = b1 - b0;
        if (overlapLength + geometryTolerance < minimumCommonSpan || Math.Abs(bDenominator) <= geometryTolerance)
        {
          return false;
        }

        double bParameter0 = (overlapStart - tangentShift - b0) / bDenominator;
        double bParameter1 = (overlapEnd - tangentShift - b0) / bDenominator;
        edgeAStart = Math.Clamp(overlapStart / ea.Length, 0, 1);
        edgeAEnd = Math.Clamp(overlapEnd / ea.Length, 0, 1);
        edgeBStart = Math.Clamp(Math.Min(bParameter0, bParameter1), 0, 1);
        edgeBEnd = Math.Clamp(Math.Max(bParameter0, bParameter1), 0, 1);
        return true;
      }

      reportStage("candidates");
      for (int a = 0; a < n; a++)
      {
        cancel.ThrowIfCancellationRequested();
        for (int b = a + 1; b < n; b++)
        {
          cancel.ThrowIfCancellationRequested();
          if (!MayShare(a, b))
          {
            continue;
          }

          double spacingA = rulesBySource.TryGetValue(originalPlacements[a].Source, out var ra) ? Math.Max(0, ra.Spacing) : 0;
          double spacingB = rulesBySource.TryGetValue(originalPlacements[b].Source, out var rb) ? Math.Max(0, rb.Spacing) : 0;
          double travelLimit = (3.0 * Math.Max(commonLineGap, (spacingA + spacingB) / 2.0)) + (8.0 * geometryTolerance);
          if (Distance(centres[a].X, centres[a].Y, centres[b].X, centres[b].Y)
              > poseRadii[a] + poseRadii[b] + travelLimit + geometryTolerance)
          {
            continue;
          }

          var pairCandidates = new List<DirectCandidate>();
          var arcPairCandidates = new List<DirectCandidate>();
          var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
          bool curvedA = optionsByPart[a].Any(HasCurvedBoundary);
          bool curvedB = optionsByPart[b].Any(HasCurvedBoundary);
          if ((curvedA && !HasExactCircularOutsideSource(a))
            || (curvedB && !HasExactCircularOutsideSource(b)))
          {
            _ = unsupportedCurvedPairs.Add((a, b));
          }

          foreach (DirectPlacementOption optionA in optionsByPart[a])
          {
            cancel.ThrowIfCancellationRequested();
            foreach (DirectPlacementOption optionB in optionsByPart[b])
            {
              cancel.ThrowIfCancellationRequested();
              foreach (DirectEdge ea in optionA.Edges)
              {
                cancel.ThrowIfCancellationRequested();
                foreach (DirectEdge eb in optionB.Edges)
                {
                  cancel.ThrowIfCancellationRequested();
                  double angularTolerance = Math.Min(
                    1e-3,
                    geometryTolerance / Math.Max(Math.Min(ea.Length, eb.Length), geometryTolerance));
                  double oppositeNormalTolerance = Math.Max(
                    1e-10,
                    2D * angularTolerance * angularTolerance);
                  if (Math.Abs((ea.Ux * eb.Uy) - (ea.Uy * eb.Ux)) > angularTolerance
                      || (ea.Nx * eb.Nx) + (ea.Ny * eb.Ny) > -1.0 + oppositeNormalTolerance)
                  {
                    continue;
                  }

                  double currentSeparation = ((((eb.Ax + eb.Bx) / 2.0) - ((ea.Ax + ea.Bx) / 2.0)) * ea.Nx)
                    + ((((eb.Ay + eb.By) / 2.0) - ((ea.Ay + ea.By) / 2.0)) * ea.Ny);
                  if (currentSeparation <= 0)
                  {
                    continue;
                  }

                  double b0 = ((eb.Ax - ea.Ax) * ea.Ux) + ((eb.Ay - ea.Ay) * ea.Uy);
                  double b1 = ((eb.Bx - ea.Ax) * ea.Ux) + ((eb.By - ea.Ay) * ea.Uy);
                  double bLow = Math.Min(b0, b1);
                  double bHigh = Math.Max(b0, b1);
                  double normalShift = commonLineGap - currentSeparation;
                  var tangentialShifts = new[]
                  {
                    0.0,
                    -bLow,
                    ea.Length - bHigh,
                    (ea.Length / 2.0) - ((bLow + bHigh) / 2.0),
                  };

                  foreach (double tangentShift in tangentialShifts
                    .OrderBy(value => Math.Abs(value))
                    .ThenBy(value => value)
                    .Distinct())
                  {
                    double shiftX = (ea.Nx * normalShift) + (ea.Ux * tangentShift);
                    double shiftY = (ea.Ny * normalShift) + (ea.Uy * tangentShift);
                    double travel = Math.Sqrt((shiftX * shiftX) + (shiftY * shiftY));
                    if (travel > travelLimit)
                    {
                      continue;
                    }

                    if (!TryOverlap(ea, eb, tangentShift, out double edgeAStart, out double edgeAEnd,
                      out double edgeBStart, out double edgeBEnd, out double overlapLength))
                    {
                      continue;
                    }

                    string key = FormattableString.Invariant(
                      $"{a}:{ea.Pose}:{ea.Index}:{edgeAStart:F9}:{edgeAEnd:F9}|{b}:{eb.Pose}:{eb.Index}:{edgeBStart:F9}:{edgeBEnd:F9}|{shiftX / geometryTolerance:F0}:{shiftY / geometryTolerance:F0}");
                    if (!candidateKeys.Add(key))
                    {
                      continue;
                    }

                    pairCandidates.Add(new DirectCandidate(
                      a,
                      b,
                      ea.Pose,
                      eb.Pose,
                      ea.Index,
                      eb.Index,
                      edgeAStart,
                      edgeAEnd,
                      edgeBStart,
                      edgeBEnd,
                      shiftX,
                      shiftY,
                      overlapLength,
                      travel));
                  }
                }
              }
            }
          }

          if (curvedA && curvedB)
          {
            IEnumerable<(DirectPlacementOption A, DirectPlacementOption B)> arcPosePairs =
              optionsByPart[a]
                .Where(option => IsLegalOption(a, option))
                .SelectMany(
                  optionA => optionsByPart[b]
                    .Where(optionB => IsLegalOption(b, optionB))
                    .Select(optionB => (A: optionA, B: optionB)))
                .OrderBy(pair => (pair.A.Pose == 0 ? 0 : 1) + (pair.B.Pose == 0 ? 0 : 1))
                .ThenBy(pair => AngleDistance(pair.A.Rotation, originalRotation[a]))
                .ThenBy(pair => AngleDistance(pair.B.Rotation, originalRotation[b]))
                .ThenBy(pair => pair.A.Pose)
                .ThenBy(pair => pair.B.Pose);
            if (orientationProfileByPart[a]?.Continuous == true
              || orientationProfileByPart[b]?.Continuous == true)
            {
              // Continuous rotation is represented by a deterministic finite pose budget. Spend more
              // work in Normal/Best, while discrete policies continue to enumerate every allowed pose.
              arcPosePairs = arcPosePairs.Take(continuousArcPosePairLimit);
            }

            foreach ((DirectPlacementOption optionA, DirectPlacementOption optionB) in arcPosePairs)
            {
              cancel.ThrowIfCancellationRequested();
              IReadOnlyList<DxfCommonLineArcCandidate> exactArcs;
              try
              {
                exactArcs = DxfCommonLineArcCandidateFinder.Find(
                  CandidatePlacement(a, optionA),
                  CandidatePlacement(b, optionB),
                  commonLineGap,
                  geometryTolerance,
                  exactCatalogByPart[a],
                  exactCatalogByPart[b]);
              }
              catch (CommonLineValidationException)
              {
                // A malformed/stale source catalog cannot authorize reduced clearance. The candidate
                // simply remains at its baseline normal-gap placement and no intent is persisted.
                continue;
              }

              foreach (DxfCommonLineArcCandidate exactArc in exactArcs)
              {
                cancel.ThrowIfCancellationRequested();
                if (exactArc?.SpanA == null
                  || exactArc.SpanB == null
                  || exactArc.SharedLength + geometryTolerance < minimumCommonSpan
                  || exactArc.Travel > travelLimit)
                {
                  continue;
                }

                double aStart = Math.Min(exactArc.SpanA.StartParameter, exactArc.SpanA.EndParameter);
                double aEnd = Math.Max(exactArc.SpanA.StartParameter, exactArc.SpanA.EndParameter);
                double bStart = Math.Min(exactArc.SpanB.StartParameter, exactArc.SpanB.EndParameter);
                double bEnd = Math.Max(exactArc.SpanB.StartParameter, exactArc.SpanB.EndParameter);
                string key = FormattableString.Invariant(
                  $"ARC|{a}:{optionA.Pose}:{exactArc.SpanA.ContourIndex}:{exactArc.SpanA.PrimitiveIndex}:{aStart:F9}:{aEnd:F9}|{b}:{optionB.Pose}:{exactArc.SpanB.ContourIndex}:{exactArc.SpanB.PrimitiveIndex}:{bStart:F9}:{bEnd:F9}|{exactArc.TranslationX / geometryTolerance:F0}:{exactArc.TranslationY / geometryTolerance:F0}");
                if (!candidateKeys.Add(key))
                {
                  continue;
                }

                arcPairCandidates.Add(new DirectCandidate(
                  a,
                  b,
                  optionA.Pose,
                  optionB.Pose,
                  -1,
                  -1,
                  aStart,
                  aEnd,
                  bStart,
                  bEnd,
                  exactArc.TranslationX,
                  exactArc.TranslationY,
                  exactArc.SharedLength,
                  exactArc.Travel,
                  exactArc.SpanA,
                  exactArc.SpanB));
              }
            }
          }

          candidates.AddRange(RetainDiversePairCandidates(pairCandidates));
          candidates.AddRange(RetainDiversePairCandidates(arcPairCandidates));
        }
      }

      var globallyRankedCandidates = candidates
        .Select(candidate => (
          Candidate: candidate,
          Footprint: ProjectedCandidateFootprint(candidate)))
        .ToList();
      globallyRankedCandidates.Sort((left, right) =>
      {
        int order = CompareCandidateFootprints(left.Footprint, right.Footprint);
        return order != 0
          ? order
          : CompareDirectCandidates(left.Candidate, right.Candidate);
      });
      candidates.Clear();
      candidates.AddRange(globallyRankedCandidates
        .Take(proposalLimit)
        .Select(ranked => ranked.Candidate));

      HashSet<(int A, int B)> AcceptedPairIndexes(
        IReadOnlyList<CommonEdgeIntent> acceptedIntents)
      {
        var acceptedPairs = new HashSet<(int A, int B)>();
        foreach (CommonEdgeIntent intent in acceptedIntents ?? Array.Empty<CommonEdgeIntent>())
        {
          cancel.ThrowIfCancellationRequested();
          int a = all.FindIndex(placement => ReferenceEquals(placement, intent.PartA));
          int b = all.FindIndex(placement => ReferenceEquals(placement, intent.PartB));
          if (a >= 0 && b >= 0)
          {
            acceptedPairs.Add(a <= b ? (a, b) : (b, a));
          }
        }

        return acceptedPairs;
      }

      HashSet<(int A, int B)> PublishUnsupportedCurves(
        IReadOnlyList<CommonEdgeIntent> acceptedIntents)
      {
        HashSet<(int A, int B)> acceptedPairs = AcceptedPairIndexes(acceptedIntents);
        var failedPairs = new HashSet<(int A, int B)>(unsupportedCurvedPairs);
        failedPairs.ExceptWith(acceptedPairs);
        foreach ((int A, int B) failed in failedPairs)
        {
          cancel.ThrowIfCancellationRequested();
          AddCommonLineFallback(
            result,
            new[] { originalPlacements[failed.A], originalPlacements[failed.B] },
            "curved common-line outside geometry has no resolvable exact source span");
        }

        return failedPairs;
      }

      void PublishUnsupportedCurvesOnly(IReadOnlyList<CommonEdgeIntent> acceptedIntents)
        => result.GivenUp = PublishUnsupportedCurves(acceptedIntents).Count;

      if (candidates.Count == 0)
      {
        PublishUnsupportedCurvesOnly(Array.Empty<CommonEdgeIntent>());
        return result;
      }

      bool TryApplyPlan(
        IReadOnlyList<DirectCandidate> plan,
        bool validateTopology,
        out List<CommonEdgeIntent> intents,
        out int[] groups,
        out DirectPlanFailure failure,
        out string failureReason)
      {
        reportStage("apply");
        RestoreOriginalPlacements();
        intents = new List<CommonEdgeIntent>();
        groups = new int[n];
        failure = DirectPlanFailure.None;
        failureReason = null;

        // Local functions cannot close over an out parameter (CS1628); neither is ever reassigned once
        // set above, so an ordinary local aliasing the same array/list is a legal, always-current stand-in.
        List<CommonEdgeIntent> intentsRef = intents;
        int[] groupsRef = groups;
        if (plan.Count == 0)
        {
          return true;
        }

        var poses = Enumerable.Repeat(-1, n).ToArray();
        foreach (DirectCandidate edge in plan)
        {
          cancel.ThrowIfCancellationRequested();
          if ((poses[edge.A] >= 0 && poses[edge.A] != edge.PoseA)
              || (poses[edge.B] >= 0 && poses[edge.B] != edge.PoseB))
          {
            failure = DirectPlanFailure.EdgeGeometry;
            RestoreOriginalPlacements();
            intents.Clear();
            Array.Clear(groups, 0, groups.Length);
            return false;
          }

          poses[edge.A] = edge.PoseA;
          poses[edge.B] = edge.PoseB;
        }

        for (int i = 0; i < n; i++)
        {
          cancel.ThrowIfCancellationRequested();
          if (poses[i] <= 0)
          {
            continue;
          }

          DirectPlacementOption option = optionsByPart[i][poses[i]];
          IPartPlacement original = originalPlacements[i];
          all[i] = new PartPlacement(option.Poly)
          {
            Id = original.Id,
            IsDragging = original.IsDragging,
            IsMirrored = original.IsMirrored,
            Rotation = option.Rotation,
            Source = original.Source,
            X = option.X,
            Y = option.Y,
          };
        }

        var adjacency = new Dictionary<int, List<(int Other, double Dx, double Dy)>>();
        void Link(int a, int b, double dx, double dy)
        {
          (adjacency.TryGetValue(a, out var fromA) ? fromA : adjacency[a] = new List<(int, double, double)>()).Add((b, dx, dy));
          (adjacency.TryGetValue(b, out var fromB) ? fromB : adjacency[b] = new List<(int, double, double)>()).Add((a, -dx, -dy));
        }

        foreach (DirectCandidate edge in plan)
        {
          cancel.ThrowIfCancellationRequested();
          Link(edge.A, edge.B, edge.Dx, edge.Dy);
        }

        var visited = new bool[n];
        int nextGroup = 1;
        foreach (int start in adjacency.Keys.OrderBy(index => index))
        {
          cancel.ThrowIfCancellationRequested();
          if (visited[start])
          {
            continue;
          }

          var offsets = new Dictionary<int, (double X, double Y)> { [start] = (0, 0) };
          var componentMembers = new List<int>();
          var queue = new Queue<int>();
          queue.Enqueue(start);
          visited[start] = true;
          while (queue.Count > 0)
          {
            int current = queue.Dequeue();
            componentMembers.Add(current);
            DirectPlacementOption option = optionsByPart[current][poses[current]];
            all[current].X = option.X + offsets[current].X;
            all[current].Y = option.Y + offsets[current].Y;
            groups[current] = nextGroup;
            foreach (var link in adjacency[current])
            {
              cancel.ThrowIfCancellationRequested();
              if (visited[link.Other])
              {
                continue;
              }

              offsets[link.Other] = (offsets[current].X + link.Dx, offsets[current].Y + link.Dy);
              visited[link.Other] = true;
              queue.Enqueue(link.Other);
            }
          }

          // A pose is built around the part's current centre, while the relative seam offsets are
          // anchored at the component's lowest index. Rotating an asymmetric part can therefore put a
          // perfectly valid rigid cluster just outside a sheet edge. Translate the whole component by
          // the smallest feasible amount before rejecting it; this preserves every selected seam and
          // lets the later layout check decide collisions with other components.
          double componentMinX = componentMembers.Min(index => all[index].MinX);
          double componentMinY = componentMembers.Min(index => all[index].MinY);
          double componentMaxX = componentMembers.Max(index => all[index].MaxX);
          double componentMaxY = componentMembers.Max(index => all[index].MaxY);
          double minimumDx = margin - componentMinX;
          double maximumDx = (sheetW - margin) - componentMaxX;
          double minimumDy = margin - componentMinY;
          double maximumDy = (sheetH - margin) - componentMaxY;
          if (minimumDx > maximumDx + geometryTolerance
            || minimumDy > maximumDy + geometryTolerance)
          {
            failure = DirectPlanFailure.Layout;
            RestoreOriginalPlacements();
            intents.Clear();
            Array.Clear(groups, 0, groups.Length);
            return false;
          }

          double componentDx = minimumDx > 0D
            ? minimumDx
            : maximumDx < 0D ? maximumDx : 0D;
          double componentDy = minimumDy > 0D
            ? minimumDy
            : maximumDy < 0D ? maximumDy : 0D;
          if (componentDx != 0D || componentDy != 0D)
          {
            foreach (int member in componentMembers)
            {
              cancel.ThrowIfCancellationRequested();
              all[member].X += componentDx;
              all[member].Y += componentDy;
            }
          }

          nextGroup++;
        }

        var usedSpans = new Dictionary<DirectBoundaryKey, List<(double Start, double End)>>();
        bool SpanAvailable(DirectBoundaryKey key, double start, double end)
          => !usedSpans.TryGetValue(key, out var occupied)
            || occupied.All(span => Math.Max(span.Start, start) >= Math.Min(span.End, end) - 1e-9);
        void OccupySpan(DirectBoundaryKey key, double start, double end)
        {
          if (!usedSpans.TryGetValue(key, out var occupied))
          {
            usedSpans[key] = occupied = new List<(double, double)>();
          }

          occupied.Add((start, end));
        }

        foreach (DirectCandidate edge in plan)
        {
          cancel.ThrowIfCancellationRequested();
          var intent = new CommonEdgeIntent(
            all[edge.A],
            edge.EdgeA,
            all[edge.B],
            edge.EdgeB,
            edge.EdgeAStart,
            edge.EdgeAEnd,
            edge.EdgeBStart,
            edge.EdgeBEnd,
            edge.SpanA,
            edge.SpanB);
          DirectBoundaryKey keyA = DirectBoundaryKey.Create(edge.A, edge.EdgeA, edge.SpanA);
          DirectBoundaryKey keyB = DirectBoundaryKey.Create(edge.B, edge.EdgeB, edge.SpanB);
          if (!SpanAvailable(keyA, edge.EdgeAStart, edge.EdgeAEnd)
              || !SpanAvailable(keyB, edge.EdgeBStart, edge.EdgeBEnd)
              || !TryCreateCommonEdgeRecord(intent, commonLineGap, geometryTolerance, out _, sourceCatalogs))
          {
            failure = DirectPlanFailure.EdgeGeometry;
            failureReason = CommonLineFallbackNotice.TopologyReason;
            RestoreOriginalPlacements();
            intents.Clear();
            Array.Clear(groups, 0, groups.Length);
            return false;
          }

          OccupySpan(keyA, edge.EdgeAStart, edge.EdgeAEnd);
          OccupySpan(keyB, edge.EdgeBStart, edge.EdgeBEnd);
          intents.Add(intent);
        }

        // A tree can pull a cycle's last two faces to CommonLineGap as a side effect (the fourth edge of
        // a 2x2 grid). Record every such deliberately proposed full edge before clearance validation, so
        // no tight contact is ever present without the CommonEdge that authorises it.
        // The candidate's overlap was measured where the parts stood before the tree moved them, and the
        // tree slides each part by its own amount. Along a row pulled together under one long edge that
        // drift grows part by part, and a seam placed where the part used to be fails the gap check at
        // its ends, so it is measured again from where the two parts are now.
        foreach (DirectCandidate edge in candidates)
        {
          cancel.ThrowIfCancellationRequested();
          if (groups[edge.A] <= 0
              || groups[edge.A] != groups[edge.B]
              || poses[edge.A] != edge.PoseA
              || poses[edge.B] != edge.PoseB)
          {
            continue;
          }

          double edgeAStart = edge.EdgeAStart;
          double edgeAEnd = edge.EdgeAEnd;
          double edgeBStart = edge.EdgeBStart;
          double edgeBEnd = edge.EdgeBEnd;
          if (edge.SpanA == null)
          {
            DirectPlacementOption optionA = optionsByPart[edge.A][edge.PoseA];
            DirectPlacementOption optionB = optionsByPart[edge.B][edge.PoseB];
            DirectEdge ea = optionA.Edges.First(e => e.Index == edge.EdgeA);
            DirectEdge eb = optionB.Edges.First(e => e.Index == edge.EdgeB);
            double driftX = (all[edge.B].X - optionB.X) - (all[edge.A].X - optionA.X);
            double driftY = (all[edge.B].Y - optionB.Y) - (all[edge.A].Y - optionA.Y);
            if (!TryOverlap(ea, eb, (driftX * ea.Ux) + (driftY * ea.Uy),
              out edgeAStart, out edgeAEnd, out edgeBStart, out edgeBEnd, out _))
            {
              continue;
            }
          }

          DirectBoundaryKey keyA = DirectBoundaryKey.Create(edge.A, edge.EdgeA, edge.SpanA);
          DirectBoundaryKey keyB = DirectBoundaryKey.Create(edge.B, edge.EdgeB, edge.SpanB);
          if (!SpanAvailable(keyA, edgeAStart, edgeAEnd) || !SpanAvailable(keyB, edgeBStart, edgeBEnd))
          {
            continue;
          }

          var intent = new CommonEdgeIntent(
            all[edge.A],
            edge.EdgeA,
            all[edge.B],
            edge.EdgeB,
            edgeAStart,
            edgeAEnd,
            edgeBStart,
            edgeBEnd,
            edge.SpanA,
            edge.SpanB);
          if (TryCreateCommonEdgeRecord(intent, commonLineGap, geometryTolerance, out _, sourceCatalogs))
          {
            OccupySpan(keyA, edgeAStart, edgeAEnd);
            OccupySpan(keyB, edgeBStart, edgeBEnd);
            intents.Add(intent);
          }
        }

        // A normal-clearance conflict here is Sparrow's OWN baseline placement of a part with no seam in
        // this plan, caught out because the welded component beside it no longer occupies the footprint
        // that placement assumed. Rejecting the whole candidate over that is exactly what left every
        // mixed job with zero CommonEdges: walk the untouched neighbour clear first, the same bounded,
        // fully-reverified way DeferUnfit already does for the final sheet, and only give up the
        // candidate if no safe spot exists. A component member is never the one that moves here.
        bool TryNudgeConflictingNeighbor(PlacementAcceptance.Unfit conflict)
        {
          if (conflict == null || conflict.OutsideMargin.Count > 0 || conflict.Overlapping.Count != 2)
          {
            return false;
          }

          IPartPlacement[] pair = conflict.Overlapping.ToArray();
          int indexA = all.IndexOf(pair[0]);
          int indexB = all.IndexOf(pair[1]);
          if (indexA < 0 || indexB < 0 || (groupsRef[indexA] > 0) == (groupsRef[indexB] > 0))
          {
            return false;
          }

          IPartPlacement neighbor = groupsRef[indexA] > 0 ? pair[1] : pair[0];
          return TryNudgeClear(
            all,
            neighbor,
            (a, b) => NormalClearanceBetween(a, b, rulesBySource),
            (a, b) => PlacementCollision.PlacementNoise,
            intentsRef,
            sheetW,
            sheetH,
            margin,
            commonLineGap,
            geometryTolerance,
            sourceCatalogs);
        }

        reportStage("clearance");
        bool layoutValid = DirectLayoutIsValid(
          all,
          intents,
          rulesBySource,
          sheetW,
          sheetH,
          margin,
          commonLineGap,
          geometryTolerance,
          out string layoutFailureReason,
          out PlacementAcceptance.Unfit layoutConflict,
          cancel,
          sourceCatalogs,
          verdictCache);
        if (!layoutValid && TryNudgeConflictingNeighbor(layoutConflict))
        {
          layoutValid = DirectLayoutIsValid(
            all,
            intents,
            rulesBySource,
            sheetW,
            sheetH,
            margin,
            commonLineGap,
            geometryTolerance,
            out layoutFailureReason,
            out _,
            cancel,
            sourceCatalogs,
            verdictCache);
        }

        if (!layoutValid)
        {
          failure = DirectPlanFailure.Layout;
          failureReason = layoutFailureReason;
          RestoreOriginalPlacements();
          intents.Clear();
          Array.Clear(groups, 0, groups.Length);
          return false;
        }

        if (validateTopology)
        {
          reportStage("topology");
          bool topologyValid = CommonLineTopologyIsValid(
                all,
                intents,
                sheetW,
                sheetH,
                commonLineGap,
                geometryTolerance,
                out failureReason,
                cancel,
                sourceCatalogs);
          if (!topologyValid)
          {
            failure = DirectPlanFailure.Topology;
            RestoreOriginalPlacements();
            intents.Clear();
            Array.Clear(groups, 0, groups.Length);
            return false;
          }
        }

        return true;
      }

      (double Area, double LongAxis, double ShortAxis) ProjectedFootprint(
        IReadOnlyList<DirectCandidate> plan)
      {
        var poses = Enumerable.Repeat(-1, n).ToArray();
        var adjacency = new Dictionary<int, List<(int Other, double Dx, double Dy)>>();
        foreach (DirectCandidate edge in plan)
        {
          cancel.ThrowIfCancellationRequested();
          poses[edge.A] = edge.PoseA;
          poses[edge.B] = edge.PoseB;
          (adjacency.TryGetValue(edge.A, out var fromA)
            ? fromA
            : adjacency[edge.A] = new List<(int, double, double)>()).Add((edge.B, edge.Dx, edge.Dy));
          (adjacency.TryGetValue(edge.B, out var fromB)
            ? fromB
            : adjacency[edge.B] = new List<(int, double, double)>()).Add((edge.A, -edge.Dx, -edge.Dy));
        }

        var offsets = new (double X, double Y)[n];
        var visited = new bool[n];
        foreach (int start in adjacency.Keys.OrderBy(index => index))
        {
          cancel.ThrowIfCancellationRequested();
          if (visited[start])
          {
            continue;
          }

          var queue = new Queue<int>();
          queue.Enqueue(start);
          visited[start] = true;
          while (queue.Count > 0)
          {
            int current = queue.Dequeue();
            foreach ((int other, double dx, double dy) in adjacency[current])
            {
              cancel.ThrowIfCancellationRequested();
              if (visited[other])
              {
                continue;
              }

              offsets[other] = (offsets[current].X + dx, offsets[current].Y + dy);
              visited[other] = true;
              queue.Enqueue(other);
            }
          }
        }

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
          cancel.ThrowIfCancellationRequested();
          if (poses[i] >= 0)
          {
            DirectPlacementOption option = optionsByPart[i][poses[i]];
            minX = Math.Min(minX, option.Poly.MinX + option.X + offsets[i].X);
            minY = Math.Min(minY, option.Poly.MinY + option.Y + offsets[i].Y);
            maxX = Math.Max(maxX, option.Poly.MaxX + option.X + offsets[i].X);
            maxY = Math.Max(maxY, option.Poly.MaxY + option.Y + offsets[i].Y);
          }
          else
          {
            minX = Math.Min(minX, originalPlacements[i].Part.MinX + originalX[i]);
            minY = Math.Min(minY, originalPlacements[i].Part.MinY + originalY[i]);
            maxX = Math.Max(maxX, originalPlacements[i].Part.MaxX + originalX[i]);
            maxY = Math.Max(maxY, originalPlacements[i].Part.MaxY + originalY[i]);
          }
        }

        double width = Math.Max(0, maxX - minX);
        double height = Math.Max(0, maxY - minY);
        return (
          width * height,
          sheetW >= sheetH ? width : height,
          sheetW >= sheetH ? height : width);
      }

      var projectedFootprints = new Dictionary<DirectPlanState, (double Area, double LongAxis, double ShortAxis)>();
      (double Area, double LongAxis, double ShortAxis) ProjectedStateFootprint(DirectPlanState state)
      {
        if (!projectedFootprints.TryGetValue(state, out var footprint))
        {
          projectedFootprints[state] = footprint = ProjectedFootprint(state.Plan);
        }

        return footprint;
      }

      int ComparePlanStates(DirectPlanState left, DirectPlanState right)
      {
        int order = right.TotalLength.CompareTo(left.TotalLength);
        if (order != 0)
        {
          return order;
        }

        order = CompareCandidateFootprints(ProjectedStateFootprint(left), ProjectedStateFootprint(right));
        if (order != 0)
        {
          return order;
        }

        order = right.Plan.Count.CompareTo(left.Plan.Count);
        if (order != 0)
        {
          return order;
        }

        order = left.PoseChanges.CompareTo(right.PoseChanges);
        if (order != 0)
        {
          return order;
        }

        order = left.TotalTravel.CompareTo(right.TotalTravel);
        if (order != 0)
        {
          return order;
        }

        int common = Math.Min(left.CandidateIndexes.Count, right.CandidateIndexes.Count);
        for (int i = 0; i < common; i++)
        {
          cancel.ThrowIfCancellationRequested();
          order = left.CandidateIndexes[i].CompareTo(right.CandidateIndexes[i]);
          if (order != 0)
          {
            return order;
          }
        }

        return left.CandidateIndexes.Count.CompareTo(right.CandidateIndexes.Count);
      }

      int beamWidth = effort switch
      {
        NestEffort.Best => 256,
        NestEffort.Normal => 64,
        _ => 16,
      };
      int validationLimit = effort switch
      {
        NestEffort.Best => 32,
        NestEffort.Normal => 16,
        _ => 8,
      };
      if (n >= 50)
      {
        // Dense grids have hundreds of cycle edges and every strict finalist repeats the same polygon
        // topology/clearance proof. Two deterministic alternatives retain rollback without multiplying
        // Fast's cost by the full beam width.
        validationLimit = Math.Min(validationLimit, 2);
      }

      List<DirectCandidate> actionableCandidates = candidates;
      reportStage("beam");
      var beam = new List<DirectPlanState> { new DirectPlanState(n, maxClusterParts) };
      for (int candidateIndex = 0; candidateIndex < actionableCandidates.Count; candidateIndex++)
      {
        cancel.ThrowIfCancellationRequested();
        DirectCandidate candidate = actionableCandidates[candidateIndex];
        var expanded = new List<DirectPlanState>(beam.Count * 2);
        foreach (DirectPlanState state in beam)
        {
          cancel.ThrowIfCancellationRequested();
          expanded.Add(state); // exclude branch
          DirectPlanState included = state.Include(candidate, candidateIndex);
          if (included != null)
          {
            expanded.Add(included);
          }
        }

        projectedFootprints.Clear();
        expanded.Sort(ComparePlanStates);

        // Pure score truncation can discard the as-yet-empty branch while a long early candidate is
        // still ahead of two shorter later candidates that save more cut in total. Preserve bounded
        // pose diversity (plus the empty branch) so mutually exclusive orientation strategies survive
        // long enough to be compared by their complete, lexicographic objective.
        var nextBeam = new List<DirectPlanState>(beamWidth);
        DirectPlanState empty = expanded.FirstOrDefault(state => state.Plan.Count == 0);
        if (empty != null)
        {
          nextBeam.Add(empty);
        }

        int diverseLimit = Math.Max(2, beamWidth / 4);
        foreach (DirectPlanState state in expanded)
        {
          cancel.ThrowIfCancellationRequested();
          if (nextBeam.Count >= diverseLimit)
          {
            break;
          }

          if (!nextBeam.Any(existing => existing.HasSamePoses(state)))
          {
            nextBeam.Add(state);
          }
        }

        foreach (DirectPlanState state in expanded)
        {
          cancel.ThrowIfCancellationRequested();
          if (nextBeam.Count >= beamWidth)
          {
            break;
          }

          if (!nextBeam.Contains(state))
          {
            nextBeam.Add(state);
          }
        }

        nextBeam.Sort(ComparePlanStates);
        beam = nextBeam;
      }

      projectedFootprints.Clear();
      bool TryApplyCutFirstPlan(
        IReadOnlyList<DirectCandidate> plan,
        bool validateTopology,
        out List<CommonEdgeIntent> intents,
        out int[] groups,
        out DirectPlanFailure failure,
        out string failureReason)
      {
        if (!TryApplyPlan(
          plan,
          validateTopology,
          out intents,
          out groups,
          out failure,
          out failureReason))
        {
          return false;
        }

        return true;
      }

      reportStage("validation");
      DirectPlanState bestFinalist = null;
      foreach (DirectPlanState finalist in beam
        .Where(state => state.Plan.Count > 0)
        .OrderBy(state => state, Comparer<DirectPlanState>.Create(ComparePlanStates))
        .Take(validationLimit))
      {
        if (TryApplyPlan(
          finalist.Plan,
          true,
          out _,
          out _,
          out _,
          out _))
        {
          int planOrder = bestFinalist == null ? -1 : ComparePlanStates(finalist, bestFinalist);
          if (bestFinalist == null
              || planOrder < 0)
          {
            bestFinalist = finalist;
          }
        }
      }

      // Validate first, rank on committed common-line length before footprint, then apply the winner once
      // more. Every candidate still proves fit, clearance and topology within this same sheet.
      if (bestFinalist != null
          && TryApplyCutFirstPlan(
            bestFinalist.Plan,
            true,
            out List<CommonEdgeIntent> beamIntents,
            out int[] beamGroups,
            out _,
            out _))
      {
        result.Groups = beamGroups;
        result.Edges.AddRange(beamIntents);
        PublishUnsupportedCurvesOnly(beamIntents);
        return result;
      }

      var parent = Enumerable.Range(0, n).ToArray();
      var componentSizes = Enumerable.Repeat(1, n).ToArray();
      int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
      var selectedPoses = Enumerable.Repeat(-1, n).ToArray();
      var occupiedTreeSpans = new Dictionary<DirectBoundaryKey, List<(double Start, double End)>>();
      var givenUpPairs = new HashSet<(int A, int B)>();
      var topologyFailuresByPair = new Dictionary<(int A, int B), string>();
      var tree = new List<DirectCandidate>();

      void ResetForestState()
      {
        occupiedTreeSpans.Clear();
        Array.Fill(selectedPoses, -1);
        for (int i = 0; i < parent.Length; i++)
        {
          cancel.ThrowIfCancellationRequested();
          parent[i] = i;
          componentSizes[i] = 1;
        }
      }

      bool TryAddToForest(DirectCandidate candidate)
      {
        bool SpanAvailable(DirectBoundaryKey key, double start, double end)
          => !occupiedTreeSpans.TryGetValue(key, out var spans)
            || spans.All(span => Math.Max(start, span.Start) >= Math.Min(end, span.End) - 1e-9);
        void Occupy(DirectBoundaryKey key, double start, double end)
        {
          if (!occupiedTreeSpans.TryGetValue(key, out var spans))
          {
            occupiedTreeSpans[key] = spans = new List<(double, double)>();
          }

          spans.Add((start, end));
        }

        int rootA = Find(candidate.A);
        int rootB = Find(candidate.B);
        if (rootA == rootB
            || componentSizes[rootA] + componentSizes[rootB] > maxClusterParts
            || !SpanAvailable(
              DirectBoundaryKey.Create(candidate.A, candidate.EdgeA, candidate.SpanA),
              candidate.EdgeAStart,
              candidate.EdgeAEnd)
            || !SpanAvailable(
              DirectBoundaryKey.Create(candidate.B, candidate.EdgeB, candidate.SpanB),
              candidate.EdgeBStart,
              candidate.EdgeBEnd)
            || (selectedPoses[candidate.A] >= 0 && selectedPoses[candidate.A] != candidate.PoseA)
            || (selectedPoses[candidate.B] >= 0 && selectedPoses[candidate.B] != candidate.PoseB))
        {
          return false;
        }

        parent[rootB] = rootA;
        componentSizes[rootA] += componentSizes[rootB];
        componentSizes[rootB] = 0;
        selectedPoses[candidate.A] = candidate.PoseA;
        selectedPoses[candidate.B] = candidate.PoseB;
        Occupy(
          DirectBoundaryKey.Create(candidate.A, candidate.EdgeA, candidate.SpanA),
          candidate.EdgeAStart,
          candidate.EdgeAEnd);
        Occupy(
          DirectBoundaryKey.Create(candidate.B, candidate.EdgeB, candidate.SpanB),
          candidate.EdgeBStart,
          candidate.EdgeBEnd);
        tree.Add(candidate);
        return true;
      }

      void PublishFailures(IReadOnlyList<CommonEdgeIntent> acceptedIntents)
      {
        HashSet<(int A, int B)> acceptedPairs = AcceptedPairIndexes(acceptedIntents);
        var failedPairs = new HashSet<(int A, int B)>(
          givenUpPairs.Where(pair => !acceptedPairs.Contains(pair)));
        failedPairs.UnionWith(PublishUnsupportedCurves(acceptedIntents));
        result.GivenUp = failedPairs.Count;
        foreach (var failed in topologyFailuresByPair)
        {
          cancel.ThrowIfCancellationRequested();
          if (!acceptedPairs.Contains(failed.Key))
          {
            AddCommonLineFallback(
              result,
              new[] { originalPlacements[failed.Key.A], originalPlacements[failed.Key.B] },
              failed.Value);
          }
        }
      }

      void BuildIncrementalForest(bool validateTopology)
      {
        RestoreOriginalPlacements();
        tree.Clear();
        ResetForestState();
        foreach (DirectCandidate candidate in candidates)
        {
          cancel.ThrowIfCancellationRequested();
          if (!TryAddToForest(candidate))
          {
            continue;
          }

          if (!TryApplyPlan(
                tree,
                validateTopology,
                out _,
                out _,
                out DirectPlanFailure failure,
                out string failureReason))
          {
            RecordDirectFailure(tree.Count, failure, failureReason);
            if (failure == DirectPlanFailure.EdgeGeometry || failure == DirectPlanFailure.Topology)
            {
              topologyFailuresByPair[(candidate.A, candidate.B)] =
                failureReason ?? CommonLineFallbackNotice.TopologyReason;
            }

            tree.RemoveAt(tree.Count - 1);
            DirectCandidate[] acceptedTree = tree.ToArray();
            tree.Clear();
            ResetForestState();
            foreach (DirectCandidate accepted in acceptedTree)
            {
              cancel.ThrowIfCancellationRequested();
              _ = TryAddToForest(accepted);
            }

            _ = givenUpPairs.Add((candidate.A, candidate.B));
          }
        }
      }

      // The normal case is a sound candidate layout. Build its deterministic forest without repeatedly
      // resolving the same ever-growing cluster, then validate the complete answer once. A 10x10 grid
      // otherwise performs 99 polygon unions and full pair-clearance scans before reaching the result.
      // If the optimistic plan is not valid, the incremental pass below retains the important recovery
      // behaviour: reject only the offending relationship, restore every pose, and try the next one.
      foreach (DirectCandidate candidate in candidates)
      {
        cancel.ThrowIfCancellationRequested();
        _ = TryAddToForest(candidate);
      }

      if (tree.Count > 0
          && TryApplyCutFirstPlan(
            tree,
            true,
            out List<CommonEdgeIntent> optimisticIntents,
            out int[] optimisticGroups,
            out _,
            out _))
      {
        result.Groups = optimisticGroups;
        result.Edges.AddRange(optimisticIntents);
        PublishUnsupportedCurvesOnly(optimisticIntents);
        return result;
      }

      // Layout and edge checks identify the candidate that physically cannot join the forest. Resolving
      // the complete cluster boundary after every accepted prefix is quadratic work on dense jobs, so do
      // that strict check once for the resulting plan. If it exposes a topology-only failure, rebuild with
      // strict prefix validation to retain the planner's candidate-by-candidate recovery behaviour.
      BuildIncrementalForest(validateTopology: false);

      if (tree.Count == 0)
      {
        PublishFailures(Array.Empty<CommonEdgeIntent>());
        RestoreOriginalPlacements();
        return result;
      }

      if (TryApplyCutFirstPlan(
            tree,
            true,
            out List<CommonEdgeIntent> finalIntents,
            out int[] finalGroups,
            out _,
            out _))
      {
        PublishFailures(finalIntents);
        result.Groups = finalGroups;
        result.Edges.AddRange(finalIntents);
        return result;
      }

      // The fast incremental pass intentionally deferred topology. Discard its provisional diagnostics
      // and retry strictly so a bad relationship is rejected without hiding a later valid alternative.
      givenUpPairs.Clear();
      topologyFailuresByPair.Clear();
      BuildIncrementalForest(validateTopology: true);
      if (tree.Count == 0)
      {
        PublishFailures(Array.Empty<CommonEdgeIntent>());
        RestoreOriginalPlacements();
        return result;
      }

      if (!TryApplyCutFirstPlan(
            tree,
            true,
            out finalIntents,
            out finalGroups,
            out _,
            out _))
      {
        RestoreOriginalPlacements();
        PublishFailures(Array.Empty<CommonEdgeIntent>());
        result.GivenUp++;
        return result;
      }

      PublishFailures(finalIntents);
      result.Groups = finalGroups;
      result.Edges.AddRange(finalIntents);
      return result;
    }

    private static void RecordDirectFailure(int planCount, DirectPlanFailure failure, string reason)
    {
      if (DiagDirectFailures.Count >= 64)
      {
        return;
      }

      DiagDirectFailures.Enqueue(FormattableString.Invariant(
        $"direct-plan edges={planCount} failure={failure} reason={reason ?? string.Empty}"));
    }

    /// <summary>Past the sheet edge, so this placement is not cut here and stays in the pool. The cull in
    /// <see cref="MapAndCompact"/> uses this same test, and both must keep using it: a scope that
    /// disagreed with the cull would either judge a part that is about to be dropped or drop one that was
    /// never judged.</summary>
    private static bool IsOutsideSheet(IPartPlacement placement, double sheetW, double sheetH)
    {
      const double tol = 1e-3;
      INfp placed = placement?.PlacedPart;
      return placed != null
        && (placed.MinX < -tol || placed.MinY < -tol || placed.MaxX > sheetW + tol || placed.MaxY > sheetH + tol);
    }

    private static bool DirectLayoutIsValid(
      IReadOnlyList<IPartPlacement> placements,
      IReadOnlyList<CommonEdgeIntent> intents,
      IReadOnlyDictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> rulesBySource,
      double sheetW,
      double sheetH,
      double margin,
      double commonLineGap,
      double geometryTolerance,
      out string failureReason,
      out PlacementAcceptance.Unfit unfit,
      CancellationToken cancel = default,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null,
      CommonEdgeClearance.PairVerdictCache verdictCache = null)
    {
      // Sparrow is a strip packer and is offered more than a sheetful on purpose, so some of what it
      // returns hangs past the sheet and is dropped by the cull in MapAndCompact. Those parts are not
      // cut here, so they get no vote on how the parts that ARE cut may share an edge. Judging them
      // was what left a job asking for one more part than fits with no shared cuts at all: a single
      // leftover past the edge failed the baseline and every candidate after it.
      // Only whole-sheet overhang is dropped, exactly the cull's own test. A part inside the sheet but
      // past the margin is still judged, because that one really is cut here.
      placements = placements.Where(placement => !IsOutsideSheet(placement, sheetW, sheetH)).ToList();
      unfit = CommonEdgeClearance.FindUnfit(
        placements,
        sheetW,
        sheetH,
        margin,
        (a, b) => NormalClearanceBetween(a, b, rulesBySource),
        (a, b) => PlacementCollision.PlacementNoise,
        ClearanceEdges(intents),
        commonLineGap,
        geometryTolerance,
        stopAtFirst: true, cancel: cancel, sourceCatalogs: sourceCatalogs, verdictCache: verdictCache);
      string conflicts = string.Join(", ", unfit.Overlapping.Select(
        placement => $"source={placement.Source} at {placement.X:F3},{placement.Y:F3}"));
      failureReason = unfit.OutsideMargin.Count > 0
        ? $"outside sheet margin ({unfit.OutsideMargin.Count} placement(s))"
        : unfit.Overlapping.Count > 0
          ? $"normal clearance conflict ({conflicts})"
          : string.Empty;
      return unfit.All.Count == 0;
    }

    /// <summary>The direct-plan schema's own spacing contract: half of each side's configured gap. This is
    /// distinct from <see cref="AcceptanceClearanceOf"/>, which uses the measured kerf at final acceptance.</summary>
    private static double NormalClearanceBetween(
      IPartPlacement a,
      IPartPlacement b,
      IReadOnlyDictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> rulesBySource)
    {
      double sa = rulesBySource.TryGetValue(a.Source, out var ra) ? Math.Max(0, ra.Spacing) : 0;
      double sb = rulesBySource.TryGetValue(b.Source, out var rb) ? Math.Max(0, rb.Spacing) : 0;
      return (sa + sb) / 2.0;
    }

    /// <summary>Final sheet acceptance covers schema-aware recorded spans and the ordinary clearance
    /// everywhere else. It runs again after hole filling and template cloning.</summary>
    private static bool FinalLayoutIsValid(
      IReadOnlyList<IPartPlacement> placements,
      IReadOnlyList<CommonEdgeIntent> intents,
      Dictionary<int, Loaded> loadedById,
      double sheetW,
      double sheetH,
      double margin,
      double commonLineGap,
      double geometryTolerance,
      CancellationToken cancel = default)
      => CommonEdgeClearance.FindUnfit(
        placements,
        sheetW,
        sheetH,
        margin,
        (a, b) => AcceptanceClearanceOf(a, b, loadedById),
        (a, b) => PlacementCollision.SliverFor(0, 0),
        ClearanceEdges(intents),
        commonLineGap,
        geometryTolerance,
        cancel: cancel).All.Count == 0;

    private static bool CommonLineTopologyIsValid(
      IReadOnlyList<IPartPlacement> placements,
      IReadOnlyList<CommonEdgeIntent> intents,
      double sheetW,
      double sheetH,
      double commonLineGap,
      double geometryTolerance,
      out string failureReason,
      CancellationToken cancel = default,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      failureReason = null;
      if (intents == null || intents.Count == 0)
      {
        return true;
      }

      try
      {
        var records = new List<CommonEdge>(intents.Count);
        foreach (CommonEdgeIntent intent in intents)
        {
          cancel.ThrowIfCancellationRequested();
          if (!TryCreateCommonEdgeRecord(intent, commonLineGap, geometryTolerance, out CommonEdge record, sourceCatalogs))
          {
            failureReason = CommonLineFallbackNotice.TopologyReason;
            return false;
          }

          records.Add(record);
        }
        var placement = new SheetPlacement(
          placementType: PlacementTypeEnum.BoundingBox,
          sheet: Sheet.NewSheet(0, (int)Math.Round(sheetW), (int)Math.Round(sheetH)),
          partPlacements: placements,
          mergedLength: 0,
          requiresMergedLines: false,
          commonEdges: records,
          commonLineGap: commonLineGap,
          geometryTolerance: geometryTolerance);
        _ = CommonLineGeometryResolver.Resolve(placement,
          sourceCatalogs == null ? null : new Dictionary<string, DxfContourCatalog>(sourceCatalogs), cancel);
        return true;
      }
      catch (CommonLineValidationException ex)
      {
        failureReason = ex.Message;
        return false;
      }
    }

    /// <summary>
    /// One integration seam between the RasterNest planner and the exact geometry resolver. Every selected
    /// interval is translated into a schema-2 source span here; callers never infer a cut from proximity.
    /// </summary>
    private static bool TryCreateCommonEdgeRecord(
      CommonEdgeIntent intent,
      double commonLineGap,
      double geometryTolerance,
      out CommonEdge commonEdge,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      commonEdge = null;
      if (intent == null)
      {
        return false;
      }

      if (intent.SpanA != null || intent.SpanB != null)
      {
        return intent.SpanA != null
          && intent.SpanB != null
          && CommonLineGeometryResolver.TryCreateCommonEdge(
            intent.PartA,
            intent.SpanA,
            intent.PartB,
            intent.SpanB,
            commonLineGap,
            geometryTolerance,
            out commonEdge,
            sourceCatalogs);
      }

      return CommonLineGeometryResolver.TryCreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        commonLineGap,
        geometryTolerance,
        out commonEdge,
        sourceCatalogs);
    }

    private static void AddCommonLineFallback(
      DirectCommonLineSnapResult result,
      IEnumerable<IPartPlacement> placements,
      string validationReason)
    {
      string reason = CommonLineFallbackNotice.ReasonFromValidation(validationReason);
      IEnumerable<IPartPlacement> affected = placements?.Where(placement => placement?.Part != null)
        ?? Enumerable.Empty<IPartPlacement>();
      if (string.Equals(reason, CommonLineFallbackNotice.ContourFormatReason, StringComparison.Ordinal))
      {
        // The relationship falls back for both parts, but name the drawing whose curve caused it. That is
        // the actionable row in Edit Part; blaming its straight-edged peer would send the operator astray.
        affected = affected.Where(placement =>
          (placement.Part as NoFitPolygon)?.GetCurvedSegments()?.Any(curved => curved) == true);
      }

      foreach (IPartPlacement placement in affected)
      {
        string source = placement.Part.Name;
        string drawing = string.IsNullOrWhiteSpace(source)
          ? FormattableString.Invariant($"Part {placement.Source + 1}")
          : Path.GetFileNameWithoutExtension(source);
        if (!result.Fallbacks.Any(existing =>
          string.Equals(existing.Drawing, drawing, StringComparison.OrdinalIgnoreCase)
          && string.Equals(existing.Reason, reason, StringComparison.Ordinal)))
        {
          result.Fallbacks.Add(new CommonLineFallbackNotice(drawing, reason));
        }
      }
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
      double dx = bx - ax;
      double dy = by - ay;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double SignedArea(SvgPoint[] pts)
    {
      double sum = 0;
      for (int i = 0; i < pts.Length; i++)
      {
        var p = pts[i];
        var q = pts[(i + 1) % pts.Length];
        sum += (p.X * q.Y) - (q.X * p.Y);
      }

      return sum;
    }

    /// <summary>True when no part's outline overlaps another's by more than a sliver: two parts may be
    /// seated at a shared cut, but neither may eat into the other.
    /// <para>When <paramref name="movedOnly"/> is given, only pairs involving one of those parts are
    /// tested. Everything else was already clear and has not moved since, so re-testing it turns the
    /// group-by-group retry from O(groups x n^2) clipping into something affordable.</para>
    /// </summary>
    private static bool OutlinesClearEveryone(List<IPartPlacement> all, Dictionary<int, INfp> outlineById, HashSet<int> movedOnly)
    {
      double scale = SvgNest.Config.ClipperScale;
      int n = all.Count;
      var partPaths = new List<IntPoint>[n];
      var toolPaths = new List<IntPoint>[n];
      for (int i = 0; i < n; i++)
      {
        partPaths[i] = ShiftPath(DeepNestClipper.ScaleUpPath(all[i].Part.Points, scale), (long)Math.Round(all[i].X * scale), (long)Math.Round(all[i].Y * scale));
        if (outlineById.TryGetValue(all[i].Source, out var tool) && tool != null)
        {
          var rot = Math.Abs(all[i].Rotation % 360) < 1e-9 ? tool : tool.Rotate(all[i].Rotation);
          toolPaths[i] = ShiftPath(DeepNestClipper.ScaleUpPath(rot.Points, scale), (long)Math.Round(all[i].X * scale), (long)Math.Round(all[i].Y * scale));
        }
        else
        {
          toolPaths[i] = partPaths[i];
        }
      }

      double invadeLimit = 0.02 * scale * scale; // area units²; ignore rounding slivers
      for (int i = 0; i < n; i++)
      {
        for (int j = 0; j < n; j++)
        {
          if (i == j || (movedOnly != null && !movedOnly.Contains(i) && !movedOnly.Contains(j)))
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

    /// <summary>True when no size has a sheet left once this one is taken, so whatever this pack leaves
    /// behind comes back to the operator as unplaced rather than landing somewhere else.</summary>
    private static bool IsLastChanceFor(StockSize size, List<StockSize> sizes)
    {
      if (size.Unlimited)
      {
        return false; // there is always another sheet of it, so nothing is about to be lost
      }

      foreach (var other in sizes)
      {
        int left = ReferenceEquals(other, size) ? other.Remaining - 1 : other.Remaining;
        if (other.Unlimited || left > 0)
        {
          return false;
        }
      }

      return true;
    }

    /// <summary>
    /// Judge a finished sheet by the rule that decides whether it may be cut, and hand back whatever fails
    /// so the next sheet can take it. Returns how many placements were given up.
    /// <para>The engine cannot simply be trusted to have honoured the clearance it was asked for. Measured
    /// on the job from issue #2: the compaction slide applied THIRTY moves that its own acceptance test
    /// allowed and this rule rejects, and two parts came to rest 0.01 mm inside a 9 mm clearance. The two
    /// tests are built differently and agreeing them exactly is a long tail; what closes the hole for good
    /// is checking the ANSWER against the rule the operator will be held to, rather than trusting the
    /// search that produced it.</para>
    /// <para>Only one side of each offending pair is given back. Both are equally guilty, and dropping
    /// both would give away a part that has somewhere perfectly good to sit.</para>
    /// </summary>
    private static int DeferUnfit(
      List<IPartPlacement> placements,
      Dictionary<int, int> placedBySrc,
      Dictionary<int, Loaded> loadedById,
      List<CommonEdgeIntent> commonEdges,
      double sheetW,
      double sheetH,
      double margin,
      double commonLineGap,
      double geometryTolerance)
    {
      int deferred = 0;

      // Whatever is dropped may free the pair it was fighting with, so re-judge until the sheet is clean.
      // The budget is worked out ONCE and counts every pass, because not every pass gives a part up: a
      // successful nudge fixes the sheet without shrinking it. Testing `guard <= placements.Count` inside
      // the loop compared a rising counter against a falling count and could quit with the sheet still
      // unfit, which is the opposite of what this method exists to guarantee.
      int budget = (placements.Count * 2) + 8;
      while (budget-- > 0)
      {
        var unfit = CommonEdgeClearance.FindUnfit(
          placements,
          sheetW,
          sheetH,
          margin,
          (a, b) => AcceptanceClearanceOf(a, b, loadedById),
          (a, b) => PlacementCollision.SliverFor(0, 0),
          ClearanceEdges(commonEdges),
          commonLineGap,
          geometryTolerance);

        if (unfit.All.Count == 0)
        {
          return deferred;
        }

        // Give up the CHEAPEST offender, by area. "The last one placed" reads like the fair choice and is
        // not: SelectBatch breaks ties on the source id, a mirrored population always carries a higher id
        // than the hand it was made from, so mirrored copies enter every batch last, are placed last, and
        // were therefore picked as the victim almost every time. A job of five plus two mirrored lost a
        // mirrored one over and over. Area is blind to which hand a part is.
        var victim = unfit.All
          .OrderBy(p => Math.Abs(p.PlacedPart?.Area ?? 0))
          .ThenByDescending(p => p.Source)
          .FirstOrDefault(p => placements.Contains(p));
        if (victim == null)
        {
          return deferred;
        }

        // Try to MOVE it before giving it up. What is usually wrong is a hair: measured at a hundredth of
        // a millimetre inside a nine millimetre clearance, and giving back a whole part for that would
        // spend a sheet to buy nothing. Only when it genuinely has nowhere to go does it go back.
        var component = CommonEdgeComponent(victim, commonEdges);
        if (component.Count == 1
            && TryNudgeClear(
              placements,
              victim,
              (a, b) => AcceptanceClearanceOf(a, b, loadedById),
              (a, b) => PlacementCollision.SliverFor(0, 0),
              commonEdges,
              sheetW,
              sheetH,
              margin,
              commonLineGap,
              geometryTolerance))
        {
          continue;
        }

        // A recorded cluster is rigid. Moving or dropping only one member would invalidate the record and
        // leave its mate at the reduced gap, so the entire connected component goes back to the pool.
        foreach (IPartPlacement member in component)
        {
          placements.Remove(member);
          if (placedBySrc.TryGetValue(member.Source, out int n))
          {
            if (n <= 1)
            {
              placedBySrc.Remove(member.Source);
            }
            else
            {
              placedBySrc[member.Source] = n - 1;
            }
          }

          deferred++;
        }

        commonEdges?.RemoveAll(e => component.Contains(e.PartA) || component.Contains(e.PartB));
      }

      return deferred;
    }

    /// <summary>
    /// Walk one part off its neighbours without taking it off the sheet. True when the sheet came clean.
    /// <para>The ladder is a FRACTION of the clearance rather than a list of numbers, because the error it
    /// is closing is proportional to the clearance too: a figure that reads as a comfortable shove on an
    /// inch drawing is a quarter of a millimetre on a metric one, and the existing separation pass has
    /// exactly that bug. It never pushes further than the clearance itself, so a part that needs more than
    /// that is not being nudged, it is in the wrong place, and it goes back in the pool.</para>
    /// </summary>
    private static bool TryNudgeClear(
      List<IPartPlacement> placements,
      IPartPlacement victim,
      Func<IPartPlacement, IPartPlacement, double> clearanceBetween,
      Func<IPartPlacement, IPartPlacement, double> sliverBetween,
      List<CommonEdgeIntent> commonEdges,
      double sheetW,
      double sheetH,
      double margin,
      double commonLineGap,
      double geometryTolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      double clearance = placements
        .Where(p => !ReferenceEquals(p, victim))
        .Select(p => clearanceBetween(victim, p))
        .DefaultIfEmpty(0)
        .Max();
      if (clearance <= 0)
      {
        return false;
      }

      double x0 = victim.X;
      double y0 = victim.Y;

      // Forty eight positions against every neighbour. Resolving the edge records and rebuilding the
      // neighbours' boundaries is the expensive part and none of it depends on where the victim is
      // being tried, so it is shared across the whole search instead of redone per position per pair.
      var probe = new CommonEdgeClearance.ProbeContext(
        ClearanceEdges(commonEdges), commonLineGap, geometryTolerance, sourceCatalogs);

      foreach (double step in new[] { 0.002, 0.01, 0.05, 0.2, 0.5, 1.0 })
      {
        double d = clearance * step;
        foreach (var (dx, dy) in new[] { (d, 0.0), (-d, 0.0), (0.0, d), (0.0, -d), (d, d), (-d, -d), (d, -d), (-d, d) })
        {
          victim.X = x0 + dx;
          victim.Y = y0 + dy;
          probe.Moved(victim); // it is the same object at a new place; its cached boundary is now a lie
          if (IsClearWhereItStands(placements, victim, clearanceBetween, sliverBetween, probe, sheetW, sheetH, margin))
          {
            return true;
          }
        }
      }

      victim.X = x0;
      victim.Y = y0;
      return false;
    }

    /// <summary>One part against the sheet and against everybody else, by the rule the app judges with.</summary>
    private static bool IsClearWhereItStands(
      List<IPartPlacement> placements,
      IPartPlacement victim,
      Func<IPartPlacement, IPartPlacement, double> clearanceBetween,
      Func<IPartPlacement, IPartPlacement, double> sliverBetween,
      CommonEdgeClearance.ProbeContext probe,
      double sheetW,
      double sheetH,
      double margin)
    {
      if (PlacementAcceptance.IsOutsideUsableArea(victim, sheetW, sheetH, margin))
      {
        return false;
      }

      foreach (var other in placements)
      {
        if (ReferenceEquals(other, victim) || other?.PlacedPart == null)
        {
          continue;
        }

        if (probe.TooClose(victim, other, clearanceBetween(victim, other), sliverBetween(victim, other)))
        {
          return false;
        }
      }

      return true;
    }

    /// <summary>The ordinary clearance outside recorded common-edge spans. Being allowed to share is not
    /// itself an exception: a pair earns a smaller gap only where it owns a recorded edge.</summary>
    private static double AcceptanceClearanceOf(
      IPartPlacement a,
      IPartPlacement b,
      Dictionary<int, Loaded> loadedById)
    {
      if (!loadedById.TryGetValue(a.Source, out var la) || !loadedById.TryGetValue(b.Source, out var lb))
      {
        return 0;
      }

      return AcceptanceClearanceFor(la.EffSpacing, lb.EffSpacing);
    }

    internal static double AcceptanceClearanceFor(double firstSpacing, double secondSpacing)
      => (Math.Max(0, firstSpacing) + Math.Max(0, secondSpacing)) / 2.0;

    private static HashSet<IPartPlacement> CommonEdgeComponent(
      IPartPlacement seed,
      IReadOnlyList<CommonEdgeIntent> commonEdges)
    {
      var component = new HashSet<IPartPlacement> { seed };
      bool grew;
      do
      {
        grew = false;
        foreach (CommonEdgeIntent edge in commonEdges ?? Array.Empty<CommonEdgeIntent>())
        {
          if (component.Contains(edge.PartA) || component.Contains(edge.PartB))
          {
            grew |= component.Add(edge.PartA);
            grew |= component.Add(edge.PartB);
          }
        }
      }
      while (grew);

      return component;
    }

    private static IReadOnlyList<CommonEdgeClearance.EdgeReference> ClearanceEdges(
      IReadOnlyList<CommonEdgeIntent> commonEdges)
      => (commonEdges ?? Array.Empty<CommonEdgeIntent>())
        .Select(edge => new CommonEdgeClearance.EdgeReference(
          edge.PartA,
          edge.EdgeAIndex,
          edge.PartB,
          edge.EdgeBIndex,
          edge.EdgeAStart,
          edge.EdgeAEnd,
          edge.EdgeBStart,
          edge.EdgeBEnd,
          edge.SpanA,
          edge.SpanB))
        .ToList();

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

    private static (List<IPartPlacement> Placements, List<CommonEdgeIntent> CommonEdges) ClonePlacements(
      List<IPartPlacement> src,
      IReadOnlyList<CommonEdgeIntent> commonEdges)
    {
      var clone = new List<IPartPlacement>(src.Count);
      var map = new Dictionary<IPartPlacement, IPartPlacement>();
      foreach (var pp in src)
      {
        var copy = new PartPlacement(pp.Part) { X = pp.X, Y = pp.Y, Rotation = pp.Rotation, Source = pp.Source, IsMirrored = pp.IsMirrored };
        clone.Add(copy);
        map[pp] = copy;
      }

      var clonedEdges = new List<CommonEdgeIntent>();
      foreach (CommonEdgeIntent edge in commonEdges ?? Array.Empty<CommonEdgeIntent>())
      {
        if (map.TryGetValue(edge.PartA, out IPartPlacement partA)
            && map.TryGetValue(edge.PartB, out IPartPlacement partB))
        {
          clonedEdges.Add(new CommonEdgeIntent(
            partA,
            edge.EdgeAIndex,
            partB,
            edge.EdgeBIndex,
            edge.EdgeAStart,
            edge.EdgeAEnd,
            edge.EdgeBStart,
            edge.EdgeBEnd,
            edge.SpanA,
            edge.SpanB));
        }
      }

      return (clone, clonedEdges);
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

                  // Fit on the TOOLING footprint (kerf + lead-ins), not the bare outline — a part dropped
                  // into a hole has to keep its cut clear of the surrounding part just like any other.
                  var forFit = loaded.Nfp;
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

    /// <summary>How many independent sparrow searches are raced in one wave. Fixed, because the wave's
    /// composition is part of the answer, not part of the scheduling.</summary>
    private const int WaveSize = 8;

    /// <summary>Threads INSIDE one engine process, per effort.</summary>
    /// <remarks>
    /// <para>The engine defaults to eight and we race several searches at once, so leaving it there asked a
    /// machine for sixty four busy threads and they queued behind each other rather than cooperating.</para>
    /// <para>What the right number is turns out to depend on how long the search runs, so it is chosen per
    /// effort and measured, not assumed. On the fourteen part reference job, free strip left and seconds
    /// taken, one thread against two:</para>
    /// <code>
    ///   rotation  effort   1 thread        2 threads
    ///   4-way     Fast     196 mm  1.5 s    57 mm  1.3 s
    ///   4-way     Normal   359 mm   29 s   367 mm   15 s
    ///   4-way     Best     388 mm  124 s   481 mm   95 s
    ///   free      Fast     144 mm   14 s   159 mm  6.1 s
    ///   free      Normal   272 mm   43 s   298 mm   29 s
    ///   free      Best     292 mm  219 s   334 mm  190 s
    /// </code>
    /// <para>Two threads win on five of the six, on BOTH counts at once, and the exception is the one with the
    /// smallest budget and the fewest races, where the run is short enough to be a lottery and the second
    /// thread drew badly. So Fast keeps one. Giving Fast more races instead was measured and rejected: it
    /// recovered the strip but took free rotation from 6 s to 32 s, which is not a Fast.</para>
    /// <para>A constant per effort rather than something derived from the core count, because the worker count
    /// is part of the search: each worker carries its own generator and the best of their competing proposals
    /// is kept, so a machine with a different number of them lands on a different layout for the same job.</para>
    /// </remarks>
    private static int SearchWorkers(NestEffort effort) => effort == NestEffort.Fast ? 1 : 2;

    /// <summary>How hard a sheet is searched, in sparrow iterations, for each <see cref="NestEffort"/>.
    /// An "iteration" is one pass of sparrow's outer loop.</summary>
    /// <remarks>
    /// <para>These replace a budget of <c>max(15, parts * 0.5)</c>, which for any ordinary job was just the
    /// floor of 15. It was calibrated on the wrong metric: the note it carried said a 40 part sheet "reaches
    /// its best result by about 20 and does not improve at 40, 70, 100, 200 or 400", and that was measured in
    /// PARTS PLACED, which saturates almost at once. Measured in EXTENT, the same range is still climbing
    /// steeply.</para>
    /// <para>Measured the way the app actually runs, best of eight races on the fourteen part reference job,
    /// reporting the width the winner packs them into and the wall clock for the whole race:</para>
    /// <code>
    ///   iterations     width      race
    ///          200   2644 mm     1.0 s
    ///          300   2527 mm     1.4 s
    ///          450   2362 mm     3.0 s
    ///          600   2212 mm    10.5 s
    ///          800   2147 mm    53 s
    /// </code>
    /// <para>The bar is 2247.7 mm, which is what another nester leaves on that job, so 600 clears it in about
    /// ten seconds. 300 is the last point that still feels instant. 800 is the floor of the curve at five
    /// times the price, and past 1250 there is nothing left to buy at any price.</para>
    /// <para>The clock is set by the BEST race, not the average: on that job the seed that finished in four
    /// seconds returned 2292 mm and the three that took the longest returned 2146 to 2179. A search runs long
    /// precisely because it keeps finding something, so buying quality here is buying time by definition.</para>
    /// <para>NOT scaled by part count, deliberately. What the budget buys is shrink steps of the strip, and how
    /// many of those a job needs is a property of how far its first layout sits from a tight one, not of how
    /// many parts it has. A sparse tail sheet converges long before it spends the budget anyway, because its
    /// iterations are cheap.</para>
    /// </remarks>
    private static int BudgetFor(NestEffort effort) => effort switch
    {
      NestEffort.Fast => 300,
      NestEffort.Best => 800,
      _ => 600,
    };


    /// <summary>How many searches run at the same instant. This is the ONLY place the machine is allowed
    /// to influence a nest, and it influences only how long it takes — a slower machine finishes the same
    /// wave later, it does not compute a different one. SHEETNEST_MAX_CONC overrides it, which is how the
    /// "any machine, same nest" claim is actually tested rather than asserted.</summary>
    private static int Concurrency()
    {
      var env = Environment.GetEnvironmentVariable("SHEETNEST_MAX_CONC");
      if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
      {
        return n;
      }

      // One thread per race now (see SearchWorkers), so the machine can afford one race per core rather
      // than the half it was given back when each race brought eight threads of its own.
      return Math.Max(1, Environment.ProcessorCount);
    }

    /// <summary>How long a sheet's search runs, in sparrow iterations. Deliberately NOT a function of the
    /// clock or of the machine: this is the number that has to match for two computers to agree on a
    /// nest. Overridable via SHEETNEST_NEST_ITERS for calibration.</summary>
    internal static int IterationBudget(NestEffort effort)
    {
      var env = Environment.GetEnvironmentVariable("SHEETNEST_NEST_ITERS");
      if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0)
      {
        return n;
      }

      return BudgetFor(effort);
    }

    /// <summary>The budget for the last-chance re-pack: one notch harder than the operator asked for, because
    /// by then the alternative is handing back parts that will not be cut at all.</summary>
    private static int LastChanceBudget(NestEffort effort)
      => BudgetFor(effort == NestEffort.Fast ? NestEffort.Normal : NestEffort.Best);

    /// <summary>How long a single engine process may run before it is killed. A WATCHDOG, not a budget: a
    /// search must end because it ran out of iterations, never because it ran out of seconds.</summary>
    /// <remarks>
    /// Deliberately far above any healthy run, because a kill is TOTAL: the engine writes its solution once,
    /// at the end, so there is nothing to salvage and the whole race is lost. The old fixed 150 s was already
    /// inside the range a legitimate search uses now, and it showed: a twelve part job on a roomy sheet takes
    /// about 270 s at Normal, and under load it crossed the leash and came back as no nest at all. Waiting is
    /// the operator's call and there is a Cancel button for it; being killed silently is not.
    /// <para>Fast gets the same leash as Normal rather than a shorter one, because the last-chance re-pack
    /// runs a notch harder than the effort asked for: a short leash sized to Fast's own budget killed the very
    /// retry that was there to stop parts being handed back uncut.</para>
    /// </remarks>
    private static int WatchdogSeconds(int perSheetBudgetSec, NestEffort effort)
    {
      int leash = Math.Max(60, perSheetBudgetSec * 30);
      return effort switch
      {
        NestEffort.Best => leash * 24,
        _ => leash * 8,
      };
    }

    /// <summary>How many independent sparrow searches to race per sheet (best-of-N). A constant: it used
    /// to be <c>ProcessorCount >= 12 ? 3 : 2</c>, which handed a smaller machine fewer attempts and so a
    /// different — and on average worse — nest for the very same job. Overridable via
    /// SHEETNEST_NEST_TRIES for experiments. Always ≥2 so a single unlucky run can't stand.</summary>
    private static int NestTries(NestEffort effort)
    {
      var env = Environment.GetEnvironmentVariable("SHEETNEST_NEST_TRIES");
      if (int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
      {
        return Math.Min(n, 8);
      }

      return effort == NestEffort.Fast ? 2 : 3;
    }

    /// <summary>The fewest races a one-off sheet gets at each effort.</summary>
    /// <remarks>
    /// Best does NOT get more races than Normal, and that is a measured decision rather than an oversight.
    /// The spread between seeds is wide (2146 to 2292 mm on the reference job at one budget), so doubling
    /// the races looks like obvious free quality; it is not, because a wave larger than
    /// <see cref="WaveSize"/> is a second wave and costs a second wave's wall clock. Measured on that job at
    /// the Best budget: eight races returned a 2249 mm block in 106 s, sixteen returned the SAME 2249 mm in
    /// 210 s. What Best buys is iterations.
    /// </remarks>
    private static int RaceFloor(NestEffort effort) => effort == NestEffort.Fast ? 4 : 8;

    /// <summary>Best-of-K count for a batch that will pattern-replicate onto `replicas` identical sheets.
    /// A one-off sheet uses the base <see cref="NestTries"/>; a template governing many clones gets more
    /// tries (up to 16) because that cost is amortized over all the clones and drives the layout to a
    /// denser result — a search is one draw from a wide spread (measured: 30 to 40 parts on the same
    /// sheet across seeds), so racing more of them is what raises the floor.</summary>
    internal static int TriesForReplicas(int replicas, NestEffort effort = NestEffortDefaults.Default)
      => Math.Clamp(replicas, NestTries(effort), 16);

    /// <summary>Best-of-K for a sheet: scales with its clone count (<see cref="TriesForReplicas"/>), and the
    /// single final/tail sheet gets at least 8 tries so that leftover partial sheet lands consistently too
    /// (it doesn't amortize over clones, but it's only one sheet so the extra tries are cheap).</summary>
    internal static int TriesFor(int replicas, bool isFinalSheet, NestEffort effort = NestEffortDefaults.Default)
    {
      int t = TriesForReplicas(replicas, effort);
      return isFinalSheet ? Math.Max(t, RaceFloor(effort)) : t;
    }

    /// <summary>The bounding box the placed parts actually occupy. This is what decides the offcut the
    /// operator gets, and it is measured AFTER the gravity compaction — sparrow's own density is measured
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

    private static double CommonLineLength(
      IEnumerable<CommonEdgeIntent> intents,
      double commonLineGap,
      double geometryTolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs)
    {
      double length = 0D;
      foreach (CommonEdgeIntent intent in intents ?? Enumerable.Empty<CommonEdgeIntent>())
      {
        if (!TryCreateCommonEdgeRecord(intent, commonLineGap, geometryTolerance, out CommonEdge edge, sourceCatalogs))
        {
          continue;
        }

        CommonCenterCurve curve = edge.CenterCurve;
        if (curve?.GeometryKind == CommonLineGeometryKind.CircularArc)
        {
          length += Math.Abs(curve.SweepRadians) * curve.Radius;
        }
        else
        {
          double dx = edge.End.X - edge.Start.X;
          double dy = edge.End.Y - edge.Start.Y;
          length += Math.Sqrt((dx * dx) + (dy * dy));
        }
      }

      return length;
    }

    /// <summary>
    /// Picks the best sheet candidate: most parts placed wins, then the greatest valid common-line length,
    /// then the shortest extent along the sheet's long axis, higher strip density, and lower seed.
    /// <para>
    /// Extent beats density because they are measured at different moments: density is sparrow's, taken on
    /// its own strip BEFORE the gravity compaction, while the extent is what is left after it — and the
    /// extent is what the operator sees as the leftover. Ranking by density discarded real material: on a
    /// measured sheet where all eight candidates placed the same ten parts, the density winner (0.94448)
    /// left an extent of 118.72 while a candidate 0.00002 less dense left 115.79. Worse, two candidates
    /// separated in the fifth decimal of density trade places between runs, so the same job produced a
    /// different leftover every time (reported in issue #2).
    /// </para>
     /// Common-line length is compared only after all candidates place the same number of parts, so this
     /// preference cannot move demand to an extra sheet. Density stays as the next tie-break when the
     /// extents match. Pure — unit-testable. Returns the winner's index in
    /// <paramref name="cands"/>, or -1 if empty.
    /// </summary>
    internal static int PickBest(IReadOnlyList<(int Count, double SharedLength, double Extent, double Density, int Seed)> cands)
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

        if (c.SharedLength != b.SharedLength)
        {
          if (c.SharedLength > b.SharedLength)
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
    /// <paramref name="hardTimeoutSec"/> is only a watchdog for a hung process — if it ever fires the
    /// result is discarded rather than used, because a truncated search is not reproducible.</summary>
    private static string RunSparrowOnce(string exe, string workDir, string inputPath, int iterations, int workers, int hardTimeoutSec, int seed, CancellationToken cancel, Action<double>? onDensity, out string error)
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
      psi.ArgumentList.Add("-s"); // fixed RNG seed → repeatable per-seed run for best-of-N
      psi.ArgumentList.Add(seed.ToString(CultureInfo.InvariantCulture));
      psi.ArgumentList.Add("--workers");
      psi.ArgumentList.Add(workers.ToString(CultureInfo.InvariantCulture));

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
