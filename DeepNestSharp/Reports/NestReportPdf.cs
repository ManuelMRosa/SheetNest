namespace DeepNestSharp.Reports
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text;
  using System.Text.RegularExpressions;
  using DeepNestLib;
  using DeepNestLib.Placement;

  /// <summary>
  /// Writes the production nest report as a PDF: one nest sheet per DISTINCT layout, the paper the
  /// operator takes to the laser. Each page carries the layout's settings top left, the parts on it
  /// below them and the sheet drawn to scale on the right, every part numbered to its row.
  /// Self-contained minimal PDF writer (vector lines + Courier text is all a report needs): no external
  /// PDF library, nothing to license.
  /// </summary>
  public static class NestReportPdf
  {
    internal const string SettingHeading = "SETTING INFORMATION";
    internal const string PartsHeading = "NEST PARTS LIST";

    /// <summary>The report page, in points: US Letter landscape.</summary>
    internal const double PageW = 792;

    /// <summary>The report page's height in points; see <see cref="PageW"/>.</summary>
    internal const double PageH = 612;

    // The sheet drawing owns the right of the page, bottom margin to top margin. The text column ends
    // before it: a parts row is 4 + 1 + 40 + 1 + 7 = 53 characters, 318 points from the left margin.
    private const double BoxX = 384;
    private const double BoxW = 372;
    private const double BoxH = 528;
    private const int LabelWidth = 17; // "SHEET UTILIZATION", the longest label
    private const int NameWidth = 40;

    /// <summary>The parts list's column heading, padded to line up with <see cref="PartRows"/>.</summary>
    internal static readonly string PartListHeader = $"{"Item",4} {"Part Name",-NameWidth} {"Num Off",7}";

    /// <summary>
    /// The four things a report page is drawn with, written into the page's content stream by
    /// <see cref="DrawPage"/>.
    /// </summary>
    private interface IPageCanvas
    {
      void Text(double x, double y, double size, bool bold, string text);

      void Line(double x1, double y1, double x2, double y2, double width);

      void Rect(double x, double y, double w, double h, double lineWidth);

      void Polygon(IEnumerable<(double X, double Y)> pts);
    }

    /// <summary>
    /// Writes the report. It prints in black and white only: greys survive any shop printer, and parts
    /// are drawn as outlines so a sheet costs almost no toner and a marked-up copy stays readable.
    /// <para>
    /// The job takes the name of the file the report is saved as. A page reports only what SheetNest
    /// knows: it has no machine, tooling, material or thickness, so those lines are left out rather than
    /// printed empty.
    /// </para>
    /// </summary>
    public static void Write(
      string path,
      IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> layouts,
      int unplacedCount,
      string units = "in")
      => Write(path, layouts, unplacedCount, units, DateTime.Now);

    /// <summary>As the public overload, stamped with a given time instead of now, so a test can compare whole
    /// pages.</summary>
    internal static void Write(
      string path,
      IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> layouts,
      int unplacedCount,
      string units,
      DateTime now)
    {
      if (layouts == null || layouts.Count == 0)
      {
        throw new InvalidOperationException("Nothing to report. Run a nest first.");
      }

      string drawingName = Path.GetFileNameWithoutExtension(path);
      var pdf = new MiniPdf();
      for (int i = 0; i < layouts.Count; i++)
      {
        var c = new PageContent();
        DrawPage(c, layouts, i, drawingName, now, unplacedCount, units);
        pdf.AddPage(c);
      }

      File.WriteAllBytes(path, pdf.Build());
    }

    /// <summary>
    /// The settings of one layout's page, in the two groups the page prints them in: what the job and its
    /// sheet are, then what cutting this layout amounts to. Each line is a label and its value, padded so the
    /// colons line up.
    /// </summary>
    internal static (List<string> Job, List<string> Cut) SettingLines(
      IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> layouts,
      int index,
      string drawingName,
      DateTime now,
      int unplacedCount,
      string units)
    {
      var (sp, count, _) = layouts[index];
      string Setting(string label, string value) => $"{label,-LabelWidth} : {value}";

      var job = new List<string>
      {
        Setting("DRAWING NAME", Trunc(drawingName, 35)),
        Setting("LAYOUT", $"{index + 1} of {layouts.Count}"),
        Setting("DATE", now.ToString("ddd MMM dd HH:mm:ss yyyy", CultureInfo.InvariantCulture)),
        Setting("SHEET SIZE", $"{Num(sp.Sheet.WidthCalculated)} x {Num(sp.Sheet.HeightCalculated)} {units}"),
      };

      var cut = new List<string>
      {
        Setting("SHEET UTILIZATION", $"{Util(sp).ToString("0.0", CultureInfo.InvariantCulture)}%"),
        Setting("NUMBER OF SHEETS", count.ToString(CultureInfo.InvariantCulture)),
      };

      if (unplacedCount > 0)
      {
        // On every page, because any one of them may be the only one somebody reads.
        cut.Add(Setting("PARTS NOT PLACED", unplacedCount.ToString(CultureInfo.InvariantCulture)));
      }

      return (job, cut);
    }

    /// <summary>
    /// The parts list of one layout, one row per distinct part under <see cref="PartListHeader"/>: its item
    /// number, its name and what one sheet yields. Every row; the page decides how many it has room for.
    /// </summary>
    internal static List<string> PartRows(ISheetPlacement sheet)
      => NumberedParts(sheet)
        .Select(t => $"{t.Number,4} {Trunc(t.File, NameWidth),-NameWidth} {t.Qty,7}")
        .ToList();

    /// <summary>Points per sheet unit that the page draws this sheet at: as big as the drawing's room allows
    /// in both directions.</summary>
    internal static double DrawingScale(double sheetW, double sheetH) => Math.Min(BoxW / sheetW, BoxH / sheetH);

    /// <summary>Lays out the page of one layout on the given canvas.</summary>
    private static void DrawPage(
      IPageCanvas c,
      IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> layouts,
      int index,
      string drawingName,
      DateTime now,
      int unplacedCount,
      string units)
    {
      // Courier advances every character by 0.6 em, so at 10 points each column is a count of 6 point
      // steps and a line padded with spaces lines up under the one above without measuring anything.
      const double size = 10;
      const double leading = 12;
      const double left = 36;
      const double bottom = 48;

      var sp = layouts[index].Sheet;
      double sheetW = sp.Sheet.WidthCalculated;
      double sheetH = sp.Sheet.HeightCalculated;

      // SETTING INFORMATION: what the job and its sheet are, then what cutting this layout amounts to.
      var (job, cut) = SettingLines(layouts, index, drawingName, now, unplacedCount, units);
      double y = 560;
      Heading(c, left, y, size, SettingHeading);
      y -= 22;
      foreach (string line in job)
      {
        c.Text(left, y, size, bold: false, line);
        y -= leading;
      }

      y -= leading;
      foreach (string line in cut)
      {
        c.Text(left, y, size, bold: false, line);
        y -= leading;
      }

      // NEST PARTS LIST: this layout's parts, numbered from 1 down the list. Num Off is what ONE sheet
      // yields; NUMBER OF SHEETS above says how many times over.
      var rows = PartRows(sp);
      double py = 420;
      Heading(c, left, py, size, PartsHeading);
      py -= 22;
      c.Text(left, py, size, bold: false, PartListHeader);
      py -= leading;

      // Rows down to the bottom margin; a list too long for that gives its last line to saying so.
      int fits = (int)((py - bottom) / leading) + 1;
      int shown = rows.Count <= fits ? rows.Count : fits - 1;
      foreach (string row in rows.Take(shown))
      {
        c.Text(left, py, size, bold: false, row);
        py -= leading;
      }

      if (shown < rows.Count)
      {
        c.Text(left, py, size, bold: false, $"     ... and {rows.Count - shown} more");
      }

      // The sheet to scale, standing on the bottom margin and centred across the drawing's room: a wide
      // sheet lands low on the right, and a tall one gets the full height of the page.
      double scale = DrawingScale(sheetW, sheetH);
      double ox = BoxX + ((BoxW - (sheetW * scale)) / 2.0);
      double oy = bottom;

      c.Rect(ox, oy, sheetW * scale, sheetH * scale, 0.9);
      foreach (var pp in sp.PartPlacements)
      {
        var poly = pp.PlacedPart;
        if (poly?.Points == null || poly.Points.Length < 3)
        {
          continue;
        }

        c.Polygon(poly.Points.Select(p => (ox + (p.X * scale), oy + (p.Y * scale))));
        if (poly.Children != null)
        {
          foreach (var hole in poly.Children)
          {
            if (hole?.Points != null && hole.Points.Length >= 3)
            {
              c.Polygon(hole.Points.Select(p => (ox + (p.X * scale), oy + (p.Y * scale))));
            }
          }
        }
      }

      // Part numbers, in a SECOND pass on purpose: every outline goes down first, so a neighbour's
      // line drawn later can never cross a digit already placed. Same numbers as the parts list, so a
      // shape on the sheet can be matched to a row and marked.
      var partNumbers = NumberedParts(sp).ToDictionary(t => t.File, t => t.Number, StringComparer.OrdinalIgnoreCase);
      foreach (var pp in sp.PartPlacements)
      {
        var poly = pp.PlacedPart;
        if (poly?.Points == null
          || poly.Points.Length < 3
          || !partNumbers.TryGetValue(Label(pp), out int number))
        {
          continue;
        }

        string numberText = number.ToString(CultureInfo.InvariantCulture);
        var (nx, ny, nsize) = NumberLabel(poly, numberText, ox, oy, scale);
        c.Text(nx, ny, nsize, bold: true, numberText);
      }
    }

    /// <summary>A section title, underlined across its own text.</summary>
    private static void Heading(IPageCanvas c, double x, double y, double size, string title)
    {
      c.Text(x, y, size, bold: false, title);
      c.Line(x, y - 3, x + (0.6 * size * title.Length), y - 3, 0.6);
    }

    private static double Util(ISheetPlacement sp)
    {
      double area = sp.Sheet.WidthCalculated * sp.Sheet.HeightCalculated;
      return area <= 0 ? 0 : sp.PartPlacements.Sum(p => Math.Abs(p.Part.NetArea)) / area * 100.0;
    }

    private static string DisplayName(string path)
    {
      try
      {
        return string.IsNullOrWhiteSpace(path) ? "(part)" : Path.GetFileName(path);
      }
      catch (ArgumentException)
      {
        return path ?? "(part)";
      }
    }

    /// <summary>Report label for a placement — mirrored copies count as their own line item.</summary>
    private static string Label(IPartPlacement p) => PartColors.LabelFor(p);

    /// <summary>
    /// One row per distinct part on ONE layout, in the order its parts list prints them, each carrying
    /// the number that marks it on that layout's drawing.
    /// <para>
    /// Numbered from 1 on every page, the way a nest sheet numbers its items: the list a number is read
    /// against sits on the same page as the drawing it was read off. Qty is what one sheet of the layout
    /// yields. A mirrored copy is its own row and so its own number, because it is the other hand and
    /// takes a different mark.
    /// </para>
    /// </summary>
    internal static List<(string File, int Qty, int Number)> NumberedParts(ISheetPlacement sheet)
    {
      return sheet.PartPlacements
        .GroupBy(p => Label(p), StringComparer.OrdinalIgnoreCase)
        .Select(g => (File: g.Key, Qty: g.Count()))
        .OrderBy(t => t.File, Comparer<string>.Create(NaturalCompare))
        .Select((t, i) => (t.File, t.Qty, Number: i + 1))
        .ToList();
    }

    /// <summary>
    /// Orders names the way a file listing does, so p9 comes before p88 and p88 before p103: runs of
    /// digits compare as numbers, the text between them ignoring case. Plain text order would put p103
    /// first.
    /// </summary>
    private static int NaturalCompare(string a, string b)
    {
      // Split keeps the digit runs because they are captured, so both arrays alternate text and digits,
      // text first even when it is empty.
      string[] x = Regex.Split(a ?? string.Empty, @"(\d+)");
      string[] y = Regex.Split(b ?? string.Empty, @"(\d+)");
      for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
      {
        int order;
        if (i % 2 == 0)
        {
          order = string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
        }
        else
        {
          // Numbers of any length: without their leading zeros, the longer run is the bigger number.
          string m = x[i].TrimStart('0');
          string n = y[i].TrimStart('0');
          order = m.Length != n.Length ? m.Length.CompareTo(n.Length) : string.CompareOrdinal(m, n);
        }

        if (order != 0)
        {
          return order;
        }
      }

      return x.Length.CompareTo(y.Length);
    }

    /// <summary>
    /// Where a part's number goes on the sheet drawing and how big it is, in PDF points.
    /// <para>
    /// The anchor is the outline's AREA CENTROID, not the middle of its bounding box: on an L or a C
    /// the box centre lands in the notch, which is off the material and usually straight on top of
    /// whatever is nested in there. On a convex part the two agree.
    /// </para>
    /// <para>
    /// A ring is the case the centroid cannot serve: its centroid is its own HOLE. That is not a
    /// cosmetic miss, because the nester parks other parts inside big cutouts, so the number would land
    /// on top of the number of whatever is nested in there and neither could be read. When the centroid
    /// is off the material the anchor moves to the roomiest point that is ON it, and the size is then
    /// taken from the room there rather than from the outer box: a thin-walled ring would otherwise
    /// take the LARGEST number allowed and print it across its own bore.
    /// </para>
    /// <para>
    /// The size follows the part, bounded at both ends: under about four points nothing survives
    /// printing, and over twelve a big part gets a silly one. Small parts keep their number even when
    /// it slightly overhangs, because a part with no number is a part the operator cannot mark.
    /// </para>
    /// </summary>
    internal static (double X, double Y, double Size) NumberLabel(
      INfp placed,
      string text,
      double ox,
      double oy,
      double scale)
    {
      (double cx, double cy) = Centroid(placed);
      double side = Math.Min(placed.MaxX - placed.MinX, placed.MaxY - placed.MinY) * scale;
      double size = Math.Max(4.0, Math.Min(12.0, side * 0.42));

      if (!OnMaterial(placed, cx, cy))
      {
        // Sized where the number actually goes. 2 * room is never MORE than the shorter side of the
        // box, whatever the shape, so this can only ever shrink the number, never grow it: a part that
        // already fitted keeps what it had, and one that never fitted gets a size that does.
        var (mx, my, room) = RoomiestMaterialPoint(placed);
        if (room > 0)
        {
          cx = mx;
          cy = my;
          size = Math.Max(4.0, Math.Min(12.0, 2.0 * room * scale * 0.42));
        }
      }

      // Courier-Bold advances every glyph by 0.6 em and stands its digits 0.616 em tall (Adobe's metrics
      // for the font). Text() draws from the baseline, left aligned, and this writer cannot measure a
      // string, so centring is arithmetic.
      double width = 0.6 * size * Math.Max(1, text?.Length ?? 1);
      return (
        ox + (cx * scale) - (width / 2.0),
        oy + (cy * scale) - (0.616 * size / 2.0),
        size);
    }

    /// <summary>Area centroid of a closed ring, falling back to the bounding-box centre for a
    /// degenerate one, which is the only case where the area is zero and there is nothing to divide
    /// by.</summary>
    private static (double X, double Y) Centroid(INfp poly)
    {
      var pts = poly.Points;
      double twiceArea = 0, cx = 0, cy = 0;
      for (int i = 0; i < pts.Length; i++)
      {
        var p = pts[i];
        var q = pts[(i + 1) % pts.Length];
        double cross = (p.X * q.Y) - (q.X * p.Y);
        twiceArea += cross;
        cx += (p.X + q.X) * cross;
        cy += (p.Y + q.Y) * cross;
      }

      return Math.Abs(twiceArea) < 1e-12
        ? ((poly.MinX + poly.MaxX) / 2.0, (poly.MinY + poly.MaxY) / 2.0)
        : (cx / (3.0 * twiceArea), cy / (3.0 * twiceArea));
    }

    /// <summary>
    /// Whether the point is on the part's MATERIAL: inside the outline and inside none of its holes.
    /// <para>
    /// Reads raw <c>Points</c>, the same coordinates the drawing and <see cref="Centroid"/> use. The
    /// solution has three point-in-polygon routines already and none of them fits here: two are private
    /// to the viewer control, and the public one adds <c>OffsetX/OffsetY</c>, which this report never
    /// applies anywhere. Those offsets happen to be null today, so borrowing it would be right by luck,
    /// and would put a number where the shape is not the day one of them is set.
    /// </para>
    /// </summary>
    private static bool OnMaterial(INfp poly, double x, double y)
    {
      if (poly == null || !ContourContains(poly.Points, x, y))
      {
        return false;
      }

      if (poly.Children != null)
      {
        foreach (var hole in poly.Children)
        {
          // Guarded exactly as the drawing guards it: a hole too small to be drawn is not on the paper,
          // so it takes no material away and the number must not dodge it.
          if (hole?.Points != null && hole.Points.Length >= 3 && ContourContains(hole.Points, x, y))
          {
            return false;
          }
        }
      }

      return true;
    }

    /// <summary>Even-odd ray casting across one closed ring.</summary>
    private static bool ContourContains(SvgPoint[] pts, double x, double y)
    {
      if (pts == null || pts.Length < 3)
      {
        return false;
      }

      bool inside = false;
      for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
      {
        if (((pts[i].Y > y) != (pts[j].Y > y))
          && (x < (((pts[j].X - pts[i].X) * (y - pts[i].Y)) / (pts[j].Y - pts[i].Y)) + pts[i].X))
        {
          inside = !inside;
        }
      }

      return inside;
    }

    /// <summary>
    /// The material point furthest from any edge, hole edges included, and how far that is. Sampled,
    /// not solved: a grid over the bounding box, then a few rounds of halving the step around the best
    /// sample.
    /// <para>
    /// A wall thinner than the grid spacing can be stepped straight over, so a miss retries four times
    /// finer before giving up. A room of zero means nothing was found and the caller keeps what it had,
    /// which is the anchor this report has always used.
    /// </para>
    /// </summary>
    private static (double X, double Y, double Room) RoomiestMaterialPoint(INfp poly)
    {
      // Read ONCE. MinX and its three siblings are a LINQ pass over every point on each read
      // (NoFitPolygon.cs:96), so leaving them in a loop condition walks the whole part per probe.
      double minX = poly.MinX;
      double maxX = poly.MaxX;
      double minY = poly.MinY;
      double maxY = poly.MaxY;
      double w = maxX - minX;
      double h = maxY - minY;
      if (w <= 0 || h <= 0)
      {
        return (0, 0, 0);
      }

      foreach (int divisions in new[] { 64, 256 })
      {
        double step = Math.Max(w, h) / divisions;
        var best = (X: 0.0, Y: 0.0, Room: 0.0);
        for (double x = minX + (step / 2.0); x < maxX; x += step)
        {
          for (double y = minY + (step / 2.0); y < maxY; y += step)
          {
            Consider(poly, x, y, ref best);
          }
        }

        if (best.Room <= 0)
        {
          continue;
        }

        // Walk in on the winner: its eight neighbours at half the step, six times over.
        for (int round = 0; round < 6; round++)
        {
          step /= 2.0;
          for (int dx = -1; dx <= 1; dx++)
          {
            for (int dy = -1; dy <= 1; dy++)
            {
              if (dx != 0 || dy != 0)
              {
                Consider(poly, best.X + (dx * step), best.Y + (dy * step), ref best);
              }
            }
          }
        }

        return best;
      }

      return (0, 0, 0);
    }

    private static void Consider(INfp poly, double x, double y, ref (double X, double Y, double Room) best)
    {
      if (!OnMaterial(poly, x, y))
      {
        return;
      }

      double room = Clearance(poly, x, y);
      if (room > best.Room)
      {
        best = (x, y, room);
      }
    }

    /// <summary>Distance from the point to the nearest edge, of the outline or of any hole.</summary>
    private static double Clearance(INfp poly, double x, double y)
    {
      double best = EdgeDistance(poly.Points, x, y);
      if (poly.Children != null)
      {
        foreach (var hole in poly.Children)
        {
          if (hole?.Points != null && hole.Points.Length >= 3)
          {
            best = Math.Min(best, EdgeDistance(hole.Points, x, y));
          }
        }
      }

      return best;
    }

    private static double EdgeDistance(SvgPoint[] pts, double x, double y)
    {
      double best = double.MaxValue;
      for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
      {
        best = Math.Min(best, PointSegmentDistance(x, y, pts[j].X, pts[j].Y, pts[i].X, pts[i].Y));
      }

      return best;
    }

    private static double PointSegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
      double dx = bx - ax;
      double dy = by - ay;
      double length2 = (dx * dx) + (dy * dy);
      double t = length2 <= 0 ? 0 : ((((px - ax) * dx) + ((py - ay) * dy)) / length2);
      t = Math.Max(0, Math.Min(1, t));
      double qx = ax + (t * dx);
      double qy = ay + (t * dy);
      return Math.Sqrt(((px - qx) * (px - qx)) + ((py - qy) * (py - qy)));
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "~";

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>One page's content stream: text, lines, rectangles and polygon outlines.</summary>
    private sealed class PageContent : IPageCanvas
    {
      private readonly StringBuilder sb = new StringBuilder();

      public void Text(double x, double y, double size, bool bold, string text)
      {
        this.sb.AppendLine(FormattableString.Invariant(
          $"BT /{(bold ? "F2" : "F1")} {size:0.#} Tf {x:0.##} {y:0.##} Td ({Escape(text)}) Tj ET"));
      }

      public void Line(double x1, double y1, double x2, double y2, double width)
      {
        this.sb.AppendLine(FormattableString.Invariant(
          $"{width:0.##} w 0.35 0.35 0.35 RG {x1:0.##} {y1:0.##} m {x2:0.##} {y2:0.##} l S"));
      }

      public void Rect(double x, double y, double w, double h, double lineWidth)
      {
        this.sb.AppendLine(FormattableString.Invariant(
          $"{lineWidth:0.##} w 0.15 0.15 0.15 RG {x:0.##} {y:0.##} {w:0.##} {h:0.##} re S"));
      }

      /// <summary>
      /// A part or hole ring, closed and stroked in near-black. There is no fill: taking no fill
      /// argument is what makes a filled part impossible rather than merely unintended, and it is why
      /// a part nested inside another's hole can no longer be painted out by the part around it.
      /// </summary>
      public void Polygon(IEnumerable<(double X, double Y)> pts)
      {
        bool first = true;
        foreach (var (x, y) in pts)
        {
          this.sb.Append(FormattableString.Invariant($"{x:0.##} {y:0.##} {(first ? "m" : "l")} "));
          first = false;
        }

        if (first)
        {
          return;
        }

        // "s" closes and strokes without touching the fill colour, so the trailing "0 0 0 rg" is only
        // there to keep the text fill black for any Text() that follows.
        this.sb.AppendLine("h 0.5 w 0.15 0.15 0.15 RG s 0 0 0 rg");
      }

      public byte[] ToBytes()
      {
        // Reset colors at stream start so pages are independent.
        return Latin1("0 0 0 rg 0 0 0 RG\n" + this.sb.ToString());
      }

      private static string Escape(string s)
      {
        return s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
      }
    }

    /// <summary>
    /// Minimal PDF assembler: fixed Letter-landscape pages, Courier + Courier-Bold (standard-14 fonts
    /// every viewer ships, so nothing is embedded), uncompressed content streams, exact xref.
    /// </summary>
    private sealed class MiniPdf
    {
      private readonly List<byte[]> pageStreams = new List<byte[]>();

      public void AddPage(PageContent content)
      {
        this.pageStreams.Add(content.ToBytes());
      }

      public byte[] Build()
      {
        // Object layout: 1 Catalog, 2 Pages, 3 F1, 4 F2, then per page i: 5+2i = content, 6+2i = page.
        int n = this.pageStreams.Count;
        var objects = new List<byte[]>
        {
          Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
          Latin1($"<< /Type /Pages /Count {n} /Kids [{string.Join(" ", Enumerable.Range(0, n).Select(i => $"{6 + (2 * i)} 0 R"))}] >>"),
          Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"),
          Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Courier-Bold /Encoding /WinAnsiEncoding >>"),
        };

        foreach (var stream in this.pageStreams)
        {
          var obj = new MemoryStream();
          WriteBytes(obj, Latin1($"<< /Length {stream.Length} >>\nstream\n"));
          WriteBytes(obj, stream);
          WriteBytes(obj, Latin1("\nendstream"));
          objects.Add(obj.ToArray());
          objects.Add(Latin1(FormattableString.Invariant(
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageW} {PageH}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {objects.Count} 0 R >>")));
        }

        var outStream = new MemoryStream();
        WriteBytes(outStream, Latin1("%PDF-1.4\n"));
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
          offsets[i] = outStream.Position;
          WriteBytes(outStream, Latin1($"{i + 1} 0 obj\n"));
          WriteBytes(outStream, objects[i]);
          WriteBytes(outStream, Latin1("\nendobj\n"));
        }

        long xref = outStream.Position;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (long off in offsets)
        {
          sb.Append(off.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        WriteBytes(outStream, Latin1(sb.ToString()));
        return outStream.ToArray();
      }

      private static void WriteBytes(Stream s, byte[] b)
      {
        s.Write(b, 0, b.Length);
      }
    }

    private static byte[] Latin1(string s)
    {
      return Encoding.GetEncoding("ISO-8859-1").GetBytes(s);
    }
  }
}
