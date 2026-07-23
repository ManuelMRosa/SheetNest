namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text;
  using System.Xml.Linq;

  /// <summary>
  /// SheetCam's ".nest" interchange file — the format its AutoNesting plugin uses to hand a job to an
  /// external nester and read the arrangement back ("Export to Auto Nesting" / "Import nest from Auto
  /// Nesting"). An un-nested file carries every <c>Position</c> at X=0 Y=0 A=0; nesting means filling
  /// those in.
  /// <para>
  /// The document is kept as loaded and only the <c>Positions</c> are rewritten, so part geometry,
  /// <c>Metadata</c>, <c>Settings</c> and elements this code does not model (<c>Lead</c>, <c>Loop</c>)
  /// survive the round trip untouched.
  /// </para>
  /// <para>
  /// Geometry is in MILLIMETRES regardless of the <c>Units</c> attribute (SheetCam is internally
  /// metric); only the <c>Settings</c> spacings are in the declared unit.
  /// </para>
  /// </summary>
  public sealed class SheetCamNestFile
  {
    public const string FileDialogFilter = "SheetCam nest file (*.nest)|*.nest";

    /// <summary>Vendor + version AutoNesting.dll validates on import; anything else it rejects.</summary>
    private const string ExpectedVendor = "SheetCam01";
    private const string ExpectedVersion = "1.0";

    private readonly XDocument document;

    private SheetCamNestFile(XDocument document, IReadOnlyList<SheetCamNestPart> parts, IReadOnlyList<SheetCamNestSheet> sheets)
    {
      this.document = document;
      this.Parts = parts;
      this.Sheets = sheets;
    }

    /// <summary>Unit the <see cref="PartSpacing"/> and <see cref="EdgeSpacing"/> values are given in ("in" / "mm").</summary>
    public string Units { get; private set; } = "in";

    /// <summary>Gap between parts, in <see cref="Units"/>.</summary>
    public double PartSpacing { get; private set; }

    /// <summary>Gap between parts and the sheet edge, in <see cref="Units"/>.</summary>
    public double EdgeSpacing { get; private set; }

    /// <summary>Job name from the Metadata block, when present.</summary>
    public string JobName { get; private set; }

    public IReadOnlyList<SheetCamNestPart> Parts { get; }

    public IReadOnlyList<SheetCamNestSheet> Sheets { get; }

    /// <summary>Millimetres per drawing unit: the factor to turn this file's geometry into app units.</summary>
    public static double MmPerDrawingUnit(bool unitsMm) => unitsMm ? 1.0 : 25.4;

    /// <summary>
    /// Factor turning <see cref="PartSpacing"/> / <see cref="EdgeSpacing"/> into drawing units. Unlike the
    /// geometry, these are expressed in whatever <see cref="Units"/> declares, so they only need scaling
    /// when the file and the app disagree.
    /// </summary>
    public double SettingsToDrawingUnits(bool unitsMm)
    {
      bool fileIsMm = this.Units != null && this.Units.Trim().StartsWith("mm", StringComparison.OrdinalIgnoreCase);
      if (fileIsMm == unitsMm)
      {
        return 1.0;
      }

      return fileIsMm ? 1.0 / 25.4 : 25.4;
    }

    /// <summary>
    /// Reads a .nest file. Throws <see cref="InvalidDataException"/> with the same wording SheetCam uses
    /// when the vendor/version markers do not match, so the message is recognisable.
    /// </summary>
    public static SheetCamNestFile Load(string path)
    {
      XDocument doc;
      try
      {
        doc = XDocument.Load(path);
      }
      catch (System.Xml.XmlException ex)
      {
        throw new InvalidDataException("Not a valid nesting file: " + ex.Message, ex);
      }

      var root = doc.Root;
      if (root == null || root.Name.LocalName != "NestingData")
      {
        throw new InvalidDataException("Not a valid nesting file.");
      }

      if ((string)root.Attribute("ID") != ExpectedVendor)
      {
        throw new InvalidDataException("Wrong vendor version: expected " + ExpectedVendor + ".");
      }

      if ((string)root.Attribute("Version") != ExpectedVersion)
      {
        throw new InvalidDataException("Wrong file version: expected " + ExpectedVersion + ".");
      }

      var parts = (root.Element("Parts")?.Elements("Part") ?? Enumerable.Empty<XElement>())
        .Select(ReadPart)
        .ToList();

      var sheets = (root.Element("Sheets")?.Elements("Sheet") ?? Enumerable.Empty<XElement>())
        .Select(ReadSheet)
        .ToList();

      if (parts.Count == 0)
      {
        throw new InvalidDataException("No nest data: the file contains no parts.");
      }

      var result = new SheetCamNestFile(doc, parts, sheets);

      var settings = root.Element("Settings");
      if (settings != null)
      {
        result.Units = (string)settings.Attribute("Units") ?? "in";
        result.PartSpacing = ReadDouble(settings.Attribute("PartSpacing"));
        result.EdgeSpacing = ReadDouble(settings.Attribute("EdgeSpacing"));
      }

      result.JobName = (string)root.Element("Metadata")?.Element("Job")?.Attribute("Name");
      return result;
    }

    /// <summary>
    /// Recovers the cutting kerf, in millimetres, from the geometry itself — the file never states it and
    /// does not say which tool the job used.
    /// <para>
    /// A lead meets the contour on the TOOLPATH, which runs half a kerf off the drawn outline, so that gap
    /// is half the kerf. The catch is that curves arrive tessellated: measured against a chord instead of
    /// the true arc, a reading comes out low near the chord's ends and high near its middle, so individual
    /// samples scatter either way by up to the tessellation's sagitta. Only a lead meeting a straight edge,
    /// or landing on a vertex, reads exactly.
    /// </para>
    /// <para>
    /// Hence the maximum rather than an average. Its error can only be upward and is bounded by that
    /// sagitta, and the two directions are not equally bad: reading high costs a sliver of material, while
    /// reading low puts a lead through the neighbouring part. Verified against a real job — the outer lead
    /// reads 0.17018 mm, and 0.34036 mm is exactly the kerf of tools in SheetCam's own table, whereas the
    /// median of all its leads would have said 0.306 mm. Returns 0 when the job carries no leads to
    /// measure, which leaves plain DXF jobs nesting exactly as before.
    /// </para>
    /// </summary>
    public double DeriveKerfMm()
    {
      // Beyond this a "kerf" is certainly a misreading, not a cut width.
      const double MaxPlausibleKerfMm = 2.0;

      double halfKerf = 0;
      foreach (var part in this.Parts)
      {
        var contours = new List<IReadOnlyList<SvgPoint>>();
        if (part.External.Count >= 3)
        {
          contours.Add(part.External);
        }

        contours.AddRange(part.Internals);
        if (contours.Count == 0)
        {
          continue;
        }

        foreach (var path in part.ToolPaths)
        {
          // One end pierces (well clear of the outline), the other meets it: the nearer one is the join.
          double first = NearestContourDistance(path[0], contours);
          double last = NearestContourDistance(path[path.Count - 1], contours);
          halfKerf = Math.Max(halfKerf, Math.Min(first, last));
        }
      }

      double kerf = 2 * halfKerf;
      return kerf > 0 && kerf <= MaxPlausibleKerfMm ? kerf : 0;
    }

    private static double NearestContourDistance(SvgPoint point, IReadOnlyList<IReadOnlyList<SvgPoint>> contours)
    {
      double best = double.MaxValue;
      foreach (var contour in contours)
      {
        for (int i = 0; i < contour.Count; i++)
        {
          var a = contour[i];
          var b = contour[(i + 1) % contour.Count];
          best = Math.Min(best, DistanceToSegment(point, a, b));
        }
      }

      return best;
    }

    private static double DistanceToSegment(SvgPoint p, SvgPoint a, SvgPoint b)
    {
      double vx = b.X - a.X;
      double vy = b.Y - a.Y;
      double lengthSquared = (vx * vx) + (vy * vy);
      double t = lengthSquared <= 0 ? 0 : (((p.X - a.X) * vx) + ((p.Y - a.Y) * vy)) / lengthSquared;
      t = t < 0 ? 0 : (t > 1 ? 1 : t);
      double dx = p.X - (a.X + (t * vx));
      double dy = p.Y - (a.Y + (t * vy));
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// Replaces the placements of one part. Positions are in millimetres, <paramref name="positions"/>
    /// angles in radians anticlockwise — the file's own convention.
    /// </summary>
    public void SetPositions(string partName, IEnumerable<SheetCamNestPosition> positions)
    {
      var part = this.document.Root.Element("Parts")?.Elements("Part")
        .FirstOrDefault(p => (string)p.Attribute("Name") == partName);
      if (part == null)
      {
        throw new InvalidOperationException($"The nest file has no part named '{partName}'.");
      }

      var container = part.Element("Positions");
      if (container == null)
      {
        // Keep the vendor's element order: Positions comes before the geometry.
        container = new XElement("Positions");
        part.AddFirst(container);
      }

      container.RemoveNodes();
      foreach (var p in positions)
      {
        container.Add(new XElement(
          "Position",
          new XAttribute("X", Num(p.X)),
          new XAttribute("Y", Num(p.Y)),
          new XAttribute("A", Num(p.A)),
          new XAttribute("Sheet", p.Sheet.ToString(CultureInfo.InvariantCulture))));
      }
    }

    /// <summary>Sets how many sheets the arrangement uses. No-op unless the file declares exactly one sheet.</summary>
    public void SetSheetCount(int count)
    {
      var sheets = this.document.Root.Element("Sheets")?.Elements("Sheet").ToList();
      if (sheets == null || sheets.Count != 1)
      {
        return;
      }

      sheets[0].SetAttributeValue("Quantity", Math.Max(1, count).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Writes the file back as UTF-8 with a BOM, matching what SheetCam produces.</summary>
    public void Save(string path)
    {
      using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
      {
        this.document.Save(writer);
      }
    }

    private static SheetCamNestPart ReadPart(XElement part)
    {
      var positions = (part.Element("Positions")?.Elements("Position") ?? Enumerable.Empty<XElement>())
        .Select(p => new SheetCamNestPosition(
          ReadDouble(p.Attribute("X")),
          ReadDouble(p.Attribute("Y")),
          ReadDouble(p.Attribute("A")),
          (int)ReadDouble(p.Attribute("Sheet"))))
        .ToList();

      // Lead and Loop are cut path, not part outline: open polylines that run outside the contour (or
      // inside a hole) and so consume material of their own. Both are treated the same — a lead-in and a
      // lead-out are equally in the way.
      var toolPaths = part.Elements("Lead").Concat(part.Elements("Loop"))
        .Select(ReadOpenPath)
        .Where(c => c.Count >= 2)
        .ToList();

      return new SheetCamNestPart(
        (string)part.Attribute("Name") ?? string.Empty,
        positions,
        ReadPoints(part.Element("External")),
        part.Elements("Internal").Select(ReadPoints).Where(c => c.Count >= 3).ToList(),
        toolPaths);
    }

    private static SheetCamNestSheet ReadSheet(XElement sheet)
    {
      var points = ReadPoints(sheet.Element("External"));
      double w = points.Count == 0 ? 0 : points.Max(p => p.X) - points.Min(p => p.X);
      double h = points.Count == 0 ? 0 : points.Max(p => p.Y) - points.Min(p => p.Y);
      int quantity = (int)ReadDouble(sheet.Attribute("Quantity"));
      return new SheetCamNestSheet((string)sheet.Attribute("Name") ?? "Sheet", Math.Max(1, quantity), w, h, points);
    }

    /// <summary>Reads an open path (lead / loop) — no closing point to strip.</summary>
    private static IReadOnlyList<SvgPoint> ReadOpenPath(XElement path)
    {
      return path.Elements("Point")
        .Select(p => new SvgPoint(ReadDouble(p.Attribute("X")), ReadDouble(p.Attribute("Y"))))
        .ToList();
    }

    private static IReadOnlyList<SvgPoint> ReadPoints(XElement contour)
    {
      if (contour == null)
      {
        return Array.Empty<SvgPoint>();
      }

      var points = contour.Elements("Point")
        .Select(p => new SvgPoint(ReadDouble(p.Attribute("X")), ReadDouble(p.Attribute("Y"))))
        .ToList();

      // The contours are written closed (last point repeats the first); drop the repeat so the polygon
      // matches what the rest of the app expects.
      if (points.Count > 1 &&
          Math.Abs(points[0].X - points[points.Count - 1].X) < 1e-9 &&
          Math.Abs(points[0].Y - points[points.Count - 1].Y) < 1e-9)
      {
        points.RemoveAt(points.Count - 1);
      }

      return points;
    }

    private static double ReadDouble(XAttribute attribute)
    {
      return attribute != null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        ? value
        : 0d;
    }

    /// <summary>Shortest round-trippable form, invariant culture — the style SheetCam itself writes.</summary>
    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);
  }

  /// <summary>One placement of a part: millimetres, angle in radians anticlockwise, 0-based sheet index.</summary>
  public readonly struct SheetCamNestPosition
  {
    public SheetCamNestPosition(double x, double y, double a, int sheet)
    {
      this.X = x;
      this.Y = y;
      this.A = a;
      this.Sheet = sheet;
    }

    public double X { get; }

    public double Y { get; }

    public double A { get; }

    public int Sheet { get; }
  }

  /// <summary>A part in a .nest file: its geometry in millimetres and one entry per copy required.</summary>
  public sealed class SheetCamNestPart
  {
    public SheetCamNestPart(string name, IReadOnlyList<SheetCamNestPosition> positions, IReadOnlyList<SvgPoint> external, IReadOnlyList<IReadOnlyList<SvgPoint>> internals, IReadOnlyList<IReadOnlyList<SvgPoint>> toolPaths)
    {
      this.Name = name;
      this.Positions = positions;
      this.External = external;
      this.Internals = internals;
      this.ToolPaths = toolPaths;
    }

    public string Name { get; }

    /// <summary>Existing placements. In an un-nested file these are all X=0 Y=0 A=0.</summary>
    public IReadOnlyList<SheetCamNestPosition> Positions { get; }

    /// <summary>How many copies the job wants — one Position per copy.</summary>
    public int Quantity => this.Positions.Count;

    /// <summary>Outer contour, millimetres, open (no repeated closing point).</summary>
    public IReadOnlyList<SvgPoint> External { get; }

    /// <summary>Holes, millimetres.</summary>
    public IReadOnlyList<IReadOnlyList<SvgPoint>> Internals { get; }

    /// <summary>
    /// Lead-in / lead-out / loop paths, millimetres, in the same local frame as the contours. These are
    /// cut path rather than part outline: the outer ones reach beyond the contour and take material a
    /// neighbouring part cannot occupy.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<SvgPoint>> ToolPaths { get; }
  }

  /// <summary>A stock sheet declared by the job.</summary>
  public sealed class SheetCamNestSheet
  {
    public SheetCamNestSheet(string name, int quantity, double width, double height, IReadOnlyList<SvgPoint> outline)
    {
      this.Name = name;
      this.Quantity = quantity;
      this.Width = width;
      this.Height = height;
      this.Outline = outline;
    }

    public string Name { get; }

    public int Quantity { get; }

    /// <summary>Width in millimetres.</summary>
    public double Width { get; }

    /// <summary>Height in millimetres.</summary>
    public double Height { get; }

    public IReadOnlyList<SvgPoint> Outline { get; }
  }
}
