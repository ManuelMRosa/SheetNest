namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Generic;
  using System.Linq;

  /// <summary>
  /// Hybrid grid pre-placement. DeepNest's NFP placement positions identical rectangular parts one
  /// at a time and rarely aligns them, leaving gaps a grid would not. This pass, run on an empty
  /// sheet before the NFP loop, finds groups of identical rectangular parts and tiles them in the
  /// grid (rows x cols and orientation) that minimises the bounding box, at a kerf gap (common-line
  /// built in). The NFP loop then fills irregular parts around the grid block. The grid alone lifts
  /// rectangular clusters from ~75% toward ~99% — the technique commercial nesters use for blanks.
  /// </summary>
  public static class GridNesting
  {
    /// <summary>Gets or sets a value indicating whether grid pre-placement runs. Default off => engine behaviour unchanged.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Gets or sets the shared-cut width between gridded parts (common-line), in part units.</summary>
    public static double Kerf { get; set; } = 0.2;

    /// <summary>Gets or sets the part spacing baked into the geometry (must match the nest's Spacing).</summary>
    public static double Spacing { get; set; } = 0;

    /// <summary>Gets or sets the smallest group of identical rectangles worth gridding.</summary>
    public static int MinGroupSize { get; set; } = 4;

    /// <summary>Diagnostics: parts grid-placed in the most recent call.</summary>
    public static int LastGridCount { get; private set; }

    private const double RectFillTol = 0.02;
    private const double Eps = 1e-6;

    /// <summary>
    /// Grid-places identical rectangular groups from <paramref name="unplaced"/> onto an empty
    /// <paramref name="sheet"/>, moving them into <paramref name="placements"/>.
    /// </summary>
    public static void PrePlace(List<INfp> unplaced, List<IPartPlacement> placements, ISheet sheet)
    {
      LastGridCount = 0;
      if (placements.Count > 0 || unplaced.Count == 0)
      {
        return;
      }

      double spacing = Spacing;
      double sMinX = sheet.Points.Min(p => p.X);
      double sMinY = sheet.Points.Min(p => p.Y);
      double sMaxX = sheet.Points.Max(p => p.X);
      double sMaxY = sheet.Points.Max(p => p.Y);
      double availW = sMaxX - sMinX;

      var groups = unplaced
        .Where(IsRectangular)
        .GroupBy(p => p.Source)
        .Where(g => g.Count() >= MinGroupSize)
        .OrderByDescending(g => g.Count())
        .ToList();

      double bandBottom = sMinY;
      foreach (var group in groups)
      {
        var members = group.ToList();
        double availH = sMaxY - bandBottom;
        if (availH <= Eps)
        {
          break;
        }

        var plan = BestPlan(members[0], availW, availH, members.Count, spacing);
        if (plan == null)
        {
          continue;
        }

        double originX = plan.Geom.Points.Min(z => z.X);
        double originY = plan.Geom.Points.Min(z => z.Y);
        int idx = 0;
        double y = bandBottom;
        double lastRowY = y;
        while (idx < plan.Place)
        {
          lastRowY = y;
          for (int c = 0; c < plan.Cols && idx < plan.Place; c++)
          {
            var m = members[idx];
            double px = sMinX + (c * plan.PitchX);
            placements.Add(new PartPlacement(plan.Geom)
            {
              X = px - originX,
              Y = y - originY,
              Id = m.Id,
              Source = m.Source,
              Rotation = plan.Geom.Rotation,
            });
            unplaced.Remove(m);
            idx++;
            LastGridCount++;
          }

          y += plan.PitchY;
        }

        // Next part type starts where this band's inflated top sits => full spacing between types.
        bandBottom = lastRowY + plan.PartH;
      }
    }

    /// <summary>Best grid plan over both orientations (smallest bounding box for the parts placed).</summary>
    private static GridPlan BestPlan(INfp sample, double availW, double availH, int count, double spacing)
    {
      var a = EvalOrientation(sample, availW, availH, count, spacing);
      var b = EvalOrientation(sample.Rotate(90), availW, availH, count, spacing);
      if (a == null)
      {
        return b;
      }

      if (b == null)
      {
        return a;
      }

      return b.BboxArea < a.BboxArea ? b : a;
    }

    /// <summary>For one orientation, the column count that minimises the bounding box for as many parts as fit.</summary>
    private static GridPlan EvalOrientation(INfp geom, double availW, double availH, int count, double spacing)
    {
      double w = geom.WidthCalculated;
      double h = geom.HeightCalculated;
      double pitchX = (w - spacing) + Kerf;
      double pitchY = (h - spacing) + Kerf;
      if (pitchX <= Eps || pitchY <= Eps || w > availW + Eps || h > availH + Eps)
      {
        return null;
      }

      int maxCols = (int)Math.Floor((availW - w) / pitchX) + 1;
      int maxRows = (int)Math.Floor((availH - h) / pitchY) + 1;
      if (maxCols < 1 || maxRows < 1)
      {
        return null;
      }

      int place = Math.Min(count, maxCols * maxRows);
      int bestCols = maxCols;
      double bestArea = double.MaxValue;
      for (int cols = 1; cols <= maxCols; cols++)
      {
        int rows = (place + cols - 1) / cols;
        if (rows > maxRows)
        {
          continue;
        }

        double bw = ((cols - 1) * pitchX) + w;
        double bh = ((rows - 1) * pitchY) + h;
        double area = bw * bh;
        if (area < bestArea)
        {
          bestArea = area;
          bestCols = cols;
        }
      }

      return new GridPlan
      {
        Geom = geom,
        Cols = bestCols,
        PitchX = pitchX,
        PitchY = pitchY,
        PartH = h,
        Place = place,
        BboxArea = bestArea,
      };
    }

    /// <summary>True if the part's outer contour fills its bounding box (it is a rectangle).</summary>
    private static bool IsRectangular(INfp p)
    {
      double box = p.WidthCalculated * p.HeightCalculated;
      if (box <= Eps)
      {
        return false;
      }

      return Math.Abs(Math.Abs(p.Area) - box) / box < RectFillTol;
    }

    private sealed class GridPlan
    {
      public INfp Geom { get; set; }

      public int Cols { get; set; }

      public double PitchX { get; set; }

      public double PitchY { get; set; }

      public double PartH { get; set; }

      public int Place { get; set; }

      public double BboxArea { get; set; }
    }
  }
}
