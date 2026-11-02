namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using ClipperLib;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;

  /// <summary>Which way the scrap cuts run across the skeleton. Persisted as an int (SessionState), so the
  /// order is fixed.</summary>
  internal enum ScrapCutType
  {
    /// <summary>Vertical and horizontal lines: the skeleton comes away in squares.</summary>
    Both,

    /// <summary>Vertical lines only, one every X spacing.</summary>
    Vertical,

    /// <summary>Horizontal lines only, one every Y spacing.</summary>
    Horizontal,
  }

  /// <summary>The user's scrap cut settings; null = feature off.</summary>
  internal sealed class ScrapCutOptions
  {
    public ScrapCutType Type { get; set; }

    /// <summary>The distance the vertical lines are spread at, as near as an even split of the skeleton
    /// allows (drawing units); ≤0 = no vertical lines.</summary>
    public double SpacingX { get; set; }

    /// <summary>The same for the horizontal lines.</summary>
    public double SpacingY { get; set; }

    /// <summary>How close a scrap cut comes to a part before it stops (drawing units). The web left there is
    /// thin enough to snap by hand.</summary>
    public double PartGap { get; set; }

    /// <summary>How far a cut runs PAST the sheet edge, so the piece of skeleton comes away fully.</summary>
    public double EdgeOverlap { get; set; }

    /// <summary>
    /// What the four numbers are worth starting from, IN THE DRAWING'S OWN UNITS. The spacings are a piece of
    /// skeleton one person can lift: about a metre by three quarters, or 40 by 30 in. The edge overlap is the
    /// same 5 mm the offcut uses, and the part gap half a millimetre (0.02 in).
    /// </summary>
    public static (double SpacingX, double SpacingY, double PartGap, double EdgeOverlap) Defaults(bool unitsMm)
      => unitsMm
        ? (1000.0, 750.0, 0.5, 5.0)
        : (40.0, 30.0, 0.02, 0.1969);
  }

  /// <summary>
  /// WHERE the scrap cuts sit on a placed sheet: straight lines across the skeleton, broken wherever they
  /// would come closer than the part gap to a part. Shared by the viewer overlay and the DXF export, so what
  /// the user sees is what the laser cuts. Computed live from the placements (manual edits included).
  /// </summary>
  internal static class ScrapCutGeometry
  {
    /// <summary>Clipper integer units per drawing unit, as in <see cref="RasterCompact"/>.</summary>
    private const double Scale = 1e6;

    /// <summary>
    /// The scrap cut segments on one sheet, or null when the feature is off or nothing qualifies.
    /// <para>Pass <paramref name="offcut"/> only for the sheet whose offcut is cut: the remnant(s) it frees get
    /// no scrap cuts, and the lines stop at its cut line, which separates the material there already.</para>
    /// </summary>
    public static IReadOnlyList<OffcutLine> BuildLines(ISheetPlacement sheetPlacement, ScrapCutOptions options, OffcutOptions offcut = null)
    {
      if (options == null || sheetPlacement?.Sheet == null || sheetPlacement.PartPlacements.Count == 0)
      {
        return null;
      }

      double w = sheetPlacement.Sheet.WidthCalculated;
      double h = sheetPlacement.Sheet.HeightCalculated;
      var (cutX, cutY) = offcut != null ? OffcutGeometry.CutPositions(sheetPlacement, offcut) : (null, null);

      var outlines = new List<IReadOnlyList<(double X, double Y)>>();
      foreach (var pp in sheetPlacement.PartPlacements)
      {
        var placed = pp?.PlacedPart;
        if (placed?.Points == null || placed.Points.Length < 3)
        {
          continue;
        }

        var outline = new List<(double X, double Y)>(placed.Points.Length);
        foreach (var p in placed.Points)
        {
          outline.Add((p.X, p.Y));
        }

        outlines.Add(outline);
      }

      var lines = BuildLinesCore(outlines, w, h, cutX, cutY, options);
      return lines.Count == 0 ? null : lines;
    }

    /// <summary>
    /// The pure math behind <see cref="BuildLines"/>, taking the part outlines (sheet coordinates, Y-up) and
    /// the offcut cut positions directly so it can be tested without building an ISheetPlacement.
    /// <para>The skeleton is the rectangle [0, cutX ?? w] × [0, cutY ?? h]: whatever the offcut direction, the
    /// remnant(s) are what lies beyond those cuts (see <see cref="OffcutGeometry.RemnantRects"/>).</para>
    /// </summary>
    public static List<OffcutLine> BuildLinesCore(IEnumerable<IReadOnlyList<(double X, double Y)>> outlines, double w, double h, double? cutX, double? cutY, ScrapCutOptions options)
    {
      var lines = new List<OffcutLine>();
      double skeletonW = cutX ?? w;
      double skeletonH = cutY ?? h;
      double o = Math.Max(0, options.EdgeOverlap);

      // A line only runs past an end that is the sheet's own edge; at an offcut cut it stops on the cut.
      double bottom = -o;
      double top = skeletonH + (cutY.HasValue ? 0 : o);
      double left = -o;
      double right = skeletonW + (cutX.HasValue ? 0 : o);

      var obstacles = Obstacles(outlines, options.PartGap);

      if (options.Type != ScrapCutType.Horizontal)
      {
        foreach (double x in Positions(skeletonW, options.SpacingX))
        {
          lines.AddRange(Clip(obstacles, x, bottom, x, top));
        }
      }

      if (options.Type != ScrapCutType.Vertical)
      {
        foreach (double y in Positions(skeletonH, options.SpacingY))
        {
          lines.AddRange(Clip(obstacles, left, y, right, y));
        }
      }

      return lines;
    }

    /// <summary>
    /// Where the lines go along one side of the skeleton: an even split into the number of pieces nearest to
    /// the ideal spacing, so every piece comes out the same size. A side shorter than one and a half spacings
    /// stays whole.
    /// </summary>
    internal static IEnumerable<double> Positions(double length, double spacing)
    {
      if (spacing <= 0 || length <= 0)
      {
        yield break;
      }

      int pieces = (int)Math.Round(length / spacing, MidpointRounding.AwayFromZero);
      for (int i = 1; i < pieces; i++)
      {
        yield return length * i / pieces;
      }
    }

    /// <summary>
    /// Every part outline grown by the part gap, merged. The outline is the part as nested: an arc there is a
    /// chain of chords that sits up to <see cref="DxfParser.CurveChordTolerance"/> inside the drawn arc, so
    /// that much is added too, and the cut keeps the full gap from the arc the laser will follow. Only the
    /// outer contour counts: whatever sits inside a part (its holes, parts nested in them) is never crossed.
    /// </summary>
    private static List<List<IntPoint>> Obstacles(IEnumerable<IReadOnlyList<(double X, double Y)>> outlines, double partGap)
    {
      double grow = (Math.Max(0, partGap) + DxfParser.CurveChordTolerance) * Scale;
      double arcTolerance = grow * 0.0005; // relative, like RasterCompact.Inflate: the same accuracy in mm as in inches
      var offset = new ClipperOffset(2.0, arcTolerance);
      foreach (var outline in outlines)
      {
        var path = new List<IntPoint>(outline.Count);
        foreach (var (x, y) in outline)
        {
          path.Add(new IntPoint(Math.Round(x * Scale), Math.Round(y * Scale)));
        }

        if (!Clipper.Orientation(path))
        {
          path.Reverse(); // a positive offset only grows positively oriented paths
        }

        offset.AddPath(path, JoinType.jtRound, EndType.etClosedPolygon);
      }

      // A round join is drawn as chords between points ON the circle, which cut inside it by up to the arc
      // tolerance: grow by that much more so the chords keep the whole gap.
      var grown = new List<List<IntPoint>>();
      offset.Execute(ref grown, grow + arcTolerance);
      return grown;
    }

    /// <summary>The pieces of one straight line that stay clear of every obstacle.</summary>
    private static IEnumerable<OffcutLine> Clip(List<List<IntPoint>> obstacles, double x1, double y1, double x2, double y2)
    {
      var line = new List<IntPoint>
      {
        new IntPoint(Math.Round(x1 * Scale), Math.Round(y1 * Scale)),
        new IntPoint(Math.Round(x2 * Scale), Math.Round(y2 * Scale)),
      };

      var clipper = new Clipper();
      clipper.AddPath(line, PolyType.ptSubject, false);
      clipper.AddPaths(obstacles, PolyType.ptClip, true);
      var tree = new PolyTree();
      clipper.Execute(ClipType.ctDifference, tree, PolyFillType.pftNonZero, PolyFillType.pftNonZero);

      bool vertical = x1 == x2;
      var pieces = new List<OffcutLine>();
      foreach (var path in Clipper.OpenPathsFromPolyTree(tree))
      {
        if (path.Count < 2)
        {
          continue;
        }

        // Only the coordinate ALONG the line comes back from Clipper; the other one is the line's own.
        double a = (vertical ? path[0].Y : path[0].X) / Scale;
        double b = (vertical ? path[path.Count - 1].Y : path[path.Count - 1].X) / Scale;
        if (a > b)
        {
          (a, b) = (b, a);
        }

        if (b - a <= 2 / Scale)
        {
          continue;
        }

        pieces.Add(vertical
          ? new OffcutLine { X1 = x1, Y1 = a, X2 = x1, Y2 = b }
          : new OffcutLine { X1 = a, Y1 = y1, X2 = b, Y2 = y1 });
      }

      pieces.Sort((p, q) => vertical ? p.Y1.CompareTo(q.Y1) : p.X1.CompareTo(q.X1));
      return pieces;
    }
  }
}
