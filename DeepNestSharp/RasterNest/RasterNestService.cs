namespace DeepNestSharp.RasterNest
{
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.Placement;

  /// <summary>
  /// Experimental raster nesting wired into the app: nest the given parts + sheet with the raster
  /// engine and build a standard <see cref="INestResult"/> so it shows in the existing viewer and
  /// production plan. Takes plain data (not UI-bound objects) so it can run on a background thread.
  /// The NFP engine is untouched. (GPU collision swaps into RasterJobNester next.)
  /// </summary>
  /// <summary>Plain per-part nesting data handed to the raster engine (background-thread safe).</summary>
  internal sealed class RasterPartInfo
  {
    public string Path;
    public int Quantity;       // total to nest (required + extra)
    public int Rotations = -1; // per-part allowed rotations; -1 = use the job's global setting
    public int Priority = 5;   // 0-10, higher nests first
  }

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

      var types = new List<PartType>();
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
          types.Add(new PartType
          {
            Source = src,
            Poly = nfp,
            Quantity = part.Quantity,
            RotationsDeg = RotationsFor(part.Rotations > 0 ? part.Rotations : rotations),
            Priority = part.Priority,
          });
          src++;
        }
      }

      if (types.Count == 0)
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

      // Part spacing → dilate each part by spacing/2 px; sheet edge margin → keep parts that far from edges.
      // Clamp both to the sheet so an over-large Spacing/margin (e.g. a units mistake) can't blow the
      // dilated-mask size up to gigabytes / hang the nest (Dilate is O(area·(2r+1)²)).
      int haloPx = (int)System.Math.Round((spacing / 2.0) * pxPerInch);
      int marginPx = (int)System.Math.Round(margin * pxPerInch);
      int halfSheet = System.Math.Max(1, System.Math.Min(sw, sh) / 2);

      // No minimum halo: Rasterize is CONSERVATIVE (it stamps every pixel the contour passes through, so
      // the mask always fully covers the true geometry) — two non-colliding masks can therefore never
      // overlap in real material, even at spacing 0, where parts are allowed to pack touching. The
      // effective spacing floor is the pixel size (1/pxPerInch); the laser still needs its own kerf gap,
      // which is the user's spacing setting to make.
      haloPx = System.Math.Max(0, System.Math.Min(haloPx, halfSheet));
      marginPx = System.Math.Max(0, System.Math.Min(marginPx, halfSheet));

      var job = RasterJobNester.Nest(types, sw, sh, pxPerInch, haloPx, marginPx);

      var collection = new SheetPlacementCollection();
      int id = 0;
      foreach (var sheetGroup in job.Placements.GroupBy(p => p.Sheet).OrderBy(g => g.Key))
      {
        var sheet = Sheet.NewSheet(sheetGroup.Key + 1, sheetWin, sheetHin);

        var jps = sheetGroup.ToList();
        var items = new List<CompactItem>(jps.Count);
        foreach (var jp in jps)
        {
          var poly = types.First(t => t.Source == jp.Source).Poly;
          var rotated = jp.RotationDeg == 0 ? poly : poly.Rotate(jp.RotationDeg);
          items.Add(new CompactItem
          {
            Poly = rotated,
            X = (jp.Xpx / pxPerInch) - rotated.MinX,
            Y = (jp.Ypx / pxPerInch) - rotated.MinY,
          });
        }

        // At spacing 0 the user wants parts TOUCHING, but the raster grid keeps interlocking parts 1-2
        // pixels apart (its masks are conservative and positions are pixel-quantized). Close that last
        // gap with an exact-geometry compaction pass — parts end up in true contact (0.001" safety gap).
        if (haloPx == 0)
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

    private static int[] RotationsFor(int rotations)
    {
      int r = rotations;
      if (r <= 1)
      {
        return new[] { 0 };
      }

      if (r == 2)
      {
        return new[] { 0, 180 };
      }

      return new[] { 0, 90, 180, 270 };
    }
  }
}
