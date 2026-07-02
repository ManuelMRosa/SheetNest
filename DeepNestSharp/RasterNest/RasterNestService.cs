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
      PlacementTypeEnum placementType,
      int rotations,
      double spacing,
      double margin,
      double pxPerInch,
      out string error)
    {
      error = null;
      var helper = new NestExecutionHelper();

      var parsed = new List<(int Src, INfp Nfp, int Qty, int Allowed, int Priority, double SpacingIn)>();
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
          parsed.Add((src, nfp, part.Quantity, part.Rotations > 0 ? part.Rotations : rotations, part.Priority, effSpacing));
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

      int sw = (int)(sheetWin * pxPerInch);
      int sh = (int)(sheetHin * pxPerInch);

      // PER-PART spacing → each part's mask dilates by its own spacing/2, so two parts end up
      // (spacingA + spacingB)/2 apart; common-line parts (spacing 0) pack touching. No minimum halo:
      // Rasterize is CONSERVATIVE (the mask always fully covers the true geometry), so non-colliding
      // masks can never overlap real material even when touching. Clamps keep an over-large value
      // (units mistake) from blowing the mask size up to gigabytes.
      int marginPx = (int)System.Math.Round(margin * pxPerInch);
      int halfSheet = System.Math.Max(1, System.Math.Min(sw, sh) / 2);
      marginPx = System.Math.Max(0, System.Math.Min(marginPx, halfSheet));
      var halos = parsed
        .Select(p => System.Math.Max(0, System.Math.Min((int)System.Math.Round((p.SpacingIn / 2.0) * pxPerInch), halfSheet)))
        .ToArray();

      // Straighten angle per part (min-area bounding box) — used by the straightened profiles. Only for
      // parts allowed 90°-step rotation or freer; parts restricted to 0/180 (grain) or fixed keep their
      // drawn orientation.
      var straighten = parsed.Select(p => p.Allowed >= 4 ? MinBoundingBoxAngle(p.Nfp) : 0).ToArray();

      var candidates = BuildCandidates(parsed.Select(p => p.Allowed).ToArray(), straighten);

      // Nest every candidate profile in parallel (each is an independent, single-threaded packing).
      var results = new (JobResult Job, List<PartType> Types)[candidates.Count];
      System.Threading.Tasks.Parallel.For(0, candidates.Count, ci =>
      {
        var candidateTypes = parsed
          .Select((p, i) => new PartType
          {
            Source = p.Src,
            Poly = p.Nfp,
            Quantity = p.Qty,
            Priority = p.Priority,
            RotationsDeg = candidates[ci][i],
            HaloPx = halos[i],
          })
          .ToList();
        results[ci] = (RasterJobNester.Nest(candidateTypes, sw, sh, pxPerInch, marginPx), candidateTypes);
      });

      bool growX = sw > sh; // pack grows along the longer sheet axis (keeps the remnant a short-dim strip)
      int best = 0;
      for (int ci = 1; ci < results.Length; ci++)
      {
        if (IsBetter(results[ci], results[best], growX))
        {
          best = ci;
        }
      }

      var job = results[best].Job;
      var types = results[best].Types;

      // Industrial pattern replication: repeat sheet 0's layout as many whole times as the demand
      // allows and RE-NEST the leftover parts on their own sheet(s). The shop then cuts "k× the same
      // layout + 1 remainder" — and the remainder is a real dense nest, not a display trick. Only kept
      // when it needs no more sheets than the greedy result.
      job = TryReplicatePattern(job, types, sw, sh, pxPerInch, marginPx);

      var collection = new SheetPlacementCollection();
      int id = 0;
      foreach (var sheetGroup in job.Placements.GroupBy(p => p.Sheet).OrderBy(g => g.Key))
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
            X = (jp.Xpx / pxPerInch) - rotated.MinX,
            Y = (jp.Ypx / pxPerInch) - rotated.MinY,
            Spacing = entry.SpacingIn,
          });
        }

        // Common-line parts (spacing 0) must TOUCH, but the raster grid keeps them 1-2 pixels apart
        // (its masks are conservative and positions are pixel-quantized). Close that last gap with an
        // exact-geometry compaction pass: only the spacing-0 parts slide, and spaced neighbours are
        // respected at their own half-spacing (parts end up in true contact, 0.001" safety gap).
        if (items.Any(it => it.Spacing <= 0))
        {
          RasterCompact.Compact(items, sheetWin, sheetHin, System.Math.Max(0, margin));
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

      // Surface any parts that could not be placed (too big for the sheet in every rotation) so the
      // results grid's Unplaced count is honest — don't report 100% placed while silently losing parts.
      var placedPerSource = job.Placements.GroupBy(p => p.Source).ToDictionary(g => g.Key, g => g.Count());
      var unplaced = new List<INfp>();
      foreach (var t in types)
      {
        placedPerSource.TryGetValue(t.Source, out int placedCount);
        for (int i = placedCount; i < t.Quantity; i++)
        {
          unplaced.Add(t.Poly);
        }
      }

      int totalParts = types.Sum(t => t.Quantity);
      return new NestResult(totalParts, collection, unplaced, placementType, 0, 0);
    }

    /// <summary>
    /// Turns a greedy multi-sheet result into "k identical pattern sheets + a freshly nested
    /// remainder" when that costs no extra sheets. Identical sheets group naturally in the production
    /// plan, and the remainder is a REAL nest (parts packed into the corner) instead of a sparse
    /// subset of the master layout.
    /// </summary>
    private static JobResult TryReplicatePattern(JobResult greedy, List<PartType> types, int sw, int sh, double pxPerInch, int marginPx)
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

      // Leftover demand after k pattern sheets, nested fresh (fast — it is a fraction of the job).
      var leftoverTypes = new List<PartType>();
      foreach (var t in types)
      {
        comp.TryGetValue(t.Source, out int used);
        int rest = t.Quantity - (k * used);
        if (rest > 0)
        {
          leftoverTypes.Add(new PartType
          {
            Source = t.Source,
            Poly = t.Poly,
            Quantity = rest,
            RotationsDeg = t.RotationsDeg,
            Priority = t.Priority,
            HaloPx = t.HaloPx,
          });
        }
      }

      JobResult tail = null;
      if (leftoverTypes.Count > 0)
      {
        tail = RasterJobNester.Nest(leftoverTypes, sw, sh, pxPerInch, marginPx);
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
        Sheets = totalSheets,
        NotPlaced = 0,
        NoOverlap = greedy.NoOverlap && (tail?.NoOverlap ?? true),
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

      return LastSheetExtentPx(a, growX) < LastSheetExtentPx(b, growX) - 0.5;
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
    /// Candidate rotation profiles for the whole job. Each entry is one rotation set PER PART, always a
    /// subset of what that part's permitted rotations allow (a fixed/grain part keeps its own set in
    /// every profile). Duplicates collapse, so a job of fixed/flip-only parts runs exactly once.
    /// </summary>
    private static List<int[][]> BuildCandidates(int[] allowed, int[] straighten)
    {
      int n = allowed.Length;
      var candidates = new List<int[][]>();
      var seen = new HashSet<string>();

      int[] Base(int a) => a <= 1 ? new[] { 0 } : a == 2 ? new[] { 0, 180 } : new[] { 0, 90, 180, 270 };
      int[] Flip(int a) => a >= 4 ? new[] { 0, 180 } : Base(a);
      int[] FlipOffset(int a) => a >= 4 ? new[] { 90, 270 } : Base(a);
      int[] Eight(int a) => a >= 8 ? AnglesN(8) : Base(a);

      void Add(System.Func<int, int[]> setFor, bool applyStraighten)
      {
        var cand = new int[n][];
        for (int i = 0; i < n; i++)
        {
          int off = applyStraighten && allowed[i] >= 4 ? straighten[i] : 0;
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
