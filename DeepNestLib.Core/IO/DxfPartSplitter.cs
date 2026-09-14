namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text;
  using DeepNestLib.Geometry;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// One part found inside a drawing: the entities that draw it, plus whatever its label said.
  /// </summary>
  public sealed class DxfPart
  {
    internal DxfPart(IReadOnlyList<DxfEntity> entities, IReadOnlyCollection<int> entityIndexes, int holeCount, string name, int quantity)
    {
      this.Entities = entities;
      this.EntityIndexes = entityIndexes;
      this.HoleCount = holeCount;
      this.Name = name;
      this.Quantity = quantity;
    }

    /// <summary>Its outer contour and its holes, as the ORIGINAL entities. Arcs and splines are carried
    /// through untouched: the common-line planner reads them out of the source schema, so a part written
    /// back as tessellated line segments would lose the ability to share a cut on a radius.</summary>
    public IReadOnlyList<DxfEntity> Entities { get; }

    /// <summary>Where this part's entities sit in the drawing's own entity list. Writing reloads the
    /// drawing, so the entity OBJECTS above are no longer the ones being written and only their
    /// position identifies them.</summary>
    public IReadOnlyCollection<int> EntityIndexes { get; }

    /// <summary>How many holes are cut in it. Worth stating on its own: how many ENTITIES a part holds
    /// depends on how it was drawn, but how many holes it has is what the split had to get right.</summary>
    public int HoleCount { get; }

    /// <summary>The name off the part's label, or null when it carries none.</summary>
    public string Name { get; }

    /// <summary>The quantity off the part's label, or 0 when it carries none.</summary>
    public int Quantity { get; }
  }

  /// <summary>
  /// Splits a drawing that holds several parts into one entry per part, and reads the label drawn
  /// inside each.
  /// </summary>
  /// <remarks>
  /// <para>A part is a closed contour with no other contour around it; a contour inside one is a HOLE in
  /// it. That is the distinction the importer never made: it kept the biggest contour by area and turned
  /// every other one into a hole, so a file of nine parts read as one part with eight holes. Those
  /// phantom holes are not only a wrong part count - hole filling drops leftover parts into them, which
  /// puts a part on top of solid material.</para>
  /// <para>Containment is counted by depth so it nests to any level: a part sitting inside another
  /// part's hole is a part in its own right, not a hole of a hole.</para>
  /// <para>Labels: a text whose insertion point falls inside a part names it, written NAME,QUANTITY. A
  /// part with no label keeps the caller's numbering, which is what a drawing that was never annotated
  /// needs.</para>
  /// </remarks>
  public static class DxfPartSplitter
  {
    /// <summary>
    /// The parts in a drawing, outermost first. A drawing holding one part returns one entry, so a
    /// caller can treat "did this split" as <c>Count &gt; 1</c>.
    /// </summary>
    public static IReadOnlyList<DxfPart> Split(
      string name,
      IEnumerable<DxfEntity> entities,
      DxfLayerColors layers = null)
    {
      var all = (entities ?? Array.Empty<DxfEntity>()).ToList();

      // The WHOLE drawing stays as the index basis: a part is written out by keeping its entities by
      // position, so dropping the marks from this list would shift every index and write the wrong
      // entities into every part. Only what gets measured into contours is filtered.
      RawDetail<DxfEntity> detail = DxfParser.ConvertDxfToRawDetail(name, DxfColorRoles.Cuttable(all, layers));

      var contours = detail.Outers.OfType<LocalContour<DxfEntity>>().ToList();
      var rings = contours.Select(ToPolygon).ToList();

      // Depth is how many other contours enclose this one: even means material, odd means a hole in the
      // one immediately around it. Counting rather than pairing is what makes a part inside a hole come
      // out as its own part instead of disappearing into its host.
      var depth = new int[contours.Count];
      for (int i = 0; i < contours.Count; i++)
      {
        for (int j = 0; j < contours.Count; j++)
        {
          if (i != j && Encloses(rings[j], rings[i]))
          {
            depth[i]++;
          }
        }
      }

      // Where each entity sits in the drawing, so a part can still be identified after the drawing has
      // been reloaded and its entity objects are different ones.
      var indexOf = new Dictionary<DxfEntity, int>();
      for (int i = 0; i < all.Count; i++)
      {
        indexOf[all[i]] = i;
      }

      var parts = new List<Island>();
      for (int i = 0; i < contours.Count; i++)
      {
        if (depth[i] % 2 == 0)
        {
          parts.Add(new Island(contours[i].Entities, rings[i], indexOf));
        }
      }

      // Each hole joins the SMALLEST part that encloses it, which is the one it is actually a hole in.
      // Against the largest it would land on the wrong part whenever parts are nested inside each other.
      for (int i = 0; i < contours.Count; i++)
      {
        if (depth[i] % 2 == 0)
        {
          continue;
        }

        Island host = null;
        foreach (Island candidate in parts)
        {
          if (Encloses(candidate.Outer, rings[i]) && (host == null || candidate.Area < host.Area))
          {
            host = candidate;
          }
        }

        if (host != null)
        {
          host.Add(contours[i].Entities, indexOf);
          host.HoleCount++;
        }
      }

      var labels = ReadLabels(all);
      var ordered = parts.OrderByDescending(p => p.Area).ToList();
      IReadOnlyList<(string Name, int Quantity)> named = AssignLabels(ordered, labels);
      var result = new List<DxfPart>();
      for (int i = 0; i < ordered.Count; i++)
      {
        Island part = ordered[i];
        result.Add(new DxfPart(part.Entities, part.Indexes, part.HoleCount, named[i].Name, named[i].Quantity));
      }

      return result;
    }

    /// <summary>
    /// Writes one DXF per part when a drawing holds several, and says what each one is called and how
    /// many are wanted. Returns EMPTY for a drawing that holds one part: that file is already the part,
    /// so the caller keeps the operator's own path and nothing is written or renamed.
    /// </summary>
    /// <remarks>
    /// The files go to a temp folder keyed on the source, the same way 3D unfolding caches its flats.
    /// Nothing is written next to the operator's drawing. A saved project therefore points at temp, so
    /// <see cref="Rebuild"/> exists to put a part back when the temp folder has been cleaned.
    /// </remarks>
    public static IReadOnlyList<SplitPartFile> WriteParts(string sourcePath)
    {
      // Read the bytes once and parse them again for every part. Writing several files out of ONE
      // loaded drawing does not work: entities that have been saved once carry a handle and an owner,
      // and putting them back into the same collection does not put them back in the output - seven of
      // eight parts came out with an empty entities section that way, and nesting refused each of them.
      byte[] bytes = ReadAllBytesShared(sourcePath);
      DxfFile drawing = Reload(bytes);
      IReadOnlyList<DxfPart> parts = Split(sourcePath, drawing.Entities, DxfLayerColors.Of(drawing));
      if (parts.Count < 2)
      {
        return Array.Empty<SplitPartFile>();
      }

      string directory = CacheDir(sourcePath);
      Directory.CreateDirectory(directory);

      string stem = Path.GetFileNameWithoutExtension(sourcePath);
      var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var written = new List<SplitPartFile>();
      for (int i = 0; i < parts.Count; i++)
      {
        string name = UniqueName(FileNameFor(parts[i].Name, stem, i), used);
        string path = Path.Combine(directory, name + ".dxf");
        Write(bytes, parts[i], path);
        written.Add(new SplitPartFile(path, i, parts[i].Quantity <= 0 ? 1 : parts[i].Quantity));
      }

      return written;
    }

    /// <summary>
    /// Puts one part's file back from its source drawing, for a saved project whose temp folder has
    /// been cleaned since. Null when the source is gone too, or no longer holds that part.
    /// </summary>
    public static string Rebuild(string sourcePath, int partIndex)
    {
      try
      {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
          return null;
        }

        IReadOnlyList<SplitPartFile> parts = WriteParts(sourcePath);
        return parts.FirstOrDefault(p => p.PartIndex == partIndex)?.Path;
      }
      catch (Exception)
      {
        // A drawing that has been edited or removed since must not stop a project opening. The part
        // reads as missing, which the parts list already shows.
        return null;
      }
    }

    /// <summary>
    /// Writes one part by emptying the drawing it came out of and leaving only that part in it, rather
    /// than by building a new drawing around its entities.
    /// </summary>
    /// <remarks>
    /// A new drawing would come back saying nothing about its units, and that is not cosmetic:
    /// <c>DxfContourCatalog</c> scales the tolerance it joins contours with by the drawing's units, so a
    /// metric part read as inches gets a tolerance twenty five times too tight and common cutting fails
    /// on it in silence. Reusing the source carries the units, the drawing version and the layer tables
    /// across without having to know which of them anything downstream depends on.
    /// </remarks>
    private static void Write(byte[] source, DxfPart part, string path)
    {
      DxfFile file = Reload(source);
      var keep = new HashSet<int>(part.EntityIndexes);
      List<DxfEntity> entities = file.Entities.ToList();
      for (int i = entities.Count - 1; i >= 0; i--)
      {
        if (!keep.Contains(i))
        {
          file.Entities.Remove(entities[i]);
        }
      }

      file.Save(path);
    }

    /// <summary>
    /// Reads the drawing while somebody else has it open. An operator importing a DXF very often has
    /// that same DXF open in their CAD program, and the plain read refuses a file held there; the rest
    /// of the loading code already shares, so this has to as well.
    /// </summary>
    private static byte[] ReadAllBytesShared(string path)
    {
      using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
      using (var buffer = new MemoryStream())
      {
        stream.CopyTo(buffer);
        return buffer.ToArray();
      }
    }

    /// <summary>A fresh reading of the same bytes, so each part is written out of entities that have
    /// never been saved before. The parse is deterministic, which is what lets a part be identified by
    /// the position of its entities rather than by the objects themselves.</summary>
    private static DxfFile Reload(byte[] source)
    {
      using (var stream = new MemoryStream(source))
      {
        return DxfFile.Load(stream);
      }
    }

    /// <summary>The part's label makes the file name, because a row is named after its file. Without a
    /// label it falls back to the drawing's own name and a number, which is what a drawing that was
    /// never annotated needs.</summary>
    private static string FileNameFor(string label, string stem, int index)
    {
      string candidate = Sanitise(label);
      return candidate.Length > 0 ? candidate : $"{Sanitise(stem)} #{index + 1}";
    }

    private static string Sanitise(string name)
    {
      if (string.IsNullOrWhiteSpace(name))
      {
        return string.Empty;
      }

      var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
      var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

      // A name is only ever used as a file name here, so one that survives sanitising as nothing, or as
      // something Windows will not store, has to give way to the numbered fallback.
      return cleaned.Trim('.').Length == 0 ? string.Empty : cleaned.Substring(0, Math.Min(cleaned.Length, 100));
    }

    /// <summary>Two parts labelled the same would otherwise overwrite each other, and the second one
    /// would silently become a copy of the first.</summary>
    private static string UniqueName(string name, HashSet<string> used)
    {
      if (used.Add(name))
      {
        return name;
      }

      for (int suffix = 2; ; suffix++)
      {
        string candidate = $"{name} #{suffix}";
        if (used.Add(candidate))
        {
          return candidate;
        }
      }
    }

    // Stable per-file temp dir. string.GetHashCode is randomised per process in .NET 6, so the folder
    // has to come from a real hash or a reopened project would look in the wrong place. The write time
    // is in the key so an edited drawing splits again instead of reusing yesterday's parts.
    private static string CacheDir(string sourcePath)
    {
      long ticks = 0;
      try
      {
        ticks = new FileInfo(sourcePath).LastWriteTimeUtc.Ticks;
      }
      catch (IOException)
      {
        // An unreadable timestamp only costs a fresh split.
      }

      byte[] bytes = Encoding.UTF8.GetBytes(sourcePath + "|" + ticks.ToString(CultureInfo.InvariantCulture));
      string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Substring(0, 16);
      return Path.Combine(Path.GetTempPath(), "sheetnest_split", hash);
    }

    /// <summary>One part written out: where it is, which part of its drawing it was, and how many the
    /// label asked for.</summary>
    public sealed class SplitPartFile
    {
      internal SplitPartFile(string path, int partIndex, int quantity)
      {
        this.Path = path;
        this.PartIndex = partIndex;
        this.Quantity = quantity;
      }

      public string Path { get; }

      public int PartIndex { get; }

      public int Quantity { get; }
    }

    /// <summary>One outer contour while its holes are still being gathered onto it.</summary>
    private sealed class Island
    {
      internal Island(IEnumerable<DxfEntity> entities, INfp outer, IReadOnlyDictionary<DxfEntity, int> indexOf)
      {
        this.Entities = new List<DxfEntity>();
        this.Indexes = new HashSet<int>();
        this.Outer = outer;
        this.Area = outer.Area;
        this.Add(entities, indexOf);
      }

      internal List<DxfEntity> Entities { get; }

      internal HashSet<int> Indexes { get; }

      internal void Add(IEnumerable<DxfEntity> entities, IReadOnlyDictionary<DxfEntity, int> indexOf)
      {
        foreach (DxfEntity entity in entities)
        {
          this.Entities.Add(entity);
          if (indexOf.TryGetValue(entity, out int index))
          {
            this.Indexes.Add(index);
          }
        }
      }

      internal INfp Outer { get; }

      internal double Area { get; }

      internal int HoleCount { get; set; }
    }

    /// <summary>
    /// Splits a label into its name and quantity. Everything before the LAST comma is the name, so a
    /// name may hold commas itself; the tail has to be a positive whole number or the whole text is
    /// taken as the name and the quantity is left unsaid. Guessing a quantity out of something that is
    /// not a number would put the wrong count of parts on the sheet.
    /// </summary>
    internal static (string Name, int Quantity) ParseLabel(string text)
    {
      string trimmed = (text ?? string.Empty).Trim();
      if (trimmed.Length == 0)
      {
        return (null, 0);
      }

      int comma = trimmed.LastIndexOf(',');
      if (comma > 0 && comma < trimmed.Length - 1)
      {
        string tail = trimmed.Substring(comma + 1).Trim();
        if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out int quantity)
          && quantity > 0)
        {
          string head = trimmed.Substring(0, comma).Trim();
          if (head.Length > 0)
          {
            return (head, quantity);
          }
        }
      }

      return (trimmed, 0);
    }

    /// <summary>
    /// Which label names each part, and how many it asks for. Decided for all the parts at once,
    /// because two of the three rules cannot be answered one part at a time.
    /// </summary>
    /// <remarks>
    /// <para>A label belongs to the SMALLEST part that holds it. A part sitting inside another part's
    /// hole is inside both outlines, and the label drawn on it names the small one; asked part by part,
    /// the big one claimed it too and was cut under a name and a quantity that were never its own.</para>
    /// <para>A text that turns up inside more than one part cannot tell parts apart: it is the drawing
    /// number, or a note written on every part. It names none of them. Should that leave a part with
    /// nothing, it gets its labels back - four parts the operator really did call BRACKET are four
    /// parts called BRACKET, not four parts left unnamed.</para>
    /// <para>What is left is settled by distance to the middle of the part, because a label put there
    /// to name the part is drawn on the part, while a stale mark or a note sits off in a corner. The
    /// order the entities happen to sit in the file decided this until now, and that is not something
    /// the operator can see or control: the same drawing named its parts differently depending on how
    /// the CAD program had saved it.</para>
    /// </remarks>
    private static IReadOnlyList<(string Name, int Quantity)> AssignLabels(
      IReadOnlyList<Island> parts,
      IReadOnlyList<(SvgPoint At, string Text)> labels)
    {
      var owned = new List<List<int>>();
      for (int i = 0; i < parts.Count; i++)
      {
        owned.Add(new List<int>());
      }

      for (int l = 0; l < labels.Count; l++)
      {
        int owner = -1;
        for (int p = 0; p < parts.Count; p++)
        {
          if (Encloses(parts[p].Outer, labels[l].At) && (owner < 0 || parts[p].Area < parts[owner].Area))
          {
            owner = p;
          }
        }

        if (owner >= 0)
        {
          owned[owner].Add(l);
        }
      }

      // How many parts hold a text that READS the same. Counted on the words rather than on the entity,
      // because a drawing number is written once on each part: four separate texts that all say 12345.
      var partsHolding = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      foreach (List<int> held in owned)
      {
        foreach (string text in held.Select(l => labels[l].Text.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
          partsHolding[text] = partsHolding.TryGetValue(text, out int count) ? count + 1 : 1;
        }
      }

      var named = new List<(string Name, int Quantity)>();
      for (int p = 0; p < parts.Count; p++)
      {
        List<int> candidates = owned[p].Where(l => partsHolding[labels[l].Text.Trim()] == 1).ToList();
        if (candidates.Count == 0)
        {
          candidates = owned[p];
        }

        named.Add(candidates.Count == 0 ? (null, 0) : ParseLabel(labels[Nearest(candidates, parts[p], labels)].Text));
      }

      return named;
    }

    /// <summary>The label nearest the middle of the part. A tie goes to the one the drawing lists
    /// first, so that a drawing always answers the same rather than however the sort happened to
    /// settle.</summary>
    private static int Nearest(
      IReadOnlyList<int> candidates,
      Island part,
      IReadOnlyList<(SvgPoint At, string Text)> labels)
    {
      double centreX = (part.Outer.MinX + part.Outer.MaxX) / 2;
      double centreY = (part.Outer.MinY + part.Outer.MaxY) / 2;
      int nearest = candidates[0];
      double shortest = double.MaxValue;
      foreach (int l in candidates)
      {
        double dx = labels[l].At.X - centreX;
        double dy = labels[l].At.Y - centreY;
        double distance = (dx * dx) + (dy * dy);
        if (distance < shortest)
        {
          shortest = distance;
          nearest = l;
        }
      }

      return nearest;
    }

    private static IReadOnlyList<(SvgPoint At, string Text)> ReadLabels(IEnumerable<DxfEntity> entities)
    {
      var labels = new List<(SvgPoint At, string Text)>();
      foreach (DxfEntity entity in entities)
      {
        switch (entity)
        {
          case DxfText text when !string.IsNullOrWhiteSpace(text.Value):
            labels.Add((new SvgPoint(text.Location.X, text.Location.Y), text.Value));
            break;
          case DxfMText mtext when !string.IsNullOrWhiteSpace(mtext.Text):
            labels.Add((new SvgPoint(mtext.InsertionPoint.X, mtext.InsertionPoint.Y), mtext.Text));
            break;
          default:
            break;
        }
      }

      return labels;
    }

    /// <summary>Whether <paramref name="inner"/> lies within <paramref name="outer"/>. Asked of a vertex
    /// at a time until one gives a straight answer: a vertex that lands exactly on the other ring cannot
    /// decide, and two contours that share a corner are common enough to matter.</summary>
    private static bool Encloses(INfp outer, INfp inner)
    {
      foreach (SvgPoint point in inner.Points)
      {
        bool? inside = GeometryUtil.PointInPolygon(point, outer);
        if (inside.HasValue)
        {
          return inside.Value;
        }
      }

      return false;
    }

    private static bool Encloses(INfp outer, SvgPoint point)
      => GeometryUtil.PointInPolygon(point, outer) == true;

    private static INfp ToPolygon(LocalContour<DxfEntity> contour)
    {
      var polygon = new NoFitPolygon();
      foreach (System.Drawing.PointF point in contour.Points)
      {
        polygon.AddPoint(new SvgPoint(point.X, point.Y));
      }

      return polygon;
    }
  }
}
