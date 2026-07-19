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

  /// <summary>The user's "Prefer rectangular offcut" settings; null = feature off.</summary>
  internal sealed class OffcutOptions
  {
    public OffcutDirection Direction { get; set; }

    /// <summary>Gap kept between the packed parts and the offcut cut line (drawing units).</summary>
    public double Spacing { get; set; }

    /// <summary>Narrowest strip worth separating (drawing units); ≤0 = automatic (5% of the
    /// sheet's side, the historical rule).</summary>
    public double MinStripWidth { get; set; } = -1;
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

      return CutPositionsCore(extentX, extentY, w, h, options.Spacing, options.Direction, options.MinStripWidth);
    }

    /// <summary>
    /// The pure math behind <see cref="CutPositions"/>, shared with the engine's Auto mode (which
    /// measures candidate layouts by their extents before any ISheetPlacement exists). Auto counts
    /// like Both: every axis is eligible and the strip filter decides what actually qualifies.
    /// </summary>
    public static (double? CutX, double? CutY) CutPositionsCore(double extentX, double extentY, double w, double h, double spacing, OffcutDirection direction, double minStripWidth = -1)
    {
      bool growX = w >= h;
      bool bothAxes = direction == OffcutDirection.Both || direction == OffcutDirection.Auto;
      bool cutOnX = bothAxes || (direction == OffcutDirection.End) == growX;
      bool cutOnY = bothAxes || (direction == OffcutDirection.End) != growX;

      double spacingGap = System.Math.Max(0, spacing);

      // The usable remnant is what's LEFT of the cut line (extent + gap), so an explicit minimum
      // measures that actual remnant width. The automatic percentage rule keeps its historical
      // "free space" measure (unchanged when no minimum is set).
      bool xQualifies = cutOnX && (minStripWidth > 0
        ? w - extentX - spacingGap >= minStripWidth
        : w - extentX >= MinStripFraction * w);
      bool yQualifies = cutOnY && (minStripWidth > 0
        ? h - extentY - spacingGap >= minStripWidth
        : h - extentY >= MinStripFraction * h);

      double? cutX = xQualifies ? System.Math.Min(extentX + spacingGap, w) : (double?)null;
      double? cutY = yQualifies ? System.Math.Min(extentY + spacingGap, h) : (double?)null;

      return (cutX, cutY);
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
    /// </summary>
    public static IReadOnlyList<OffcutLine> BuildLines(ISheetPlacement sheetPlacement, OffcutOptions options)
    {
      var (cutX, cutY) = CutPositions(sheetPlacement, options);
      double w = sheetPlacement.Sheet.WidthCalculated;
      double h = sheetPlacement.Sheet.HeightCalculated;
      var rects = RemnantRects(cutX, cutY, w, h);
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
    public static IReadOnlyList<RemnantRect> RemnantRects(double? cutX, double? cutY, double w, double h)
    {
      var rects = new List<RemnantRect>(2);
      bool growX = w >= h;

      if (cutX.HasValue)
      {
        double top = growX ? h : cutY ?? h;
        rects.Add(new RemnantRect(cutX.Value, 0, w - cutX.Value, top, new OffcutLine { X1 = cutX.Value, Y1 = 0, X2 = cutX.Value, Y2 = top }));
      }

      if (cutY.HasValue)
      {
        double right = growX ? cutX ?? w : w;
        rects.Add(new RemnantRect(0, cutY.Value, right, h - cutY.Value, new OffcutLine { X1 = 0, Y1 = cutY.Value, X2 = right, Y2 = cutY.Value }));
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
