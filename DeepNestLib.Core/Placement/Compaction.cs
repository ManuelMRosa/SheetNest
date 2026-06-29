namespace DeepNestLib.Placement
{
  using System.Collections.Generic;
  using System.Linq;

  /// <summary>
  /// Post-placement compaction. DeepNest's placement is greedy and order-dependent: once a
  /// part is placed it is never revisited, so gaps left by earlier parts are never closed.
  /// This pass slides each placed part toward the sheet origin (down, then left) as far as it
  /// can travel without overlapping another part or leaving the sheet, repeating until stable.
  /// It reuses the already-offset part geometry, so the configured spacing is preserved.
  /// </summary>
  public static class Compaction
  {
    /// <summary>Gets or sets a value indicating whether compaction runs. Default off => engine behaviour unchanged.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Gets or sets the maximum number of sweeps over all parts.</summary>
    public static int MaxPasses { get; set; } = 6;

    /// <summary>Diagnostics: number of part-moves in the most recent compaction.</summary>
    public static int LastMovedCount { get; private set; }

    /// <summary>Diagnostics: summed slide distance in the most recent compaction.</summary>
    public static double LastTotalSlide { get; private set; }

    private const double Tolerance = 1e-3;
    private const int BisectionSteps = 22;

    /// <summary>
    /// Compacts the given placements in place (mutates each part's X/Y).
    /// </summary>
    /// <param name="placements">The parts already placed on the sheet.</param>
    /// <param name="sheet">The sheet they sit on (used for the origin/floor).</param>
    public static void Compact(List<IPartPlacement> placements, ISheet sheet)
    {
      if (placements == null || placements.Count < 2)
      {
        return;
      }

      // Sheet floor in the same coordinate frame as the placement offsets.
      double sheetMinX = sheet.Points.Min(pt => pt.X);
      double sheetMinY = sheet.Points.Min(pt => pt.Y);

      LastMovedCount = 0;
      LastTotalSlide = 0;

      // Cache each part's shifted polygon; refreshed whenever that part moves.
      var placed = placements.ToDictionary(p => p, p => p.PlacedPart);

      // Bottom-left compaction: slide down, then left, and repeat.
      var directions = new (double Dx, double Dy)[] { (0, -1), (-1, 0) };

      for (int pass = 0; pass < MaxPasses; pass++)
      {
        bool moved = false;
        foreach (var (dx, dy) in directions)
        {
          // Settle the parts nearest the target edge first.
          var ordered = dy < 0
            ? placements.OrderBy(p => p.MinY).ToList()
            : placements.OrderBy(p => p.MinX).ToList();

          foreach (var p in ordered)
          {
            double limit = dy < 0 ? p.MinY - sheetMinY : p.MinX - sheetMinX;
            double slide = MaxSlide(p, dx, dy, limit, placements, placed);
            if (slide > Tolerance)
            {
              p.X += dx * slide;
              p.Y += dy * slide;
              placed[p] = p.PlacedPart;
              moved = true;
              LastMovedCount++;
              LastTotalSlide += slide;
            }
          }
        }

        if (!moved)
        {
          break;
        }
      }
    }

    /// <summary>
    /// Largest distance in [0, limit] that <paramref name="p"/> can move along (dx,dy)
    /// without overlapping another placement.
    /// </summary>
    private static double MaxSlide(IPartPlacement p, double dx, double dy, double limit, List<IPartPlacement> all, Dictionary<IPartPlacement, INfp> placed)
    {
      if (limit <= Tolerance)
      {
        return 0;
      }

      if (IsFeasible(p, dx, dy, limit, all, placed))
      {
        return limit;
      }

      double lo = 0;
      double hi = limit;
      for (int i = 0; i < BisectionSteps; i++)
      {
        double mid = 0.5 * (lo + hi);
        if (IsFeasible(p, dx, dy, mid, all, placed))
        {
          lo = mid;
        }
        else
        {
          hi = mid;
        }
      }

      return lo;
    }

    private static bool IsFeasible(IPartPlacement p, double dx, double dy, double t, List<IPartPlacement> all, Dictionary<IPartPlacement, INfp> placed)
    {
      double offX = dx * t;
      double offY = dy * t;

      // Trial bounding box (the current box shifted by the trial offset).
      double tMinX = p.MinX + offX;
      double tMaxX = p.MaxX + offX;
      double tMinY = p.MinY + offY;
      double tMaxY = p.MaxY + offY;

      INfp trial = null;
      foreach (var q in all)
      {
        if (ReferenceEquals(q, p))
        {
          continue;
        }

        // Broad phase: bounding boxes that merely touch cannot overlap.
        if (q.MaxX <= tMinX || q.MinX >= tMaxX || q.MaxY <= tMinY || q.MinY >= tMaxY)
        {
          continue;
        }

        // Narrow phase: only build the shifted polygon once, lazily.
        trial ??= p.Part.Shift(p.X + offX, p.Y + offY);
        if (trial.Overlaps(placed[q]))
        {
          return false;
        }
      }

      return true;
    }
  }
}
