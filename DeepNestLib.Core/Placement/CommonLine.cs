namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Generic;
  using System.Linq;

  /// <summary>
  /// Common-line cutting. When two parts present parallel straight edges to each other, a single
  /// cut can serve both, so they only need to be a kerf apart instead of the full part spacing.
  /// This pass detects rectangular-outer parts that face each other along an axis-aligned edge and
  /// pulls them together to a kerf gap (a shared cut), while keeping the full spacing to every
  /// other part. The saving is structural — it recovers (spacing - kerf) on each shared edge —
  /// and is the technique that lifts yield past what pure packing can reach.
  ///
  /// Geometry note: parts arrive inflated by spacing/2 (the offset phase), so their bounding boxes
  /// already encode the spacing. Two rectangular parts at a kerf gap means their inflated boxes
  /// overlap by exactly (spacing - kerf) along the shared edge, which this pass allows only between
  /// rectangular-outer parts that share that edge.
  /// </summary>
  public static class CommonLine
  {
    /// <summary>Gets or sets a value indicating whether common-line runs. Default off => engine behaviour unchanged.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Gets or sets the shared-cut width (material lost to one cut), in part units.</summary>
    public static double Kerf { get; set; } = 0.2;

    /// <summary>Gets or sets the part spacing baked into the geometry (must match the nest's Spacing).</summary>
    public static double Spacing { get; set; } = 0;

    /// <summary>Gets or sets the maximum number of sweeps.</summary>
    public static int MaxPasses { get; set; } = 8;

    /// <summary>Diagnostics: fractional bounding-box area reduction in the most recent run.</summary>
    public static double LastImprovement { get; private set; }

    private const double Tol = 1e-3;
    private const double RectFillTol = 0.02;

    public static void Apply(List<IPartPlacement> placements, ISheet sheet)
    {
      if (placements == null || placements.Count < 2)
      {
        return;
      }

      LastImprovement = 0;
      double spacing = Spacing;
      double startArea = BoundingArea(placements);
      double sheetMinX = sheet.Points.Min(p => p.X);
      double sheetMinY = sheet.Points.Min(p => p.Y);

      var isRect = placements.ToDictionary(p => p, p => IsRectangular(p));

      var directions = new[] { (-1.0, 0.0), (0.0, -1.0) };
      for (int pass = 0; pass < MaxPasses; pass++)
      {
        bool moved = false;
        foreach (var (dx, dy) in directions)
        {
          var ordered = dx < 0
            ? placements.OrderBy(p => p.MinX).ToList()
            : placements.OrderBy(p => p.MinY).ToList();

          foreach (var p in ordered)
          {
            double slide = MaxSlide(p, dx, dy, placements, isRect, spacing, sheetMinX, sheetMinY);
            if (slide > Tol)
            {
              p.X += dx * slide;
              p.Y += dy * slide;
              moved = true;
            }
          }
        }

        if (!moved)
        {
          break;
        }
      }

      double endArea = BoundingArea(placements);
      LastImprovement = startArea > Tol ? (startArea - endArea) / startArea : 0;
    }

    /// <summary>
    /// Largest distance part <paramref name="p"/> can move along (dx,dy): it must keep the full
    /// spacing to every blocking part, except a rectangular neighbour sharing the edge, to which it
    /// may close to the kerf.
    /// </summary>
    private static double MaxSlide(IPartPlacement p, double dx, double dy, List<IPartPlacement> all, Dictionary<IPartPlacement, bool> isRect, double spacing, double sheetMinX, double sheetMinY)
    {
      double limit = dx < 0 ? p.MinX - sheetMinX : p.MinY - sheetMinY;
      if (limit <= Tol)
      {
        return 0;
      }

      foreach (var q in all)
      {
        if (ReferenceEquals(q, p))
        {
          continue;
        }

        double lim;
        if (dx < 0)
        {
          // "To the left" by centre, so a part we've already closed against (and now overlap
          // by the shared cut) keeps constraining us instead of being skipped.
          if (q.MinX + q.MaxX >= p.MinX + p.MaxX)
          {
            continue;
          }

          if (q.MaxY <= p.MinY || q.MinY >= p.MaxY)
          {
            continue; // edges don't face each other in the perpendicular axis
          }

          double gap = (isRect[p] && isRect[q]) ? Kerf : spacing;
          lim = (p.MinX - q.MaxX) + spacing - gap;
        }
        else
        {
          if (q.MinY + q.MaxY >= p.MinY + p.MaxY)
          {
            continue;
          }

          if (q.MaxX <= p.MinX || q.MinX >= p.MaxX)
          {
            continue;
          }

          double gap = (isRect[p] && isRect[q]) ? Kerf : spacing;
          lim = (p.MinY - q.MaxY) + spacing - gap;
        }

        if (lim < limit)
        {
          limit = lim;
        }
      }

      return Math.Max(0, limit);
    }

    /// <summary>True if the part's outer contour fills its bounding box (i.e. it is a rectangle).</summary>
    private static bool IsRectangular(IPartPlacement p)
    {
      double w = p.MaxX - p.MinX;
      double h = p.MaxY - p.MinY;
      double box = w * h;
      if (box <= Tol)
      {
        return false;
      }

      return Math.Abs(Math.Abs(p.Part.Area) - box) / box < RectFillTol;
    }

    private static double BoundingArea(List<IPartPlacement> placements)
    {
      double minX = double.MaxValue;
      double minY = double.MaxValue;
      double maxX = double.MinValue;
      double maxY = double.MinValue;
      foreach (var p in placements)
      {
        if (p.MinX < minX)
        {
          minX = p.MinX;
        }

        if (p.MinY < minY)
        {
          minY = p.MinY;
        }

        if (p.MaxX > maxX)
        {
          maxX = p.MaxX;
        }

        if (p.MaxY > maxY)
        {
          maxY = p.MaxY;
        }
      }

      return (maxX - minX) * (maxY - minY);
    }
  }
}
