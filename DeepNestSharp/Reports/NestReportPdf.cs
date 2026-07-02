namespace DeepNestSharp.Reports
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text;
  using DeepNestLib;
  using DeepNestLib.Placement;

  /// <summary>
  /// Writes the production nest report as a PDF: a job summary page (sheets to cut, utilization,
  /// part totals) plus one page per DISTINCT layout with a scaled drawing and its cut count —
  /// the paper the operator takes to the laser. Self-contained minimal PDF writer (vector lines +
  /// Helvetica text is all a report needs): no external PDF library, nothing to license.
  /// </summary>
  public static class NestReportPdf
  {
    private const double PageW = 792; // US Letter landscape, points
    private const double PageH = 612;

    public static void Write(string path, IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> layouts, int unplacedCount)
    {
      if (layouts == null || layouts.Count == 0)
      {
        throw new InvalidOperationException("Nothing to report — run a nest first.");
      }

      double sheetW = layouts[0].Sheet.Sheet.WidthCalculated;
      double sheetH = layouts[0].Sheet.Sheet.HeightCalculated;
      double sheetArea = sheetW * sheetH;
      int totalSheets = layouts.Sum(l => l.Count);
      int totalParts = layouts.Sum(l => l.Count * l.Sheet.PartPlacements.Count);
      double placedArea = layouts.Sum(l => l.Count * l.Sheet.PartPlacements.Sum(p => Math.Abs(p.Part.NetArea)));
      double overallUtil = sheetArea <= 0 || totalSheets == 0 ? 0 : placedArea / (sheetArea * totalSheets) * 100.0;

      // Total quantity per source part across the whole job.
      var partTotals = layouts
        .SelectMany(l => l.Sheet.PartPlacements.Select(p => (File: DisplayName(p.Part.Name), l.Count)))
        .GroupBy(t => t.File, StringComparer.OrdinalIgnoreCase)
        .Select(g => (File: g.Key, Qty: g.Sum(t => t.Count)))
        .OrderByDescending(t => t.Qty)
        .ToList();

      var pdf = new MiniPdf();
      string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
      int pageCount = 1 + layouts.Count;

      // ── Page 1: job summary ─────────────────────────────────────────────────────────────────
      {
        var c = new PageContent();
        Header(c, stamp);
        c.Text(40, 520, 16, bold: true, "Job summary");

        double y = 488;
        void Row(string label, string value, bool warn = false)
        {
          c.Text(48, y, 11, bold: false, label);
          if (warn)
          {
            c.SetFill(0.75, 0, 0);
          }

          c.Text(230, y, 11, bold: true, value);
          c.SetFill(0, 0, 0);
          y -= 20;
        }

        Row("Sheet size", $"{Num(sheetW)} x {Num(sheetH)} in");
        Row("Sheets to cut", totalSheets.ToString(CultureInfo.InvariantCulture));
        Row("Distinct layouts", layouts.Count.ToString(CultureInfo.InvariantCulture));
        Row("Parts placed", totalParts.ToString(CultureInfo.InvariantCulture));
        Row("Overall utilization", $"{overallUtil.ToString("0.0", CultureInfo.InvariantCulture)} %");
        if (unplacedCount > 0)
        {
          Row("PARTS NOT PLACED", unplacedCount.ToString(CultureInfo.InvariantCulture), warn: true);
        }

        // Cutting plan lines: "30 x Layout 1 (26 parts, 86.0%)".
        y -= 8;
        c.Text(40, y, 13, bold: true, "Cutting plan");
        y -= 22;
        for (int i = 0; i < layouts.Count; i++)
        {
          var l = layouts[i];
          c.Text(48, y, 11, bold: false,
            $"{l.Count} x  Layout {i + 1}   ({l.Sheet.PartPlacements.Count} parts, {Util(l.Sheet).ToString("0.0", CultureInfo.InvariantCulture)}% utilization)");
          y -= 18;
        }

        // Part totals table on the right half.
        double ty = 488;
        c.Text(470, 520, 13, bold: true, "Part totals");
        c.Text(470, ty, 10, bold: true, "Part");
        c.Text(710, ty, 10, bold: true, "Qty");
        c.Line(470, ty - 4, 760, ty - 4, 0.7);
        ty -= 17;
        foreach (var (file, qty) in partTotals.Take(24))
        {
          c.Text(470, ty, 10, bold: false, Trunc(file, 42));
          c.Text(710, ty, 10, bold: false, qty.ToString(CultureInfo.InvariantCulture));
          ty -= 15;
        }

        if (partTotals.Count > 24)
        {
          c.Text(470, ty, 10, bold: false, $"... and {partTotals.Count - 24} more");
        }

        Footer(c, 1, pageCount);
        pdf.AddPage(c);
      }

      // ── One page per distinct layout ────────────────────────────────────────────────────────
      for (int i = 0; i < layouts.Count; i++)
      {
        var (sp, count, _) = layouts[i];
        var c = new PageContent();
        Header(c, stamp);

        c.Text(40, 520, 16, bold: true, $"Layout {i + 1} of {layouts.Count}   -   cut x {count}");
        c.Text(40, 500, 11, bold: false,
          $"{sp.PartPlacements.Count} parts   |   {Util(sp).ToString("0.0", CultureInfo.InvariantCulture)}% utilization   |   sheet {Num(sheetW)} x {Num(sheetH)} in");

        // Drawing area (left) — sheet border + every part outline with its holes, Y up like the shop.
        const double boxX = 40, boxY = 48, boxW = 540, boxH = 430;
        double scale = Math.Min(boxW / sheetW, boxH / sheetH);
        double ox = boxX;
        double oy = boxY + ((boxH - (sheetH * scale)) / 2.0);

        c.Rect(ox, oy, sheetW * scale, sheetH * scale, 0.9);
        foreach (var pp in sp.PartPlacements)
        {
          var poly = pp.PlacedPart;
          if (poly?.Points == null || poly.Points.Length < 3)
          {
            continue;
          }

          c.Polygon(poly.Points.Select(p => (ox + (p.X * scale), oy + (p.Y * scale))), fill: true);
          if (poly.Children != null)
          {
            foreach (var hole in poly.Children)
            {
              if (hole?.Points != null && hole.Points.Length >= 3)
              {
                c.Polygon(hole.Points.Select(p => (ox + (p.X * scale), oy + (p.Y * scale))), fill: false, holeFill: true);
              }
            }
          }
        }

        // Parts on this layout (right column).
        var onSheet = sp.PartPlacements
          .GroupBy(p => DisplayName(p.Part.Name), StringComparer.OrdinalIgnoreCase)
          .Select(g => (File: g.Key, Qty: g.Count()))
          .OrderByDescending(t => t.Qty)
          .ToList();

        double py = 470;
        c.Text(600, 490, 12, bold: true, "Parts on this layout");
        foreach (var (file, qty) in onSheet.Take(26))
        {
          c.Text(600, py, 10, bold: false, $"{qty} x  {Trunc(file, 26)}");
          py -= 15;
        }

        if (onSheet.Count > 26)
        {
          c.Text(600, py, 10, bold: false, $"... and {onSheet.Count - 26} more");
        }

        Footer(c, i + 2, pageCount);
        pdf.AddPage(c);
      }

      File.WriteAllBytes(path, pdf.Build());
    }

    private static void Header(PageContent c, string stamp)
    {
      c.Text(40, 572, 18, bold: true, "SheetNest - Nest Report");
      c.Text(650, 575, 10, bold: false, stamp);
      c.Line(40, 562, 752, 562, 1.0);
    }

    private static void Footer(PageContent c, int page, int of)
    {
      c.Text(370, 24, 9, bold: false, $"Page {page} of {of}");
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

    private static string Trunc(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "~";

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>One page's content stream: text, lines, rectangles and filled polygons.</summary>
    private sealed class PageContent
    {
      private readonly StringBuilder sb = new StringBuilder();

      public void SetFill(double r, double g, double b)
      {
        this.sb.AppendLine(FormattableString.Invariant($"{r:0.###} {g:0.###} {b:0.###} rg"));
      }

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

      /// <summary>Part outline (light blue fill) or hole (white fill), always stroked.</summary>
      public void Polygon(IEnumerable<(double X, double Y)> pts, bool fill, bool holeFill = false)
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

        string paint = fill ? "0.85 0.91 0.96 rg b" : holeFill ? "1 1 1 rg b" : "s";
        this.sb.AppendLine(FormattableString.Invariant($"h 0.5 w 0.25 0.3 0.35 RG {paint}"));
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
    /// Minimal PDF assembler: fixed Letter-landscape pages, Helvetica + Helvetica-Bold (standard-14
    /// fonts every viewer ships — nothing embedded), uncompressed content streams, exact xref.
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
          Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
          Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
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
