namespace DeepNestSharp.RasterNest
{
  using System.Collections.Generic;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;

  /// <summary>Which end(s) of the sheet the reusable offcut is kept at.</summary>
  internal enum OffcutDirection
  {
    /// <summary>A full-short-dimension strip at the end of the LONG axis (the classic remnant).</summary>
    End,

    /// <summary>A full-long-dimension strip along the SHORT axis.</summary>
    Side,

    /// <summary>Both axes: the pack squeezes into a corner, leaving an L-shaped leftover cut as a
    /// full strip plus a partial one (guillotine style — no cut ever splits a remnant in two).</summary>
    Both,

    /// <summary>The engine tries End, Side and Both and keeps whichever leaves the largest usable
    /// remnant area. Kept LAST so the persisted int (SessionState) and combo index stay stable.</summary>
    Auto,
  }

  /// <summary>
  /// The size a remnant has to come out at to be worth cutting off at all, the way SheetCam's remnant
  /// cutoff states it: at least this big, and no bigger than this. ≤0 on any of the four = not set.
  /// <para>Past the maximum the leftover is not a remnant any more, it is very nearly a sheet, so nothing
  /// is cut and the sheet is kept whole.</para>
  /// </summary>
  internal readonly struct OffcutLimits
  {
    public OffcutLimits(double minX, double minY, double maxX, double maxY)
    {
      this.MinX = minX;
      this.MinY = minY;
      this.MaxX = maxX;
      this.MaxY = maxY;
    }

    public double MinX { get; }

    public double MinY { get; }

    public double MaxX { get; }

    public double MaxY { get; }
  }

  /// <summary>The user's "Prefer rectangular offcut" settings; null = feature off.</summary>
  internal sealed class OffcutOptions
  {
    /// <summary>
    /// What the size window and the edge overlap are worth starting from, IN THE DRAWING'S OWN UNITS.
    /// A foot square is the classic "this goes on the remnant rack" threshold and works the same on a 4x8
    /// sheet as on a 120x60 one; 5mm of overrun is what a shop puts on a cutoff.
    /// <para>The maximum is deliberately larger than any ordinary sheet. It is measured against the remnant
    /// RECTANGLE, and in End mode that rectangle spans the sheet's whole height - so a factory maximum of
    /// 60in would quietly stop cutting any remnant on a sheet taller than 60in, with nothing on screen to
    /// explain it. Anyone with a real rack limit lowers it to their own.</para>
    /// <para>The two gaps are the SAME PHYSICAL DISTANCE in both systems - 0.1969in is 5mm to a hundredth of
    /// a millimetre, and 5mm of clearance is 5mm whichever units the drawing happens to be in. The sizes are
    /// not: a foot and 300mm are each the round "this goes on the rack" figure of their own system. Do not
    /// tidy either into looking like the other; they are different kinds of number on purpose.</para>
    /// </summary>
    public static (OffcutLimits Limits, double Spacing, double EdgeOverlap) Defaults(bool unitsMm)
      => unitsMm
        ? (new OffcutLimits(300, 300, 6000, 6000), 5.0, 5.0)
        : (new OffcutLimits(12, 12, 240, 240), 0.1969, 0.1969);

    public OffcutDirection Direction { get; set; }

    /// <summary>Gap kept between the packed parts and the offcut cut line (drawing units).</summary>
    public double Spacing { get; set; }

    /// <summary>Narrowest remnant worth separating across the sheet's X axis (drawing units); ≤0 =
    /// automatic (5% of the sheet's side, the historical rule).</summary>
    public double MinX { get; set; } = -1;

    /// <summary>Narrowest remnant worth separating up the Y axis; ≤0 = automatic.</summary>
    public double MinY { get; set; } = -1;

    /// <summary>Widest remnant still worth cutting off; ≤0 = no limit.</summary>
    public double MaxX { get; set; } = -1;

    /// <summary>Tallest remnant still worth cutting off; ≤0 = no limit.</summary>
    public double MaxY { get; set; } = -1;

    /// <summary>How far the cut runs PAST the sheet edge at each end, so the remnant separates fully
    /// instead of hanging on by a bridge at the corners (drawing units).</summary>
    public double EdgeOverlap { get; set; }

    public OffcutLimits Limits => new OffcutLimits(this.MinX, this.MinY, this.MaxX, this.MaxY);
  }

  /// <summary>
  /// The single source of truth for WHERE the offcut cut lines sit on a placed sheet — shared by the
  /// viewer overlay and the DXF export so what the user sees is exactly what the laser cuts.
  /// Computed live from the placements (manual edits included).
  /// </summary>
  internal static class OffcutGeometry
  {
    /// <summary>A free strip smaller than this fraction of its axis is not worth separating.</summary>
    private const double MinStripFraction = 0.05;

    /// <summary>
    /// The cut positions on each axis, or null per axis when that direction is off or its free strip
    /// is too small. CutX splits the width at [CutX..w]; CutY splits the height at [CutY..h].
    /// The "End" axis is the sheet's LONG axis (the engine packs along it).
    /// </summary>
    public static (double? CutX, double? CutY) CutPositions(ISheetPlacement sheetPlacement, OffcutOptions options)
    {
      if (options == null || sheetPlacement?.Sheet == null || sheetPlacement.PartPlacements.Count == 0)
      {
        return (null, null);
      }

      double w = sheetPlacement.Sheet.WidthCalculated;
      double h = sheetPlacement.Sheet.HeightCalculated;

      double extentX = 0;
      double extentY = 0;
      foreach (var pp in sheetPlacement.PartPlacements)
      {
        extentX = System.Math.Max(extentX, pp.MaxX);
        extentY = System.Math.Max(extentY, pp.MaxY);
      }

      return CutPositionsCore(extentX, extentY, w, h, options.Spacing, options.Direction, options.Limits);
    }

    /// <summary>
    /// The pure math behind <see cref="CutPositions"/>, taking the extents directly so it can be tested
    /// without building an ISheetPlacement. Auto counts like Both: every axis is eligible and the strip
    /// filter decides what actually qualifies.
    /// </summary>
    public static (double? CutX, double? CutY) CutPositionsCore(double extentX, double extentY, double w, double h, double spacing, OffcutDirection direction, OffcutLimits limits = default)
    {
      bool growX = w >= h;
      bool bothAxes = direction == OffcutDirection.Both || direction == OffcutDirection.Auto;
      bool cutOnX = bothAxes || (direction == OffcutDirection.End) == growX;
      bool cutOnY = bothAxes || (direction == OffcutDirection.End) != growX;

      double spacingGap = System.Math.Max(0, spacing);
      double candX = System.Math.Min(extentX + spacingGap, w);
      double candY = System.Math.Min(extentY + spacingGap, h);

      // The usable remnant is what's LEFT of the cut line (extent + gap), so an explicit minimum
      // measures that actual remnant width. The automatic percentage rule keeps its historical
      // "free space" measure (unchanged when no minimum is set).
      bool xQualifies = cutOnX && (limits.MinX > 0
        ? w - candX >= limits.MinX
        : w - extentX >= MinStripFraction * w);
      bool yQualifies = cutOnY && (limits.MinY > 0
        ? h - candY >= limits.MinY
        : h - extentY >= MinStripFraction * h);

      // The two cuts are COUPLED - in an L the second remnant stops at the first (see RemnantRects) - so
      // a rectangle that falls outside the size window changes the other one's shape rather than just
      // disappearing. Weigh the combinations whole and keep the one that frees the most material, which is
      // how Auto has always chosen. With no window set the pair always wins, so this is the old answer.
      var combos = new List<(double? X, double? Y)>(3);
      if (xQualifies && yQualifies)
      {
        combos.Add((candX, candY));
      }

      if (xQualifies)
      {
        combos.Add((candX, null));
      }

      if (yQualifies)
      {
        combos.Add((null, candY));
      }

      (double? X, double? Y) best = (null, null);
      double bestArea = 0;
      foreach (var (cx, cy) in combos)
      {
        double area = 0;
        bool fits = true;
        foreach (var r in RemnantRects(cx, cy, w, h))
        {
          if (!FitsWindow(r, limits))
          {
            fits = false;
            break;
          }

          area += r.W * r.H;
        }

        if (fits && area > bestArea)
        {
          bestArea = area;
          best = (cx, cy);
        }
      }

      return best;
    }

    /// <summary>Whether one remnant rectangle comes out inside the size window the user asked for. An
    /// unset bound does not constrain, so with nothing set every rectangle fits.</summary>
    private static bool FitsWindow(RemnantRect r, OffcutLimits limits)
    {
      const double Tol = 1e-9;
      return (limits.MinX <= 0 || r.W >= limits.MinX - Tol)
        && (limits.MinY <= 0 || r.H >= limits.MinY - Tol)
        && (limits.MaxX <= 0 || r.W <= limits.MaxX + Tol)
        && (limits.MaxY <= 0 || r.H <= limits.MaxY + Tol);
    }

    /// <summary>The usable remnant area freed by the cut(s): the sum of the remnant rectangles. 0
    /// when no strip qualifies.</summary>
    public static double RemnantArea(double? cutX, double? cutY, double w, double h)
    {
      double area = 0;
      foreach (var r in RemnantRects(cutX, cutY, w, h))
      {
        area += r.W * r.H;
      }

      return area;
    }

    /// <summary>
    /// The 0-2 straight cut lines (sheet coordinates) that free the offcut(s): the separating edge
    /// of each remnant rectangle. Null when no strip qualifies (the export's "no offcut" signal).
    /// <para>Export asks on every run, so the feature being OFF has to be one of the answers: null
    /// options means exactly that (see <see cref="OffcutOptions"/>) and it arrives here whenever
    /// "Prefer rectangular offcut" is unticked, which is the default.</para>
    /// </summary>
    public static IReadOnlyList<OffcutLine> BuildLines(ISheetPlacement sheetPlacement, OffcutOptions options)
    {
      if (options == null || sheetPlacement?.Sheet == null)
      {
        return null;
      }

      var (cutX, cutY) = CutPositions(sheetPlacement, options);
      double w = sheetPlacement.Sheet.WidthCalculated;
      double h = sheetPlacement.Sheet.HeightCalculated;
      var rects = RemnantRects(cutX, cutY, w, h, options.EdgeOverlap);
      if (rects.Count == 0)
      {
        return null;
      }

      var lines = new List<OffcutLine>(rects.Count);
      foreach (var r in rects)
      {
        lines.Add(r.Cut);
      }

      return lines;
    }

    /// <summary>
    /// The 0-2 reusable remnant rectangles the cut(s) free (sheet coordinates, Y-up), each with the
    /// guillotine cut edge that separates it from the pack. The LONG axis's strip spans the full
    /// short dimension; the other stops at it — so no cut ever splits a remnant in two. This is the
    /// SINGLE source the cut lines, the remnant area and the viewer overlay all derive from, so the
    /// "what you see is what the laser cuts" promise can never drift between them.
    /// </summary>
    /// <param name="edgeOverlap">How far a cut runs PAST THE SHEET, so the remnant comes away instead of
    /// hanging on by a bridge at the corners. Only ends that sit on the sheet's boundary are extended: in an
    /// L one end of one cut dies against the other cut, and running past THAT would saw into the remnant the
    /// other cut frees. The two already pass through that corner, so the material parts anyway.</param>
    public static IReadOnlyList<RemnantRect> RemnantRects(double? cutX, double? cutY, double w, double h, double edgeOverlap = 0)
    {
      var rects = new List<RemnantRect>(2);
      bool growX = w >= h;
      double o = System.Math.Max(0, edgeOverlap);

      if (cutX.HasValue)
      {
        double top = growX ? h : cutY ?? h;
        double over = growX || !cutY.HasValue ? o : 0; // top is the sheet's edge, or the L's corner
        rects.Add(new RemnantRect(cutX.Value, 0, w - cutX.Value, top, new OffcutLine { X1 = cutX.Value, Y1 = -o, X2 = cutX.Value, Y2 = top + over }));
      }

      if (cutY.HasValue)
      {
        double right = growX ? cutX ?? w : w;
        double over = !growX || !cutX.HasValue ? o : 0; // same question on the other axis
        rects.Add(new RemnantRect(0, cutY.Value, right, h - cutY.Value, new OffcutLine { X1 = -o, Y1 = cutY.Value, X2 = right + over, Y2 = cutY.Value }));
      }

      return rects;
    }
  }

  /// <summary>A reusable remnant rectangle (sheet coordinates, Y-up) with the guillotine cut edge
  /// that frees it from the pack.</summary>
  internal readonly struct RemnantRect
  {
    public RemnantRect(double x, double y, double w, double h, OffcutLine cut)
    {
      this.X = x;
      this.Y = y;
      this.W = w;
      this.H = h;
      this.Cut = cut;
    }

    public double X { get; }

    public double Y { get; }

    public double W { get; }

    public double H { get; }

    public OffcutLine Cut { get; }
  }
}
