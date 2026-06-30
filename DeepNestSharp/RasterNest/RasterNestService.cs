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
  internal static class RasterNestService
  {
    public static INestResult Nest(
      IReadOnlyList<(string Path, int Quantity)> partInfos,
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
      foreach (var (path, quantity) in partInfos)
      {
        if (string.IsNullOrWhiteSpace(path))
        {
          continue;
        }

        var det = helper.LoadRawDetail(new FileInfo(path));
        if (det != null && det.TryConvertToNfp(src, out INfp nfp) && nfp.Points.Length > 2)
        {
          types.Add(new PartType { Source = src, Poly = nfp, Quantity = quantity, RotationsDeg = RotationsFor(rotations) });
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
      int haloPx = (int)System.Math.Round((spacing / 2.0) * pxPerInch);
      int marginPx = (int)System.Math.Round(margin * pxPerInch);

      var job = RasterJobNester.Nest(types, sw, sh, pxPerInch, haloPx, marginPx);

      var collection = new SheetPlacementCollection();
      int id = 0;
      foreach (var sheetGroup in job.Placements.GroupBy(p => p.Sheet).OrderBy(g => g.Key))
      {
        var sheet = Sheet.NewSheet(sheetGroup.Key + 1, sheetWin, sheetHin);
        var placements = new List<IPartPlacement>();
        foreach (var jp in sheetGroup)
        {
          var poly = types.First(t => t.Source == jp.Source).Poly;
          var rotated = jp.RotationDeg == 0 ? poly : poly.Rotate(jp.RotationDeg);
          placements.Add(new PartPlacement(rotated)
          {
            X = (jp.Xpx / pxPerInch) - rotated.MinX,
            Y = (jp.Ypx / pxPerInch) - rotated.MinY,
            Rotation = jp.RotationDeg,
            Source = jp.Source,
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

      int totalParts = types.Sum(t => t.Quantity);
      return new NestResult(totalParts, collection, new List<INfp>(), placementType, 0, 0);
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
