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
    private const double PageW = 612; // US Letter portrait, points
    private const double PageH = 792;

    /// <summary>
    /// <paramref name="partColours"/> comes from the window, not from the placements: the colours follow
    /// the order of the PART LIST and the owner may have picked their own, and neither of those can be read
    /// off a sheet. Null falls back to colouring by the order the parts appear in the layouts.
    /// </summary>
    public static void Write(
      string path,
      IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> layouts,
      int unplacedCount,
      string units = "in",
      IReadOnlyDictionary<string, (byte R, byte G, byte B)> partColours = null)
    {
      if (layouts == null || layouts.Count == 0)
      {
        throw new InvalidOperationException("Nothing to report. Run a nest first.");
      }

      // Mixed stock: every layout carries ITS OWN sheet size — never assume the first one's.
      int totalSheets = layouts.Sum(l => l.Count);
      int totalParts = layouts.Sum(l => l.Count * l.Sheet.PartPlacements.Count);
      double placedArea = layouts.Sum(l => l.Count * l.Sheet.PartPlacements.Sum(p => Math.Abs(p.Part.NetArea)));
      double stockArea = layouts.Sum(l => l.Count * l.Sheet.Sheet.WidthCalculated * l.Sheet.Sheet.HeightCalculated);
      double overallUtil = stockArea <= 0 ? 0 : placedArea / stockArea * 100.0;

      // Total quantity per source part across the whole job (mirrored copies listed separately —
      // a left-hand and a right-hand part are different physical parts on the shop floor).
      // Colour rides alongside the label: they are keyed differently on purpose — a mirrored copy is its own
      // LINE (different physical part to count) but shares its original's COLOUR (the parts list has one
      // thumbnail per file, and that thumbnail is what names the colour).
      var partTotals = layouts
        .SelectMany(l => l.Sheet.PartPlacements.Select(p => (File: Label(p), Colour: PartColors.ColourKeyFor(p), l.Count)))
        .GroupBy(t => t.File, StringComparer.OrdinalIgnoreCase)
        .Select(g => (File: g.Key, Colour: g.First().Colour, Qty: g.Sum(t => t.Count)))
        .OrderByDescending(t => t.Qty)
        .ToList();

      var colours = partColours
        ?? PartColors.Build(layouts.SelectMany(l => l.Sheet.PartPlacements).Select(p => p.Part?.Name));

      // Every part in the job is coloured, a single-type job included — the paper has to match the screen.
      bool colourCoded = colours.Count > 0;
      double nameX = colourCoded ? 60 : 48;

      var pdf = new MiniPdf();
      string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
      int pageCount = 1 + layouts.Count;

      // ── Page 1: written for TWO readers — the material buyer (big unambiguous "what to buy"
      // box, nothing to interpret) and the operator (cutting plan + part totals tables below). ──
      {
        var c = new PageContent();
        Header(c, stamp);

        // MATERIAL REQUIRED — one line per sheet size with a big count. This is the purchase order.
        var stockLines = layouts
          .GroupBy(l => (W: l.Sheet.Sheet.WidthCalculated, H: l.Sheet.Sheet.HeightCalculated))
          .Select(g => (g.Key.W, g.Key.H, Count: g.Sum(x => x.Count)))
          .OrderByDescending(t => t.W * t.H)
          .ToList();
        double totalSqFt = stockLines.Sum(s => s.W * s.H * s.Count) / 144.0;

        // Portrait: page 1 is a single stacked column (40..572 wide), section after section.
        c.Text(40, 716, 13, bold: true, "MATERIAL REQUIRED");
        double rowH = 34;
        double boxH = (stockLines.Count * rowH) + 32;
        double boxTop = 706;
        c.FillRect(40, boxTop - boxH, 532, boxH, 0.94, 0.94, 0.94);
        c.Rect(40, boxTop - boxH, 532, boxH, 0.9);
        double ry = boxTop - 27;
        foreach (var (w, h, count) in stockLines)
        {
          c.Text(56, ry, 21, bold: true, count.ToString("#,0", CultureInfo.InvariantCulture));
          c.Text(120, ry, 13, bold: false, $"sheet{(count == 1 ? string.Empty : "s")} of  {Num(w)} x {Num(h)} {units}");
          c.SetFill(0.35, 0.35, 0.35);
          c.Text(430, ry, 10, bold: false, $"{(w * h * count / 144.0).ToString("#,0", CultureInfo.InvariantCulture)} sq ft");
          c.SetFill(0, 0, 0);
          ry -= rowH;
        }

        c.Line(52, ry + 22, 560, ry + 22, 0.6);
        c.Text(56, ry + 6, 11, bold: true,
          $"Total:  {totalSheets.ToString("#,0", CultureInfo.InvariantCulture)} sheet{(totalSheets == 1 ? string.Empty : "s")}   -   {totalSqFt.ToString("#,0", CultureInfo.InvariantCulture)} sq ft");

        // Job summary panel (stacked below the material box).
        double sumTop = boxTop - boxH - 34;
        int jRows = unplacedCount > 0 ? 4 : 3;
        double sumBoxH = (jRows * 19) + 14;
        c.Text(40, sumTop, 13, bold: true, "JOB SUMMARY");
        c.FillRect(40, sumTop - 10 - sumBoxH, 532, sumBoxH, 0.965, 0.965, 0.965);
        c.Rect(40, sumTop - 10 - sumBoxH, 532, sumBoxH, 0.9);
        double jy = sumTop - 27;
        void JRow(string label, string value, bool warn = false)
        {
          c.Text(56, jy, 10.5, bold: false, label);
          if (warn)
          {
            c.SetFill(0.75, 0, 0);
          }

          c.Text(400, jy, 10.5, bold: true, value);
          c.SetFill(0, 0, 0);
          jy -= 19;
        }

        JRow("Parts to cut", totalParts.ToString("#,0", CultureInfo.InvariantCulture));
        JRow("Material used", $"{overallUtil.ToString("0.0", CultureInfo.InvariantCulture)} %");
        JRow("Different layouts", layouts.Count.ToString(CultureInfo.InvariantCulture));
        if (unplacedCount > 0)
        {
          JRow("Parts NOT placed", unplacedCount.ToString(CultureInfo.InvariantCulture), warn: true);
        }

        // CUTTING PLAN — one row per layout, table form: what to cut, on what, how many times.
        // Runs down to a floor that reserves room for the PART TOTALS section below it.
        double tableTop = sumTop - 10 - sumBoxH - 34;
        c.Text(40, tableTop, 13, bold: true, "CUTTING PLAN");
        double ty = tableTop - 20;
        c.FillRect(40, ty - 5, 532, 19, 0.92, 0.92, 0.92);
        c.Text(48, ty, 10, bold: true, "Layout");
        c.Text(110, ty, 10, bold: true, "Page");
        c.Text(160, ty, 10, bold: true, "Sheet size");
        c.Text(280, ty, 10, bold: true, "Parts");
        c.Text(360, ty, 10, bold: true, "Cut");
        c.Text(460, ty, 10, bold: true, "Used");
        ty -= 19;
        int planShown = 0;
        for (int i = 0; i < layouts.Count && ty >= 190; i++)
        {
          var l = layouts[i];
          c.Text(48, ty, 10, bold: false, $"Layout {i + 1}");
          c.Text(110, ty, 10, bold: false, (i + 2).ToString(CultureInfo.InvariantCulture));
          c.Text(160, ty, 10, bold: false, $"{Num(l.Sheet.Sheet.WidthCalculated)} x {Num(l.Sheet.Sheet.HeightCalculated)}");
          c.Text(280, ty, 10, bold: false, l.Sheet.PartPlacements.Count.ToString(CultureInfo.InvariantCulture));
          c.Text(360, ty, 10, bold: true, $"x {l.Count}");
          c.Text(460, ty, 10, bold: false, $"{Util(l.Sheet).ToString("0.0", CultureInfo.InvariantCulture)} %");
          c.Line(40, ty - 5, 572, ty - 5, 0.3);
          ty -= 17;
          planShown++;
        }

        if (planShown < layouts.Count)
        {
          c.Text(48, ty, 10, bold: false, $"... and {layouts.Count - planShown} more layouts (see their pages)");
          ty -= 17;
        }

        // PART TOTALS — what the job produces, for checking against the order (below the plan).
        double py = ty - 24;
        c.Text(40, py, 13, bold: true, "PART TOTALS");
        py -= 20;
        c.FillRect(40, py - 5, 532, 19, 0.92, 0.92, 0.92);
        c.Text(nameX, py, 10, bold: true, "Part");
        c.Text(520, py, 10, bold: true, "Qty");
        py -= 19;
        int totalsShown = 0;
        foreach (var (file, colourKey, qty) in partTotals)
        {
          if (py < 40)
          {
            break;
          }

          if (colourCoded && colours.TryGetValue(colourKey, out var swatch))
          {
            c.FillRect(48, py, 8, 8, swatch.R / 255.0, swatch.G / 255.0, swatch.B / 255.0);
          }

          c.Text(nameX, py, 10, bold: false, Trunc(file, 60));
          c.Text(520, py, 10, bold: false, qty.ToString("#,0", CultureInfo.InvariantCulture));
          c.Line(40, py - 5, 572, py - 5, 0.3);
          py -= 17;
          totalsShown++;
        }

        if (totalsShown < partTotals.Count)
        {
          c.Text(48, System.Math.Max(py, 32), 10, bold: false, $"... and {partTotals.Count - totalsShown} more");
        }

        Footer(c, 1, pageCount);
        pdf.AddPage(c);
      }

      // ── One page per distinct layout ────────────────────────────────────────────────────────
      for (int i = 0; i < layouts.Count; i++)
      {
        var (sp, count, _) = layouts[i];
        double sheetW = sp.Sheet.WidthCalculated;
        double sheetH = sp.Sheet.HeightCalculated;
        var c = new PageContent();
        Header(c, stamp);

        c.Text(40, 700, 16, bold: true, $"Layout {i + 1} of {layouts.Count}   -   cut x {count}");
        c.Text(40, 680, 11, bold: false,
          $"{sp.PartPlacements.Count} parts   |   {Util(sp).ToString("0.0", CultureInfo.InvariantCulture)}% utilization   |   sheet {Num(sheetW)} x {Num(sheetH)} {units}");

        // Drawing area (top, full portrait width) — sheet border + every part outline with its
        // holes, Y up like the shop; centered both ways inside the box.
        const double boxX = 40, boxY = 250, boxW = 532, boxH = 410;
        double scale = Math.Min(boxW / sheetW, boxH / sheetH);
        double ox = boxX + ((boxW - (sheetW * scale)) / 2.0);
        double oy = boxY + ((boxH - (sheetH * scale)) / 2.0);

        c.Rect(ox, oy, sheetW * scale, sheetH * scale, 0.9);
        foreach (var pp in sp.PartPlacements)
        {
          var poly = pp.PlacedPart;
          if (poly?.Points == null || poly.Points.Length < 3)
          {
            continue;
          }

          c.Polygon(
            poly.Points.Select(p => (ox + (p.X * scale), oy + (p.Y * scale))),
            fill: true,
            fillColour: colourCoded ? PartColors.For(colours, pp) : (ValueTuple<byte, byte, byte>?)null);
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

        // Parts on this layout (below the drawing, portrait flow). Mirrored copies list separately.
        var onSheet = sp.PartPlacements
          .GroupBy(p => Label(p), StringComparer.OrdinalIgnoreCase)
          .Select(g => (File: g.Key, Colour: PartColors.ColourKeyFor(g.First()), Qty: g.Count()))
          .OrderByDescending(t => t.Qty)
          .ToList();

        double py = 204;
        c.Text(40, 222, 12, bold: true, "Parts on this layout");
        int listShown = 0;
        foreach (var (file, colourKey, qty) in onSheet)
        {
          if (py < 40)
          {
            break;
          }

          if (colourCoded && colours.TryGetValue(colourKey, out var swatch))
          {
            c.FillRect(40, py, 8, 8, swatch.R / 255.0, swatch.G / 255.0, swatch.B / 255.0);
          }

          c.Text(colourCoded ? 52 : 40, py, 10, bold: false, $"{qty} x  {Trunc(file, 60)}");
          py -= 15;
          listShown++;
        }

        if (listShown < onSheet.Count)
        {
          c.Text(40, System.Math.Max(py, 32), 10, bold: false, $"... and {onSheet.Count - listShown} more");
        }

        Footer(c, i + 2, pageCount);
        pdf.AddPage(c);
      }

      File.WriteAllBytes(path, pdf.Build());
    }

    private static void Header(PageContent c, string stamp)
    {
      c.Text(40, 752, 18, bold: true, "SheetNest - Nest Report");
      c.Text(478, 755, 10, bold: false, stamp);
      c.Line(40, 742, 572, 742, 1.0);
    }

    private static void Footer(PageContent c, int page, int of)
    {
      c.Text(280, 24, 9, bold: false, $"Page {page} of {of}");
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

      public void FillRect(double x, double y, double w, double h, double r, double g, double b)
      {
        this.sb.AppendLine(FormattableString.Invariant(
          $"{r:0.###} {g:0.###} {b:0.###} rg {x:0.##} {y:0.##} {w:0.##} {h:0.##} re f 0 0 0 rg"));
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

      /// <summary>Part outline (its part-type colour, or aluminum-gray when uncoloured) or hole (white
      /// fill), always stroked.</summary>
      public void Polygon(IEnumerable<(double X, double Y)> pts, bool fill, bool holeFill = false, (byte R, byte G, byte B)? fillColour = null)
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

        // The trailing "0 0 0 rg" restores the text fill color — without it every Text() drawn after
        // a polygon inherits the part/hole fill (white holes made whole part lists invisible).
        // Part fill (its type's colour, else aluminum-gray) + near-black outline, matching the app.
        string partRg = fillColour.HasValue
          ? FormattableString.Invariant($"{fillColour.Value.R / 255.0:0.###} {fillColour.Value.G / 255.0:0.###} {fillColour.Value.B / 255.0:0.###}")
          : "0.71 0.72 0.74";
        string paint = fill ? partRg + " rg b" : holeFill ? "1 1 1 rg b" : "s";
        this.sb.AppendLine(FormattableString.Invariant($"h 0.5 w 0.15 0.15 0.15 RG {paint} 0 0 0 rg"));
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
    /// Minimal PDF assembler: fixed Letter-portrait pages, Helvetica + Helvetica-Bold (standard-14
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
