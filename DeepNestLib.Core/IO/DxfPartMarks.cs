namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Concurrent;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// What is engraved on a part, read from the part's own file.
  /// </summary>
  /// <remarks>
  /// <para>Marks are read back out of the file rather than carried along with the part, because nothing
  /// a part turns into on the way to the screen has room for them: a part becomes an
  /// <see cref="INfp"/> of points, and the entities are gone by then. Everything that has to draw or
  /// write a mark does hold the part's PATH, and the exporter already reloads the file from it, so the
  /// file is the one place they all agree on.</para>
  /// <para>Cached on the path and the time the file was last written, because a viewer asks this on
  /// every redraw and reading a drawing off the disk each time would be felt.</para>
  /// </remarks>
  public static class DxfPartMarks
  {
    /// <summary>
    /// What a mark is allowed to be drawn with: everything the exporter knows how to move onto a sheet.
    /// Anything else - a block, a solid, a bare point - is left out here rather than further down, where
    /// the exporter refuses an entity it cannot transform and takes the whole sheet with it. A yellow
    /// centre point in one part should not be the reason a nest does not come out.
    /// </summary>
    private static readonly HashSet<DxfEntityType> Movable = new HashSet<DxfEntityType>
    {
      DxfEntityType.Arc, DxfEntityType.ArcAlignedText, DxfEntityType.Attribute,
      DxfEntityType.AttributeDefinition, DxfEntityType.Circle, DxfEntityType.Ellipse,
      DxfEntityType.Image, DxfEntityType.Leader, DxfEntityType.Line, DxfEntityType.LwPolyline,
      DxfEntityType.MLine, DxfEntityType.Polyline, DxfEntityType.Spline, DxfEntityType.Text,
      DxfEntityType.MText,
    };

    private static readonly ConcurrentDictionary<string, (long Ticks, IReadOnlyList<DxfEntity> Marks)> Cache
      = new ConcurrentDictionary<string, (long, IReadOnlyList<DxfEntity>)>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What is engraved on the part in this file. Empty for a part with nothing marked on it, and for a
    /// file that cannot be read: a drawing that is no good is refused by the import, which says why in
    /// words the operator can act on, and asking what is engraved on it is not the place to repeat that.
    /// </summary>
    public static IReadOnlyList<DxfEntity> For(string path)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        return Array.Empty<DxfEntity>();
      }

      long? ticks = WrittenAt(path);
      return ticks == null ? Array.Empty<DxfEntity>() : Entry(path, ticks.Value);
    }

    /// <summary>
    /// The same marks, as a set of entities nobody else holds. Anything that is going to MOVE a mark has
    /// to ask for these: the ones <see cref="For"/> hands out are shared, and the exporter transforms
    /// entities in place, so it would otherwise leave every later reader a part's mark already rotated
    /// onto a sheet.
    /// </summary>
    public static IReadOnlyList<DxfEntity> Fresh(string path)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        return Array.Empty<DxfEntity>();
      }

      try
      {
        return MarksIn(DxfPartSplitter.ReadAllBytesShared(path));
      }
      catch (Exception)
      {
        return Array.Empty<DxfEntity>();
      }
    }

    /// <summary>When this file was last written, or null when there is no reading it. A path can be
    /// malformed as well as missing, and either way the import is what refuses the drawing and says
    /// why; asking what is engraved on it must not throw in the middle of a repaint.</summary>
    private static long? WrittenAt(string path)
    {
      try
      {
        var file = new FileInfo(path);
        return file.Exists ? file.LastWriteTimeUtc.Ticks : (long?)null;
      }
      catch (Exception)
      {
        return null;
      }
    }

    /// <summary>
    /// Which way engraved text reads once its part has been placed, and whether it now hangs from its
    /// other end.
    /// </summary>
    /// <remarks>
    /// <para>The line the text sits on follows the part, mirror included: reflecting a direction maps
    /// its angle to 180 minus itself, the same way it does for an arc's sweep. What must NOT follow the
    /// part is the letters themselves. A DXF does not reflect glyphs, so reflecting the line alone
    /// leaves text reading backwards along it, which on a part number is the difference between a part
    /// and a piece of scrap.</para>
    /// <para>So when the reflected line would have the words running away from the reader, the text is
    /// turned the other half turn to face them again and hung from its other end, which puts the same
    /// words in the same place the right way round.</para>
    /// <para>Here rather than in the exporter because the screen has to show a mark the way it is going
    /// to be cut.</para>
    /// </remarks>
    public static (double Angle, bool Flip) TextPlacement(bool mirror, double rotationAngle, double own)
    {
      if (!mirror)
      {
        return (own + rotationAngle, false);
      }

      double reflected = Normalise(180.0 - own + rotationAngle);
      return Readable(reflected) ? (reflected, false) : (Normalise(reflected + 180.0), true);
    }

    /// <summary>An angle in [0, 360).</summary>
    public static double Normalise(double angle) => ((angle % 360.0) + 360.0) % 360.0;

    /// <summary>Whether text written at this angle reads without turning the sheet round.</summary>
    public static bool Readable(double angle) => angle <= 90.0 || angle >= 270.0;

    /// <summary>
    /// Whether this text is stretched between its two points rather than hung from one of them.
    /// </summary>
    /// <remarks>Aligned and fit text has no anchor and no angle of its own: the two points are both
    /// real ends of the run, and between them they say where it sits and which way it reads.</remarks>
    public static bool IsStretched(DxfText text)
      => text.HorizontalTextJustification == DxfHorizontalTextJustification.Aligned
        || text.HorizontalTextJustification == DxfHorizontalTextJustification.Fit;

    /// <summary>Which way a run of stretched text reads, in degrees, from the two points it spans.
    /// </summary>
    public static double RunAngle(DxfPoint from, DxfPoint to)
      => Normalise(Math.Atan2(to.Y - from.Y, to.X - from.X) * 180.0 / Math.PI);

    /// <summary>
    /// The point a text hangs from: the first one only when it is left justified off the baseline, the
    /// second one in every other case.
    /// </summary>
    /// <remarks>
    /// <para>A CAD centring a text leaves the first point at nothing and puts the anchor that counts in
    /// the second, so reading the first asks about the drawing origin instead of about the part. That
    /// is far enough away to lose the text altogether: the splitter decides which part a text is on by
    /// testing whether its point falls inside one, and a text that falls inside nothing is dropped from
    /// every part file written.</para>
    /// <para>Here rather than in any one caller because four places need this rule - the splitter for
    /// marks and for names, the exporter, and the screen - and it has already gone out of step once.
    /// </para>
    /// <para>Aligned and fit text is stretched BETWEEN its two points, so the second is the far end of
    /// the run rather than somewhere the text hangs from. It is still on the part, which is all the
    /// splitter is asking; the exporter treats that case separately.</para>
    /// </remarks>
    public static DxfPoint AnchorOf(DxfText text)
      => AnchorOf(
        text.Location, text.SecondAlignmentPoint,
        text.HorizontalTextJustification, text.VerticalTextJustification);

    /// <summary>The same rule for an attribute, which carries the same two points and the same pair of
    /// justifications without sharing a base class with a text.</summary>
    public static DxfPoint AnchorOf(DxfAttribute attribute)
      => AnchorOf(
        attribute.Location, attribute.SecondAlignmentPoint,
        attribute.HorizontalTextJustification, attribute.VerticalTextJustification);

    /// <inheritdoc cref="AnchorOf(DxfAttribute)"/>
    public static DxfPoint AnchorOf(DxfAttributeDefinition definition)
      => AnchorOf(
        definition.Location, definition.SecondAlignmentPoint,
        definition.HorizontalTextJustification, definition.VerticalTextJustification);

    private static DxfPoint AnchorOf(
      DxfPoint location,
      DxfPoint second,
      DxfHorizontalTextJustification horizontal,
      DxfVerticalTextJustification vertical)
      => horizontal == DxfHorizontalTextJustification.Left
        && vertical == DxfVerticalTextJustification.Baseline
        ? location
        : second;

    /// <summary>
    /// The layers the marks in this file are drawn on, as the drawing declares them. A shop that colours
    /// its layers rather than its lines leaves every mark saying "whatever my layer is", so writing the
    /// mark out without its layer would lose the colour that says it is a mark at all.
    /// </summary>
    public static IReadOnlyList<DxfLayer> LayersFor(string path)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        return Array.Empty<DxfLayer>();
      }

      try
      {
        using (var stream = new MemoryStream(DxfPartSplitter.ReadAllBytesShared(path)))
        {
          DxfFile drawing = DxfFile.Load(stream);
          DxfLayerColors layers = DxfLayerColors.Of(drawing);
          var used = new HashSet<string>(
            drawing.Entities
              .Where(entity => DxfColorRoles.RoleOf(entity, layers) == DxfColorRole.PartMark)
              .Select(entity => Named(entity.Layer)),
            StringComparer.OrdinalIgnoreCase);

          // Compared on the trimmed name at both ends: a layer called "ENGRAVE " with a space on it is
          // the same layer as the one its marks say they are on.
          return drawing.Layers.Where(layer => used.Contains(Named(layer.Name))).ToList();
        }
      }
      catch (Exception)
      {
        return Array.Empty<DxfLayer>();
      }
    }

    private static IReadOnlyList<DxfEntity> Entry(string path, long ticks)
    {
      if (Cache.TryGetValue(path, out (long Ticks, IReadOnlyList<DxfEntity> Marks) cached) && cached.Ticks == ticks)
      {
        return cached.Marks;
      }

      IReadOnlyList<DxfEntity> marks;
      try
      {
        marks = MarksIn(DxfPartSplitter.ReadAllBytesShared(path));
      }
      catch (Exception)
      {
        // A drawing that cannot be read is refused by the import, which says why in words the operator
        // can act on. Asking what is engraved on it is not the place to repeat that.
        marks = Array.Empty<DxfEntity>();
      }

      Cache[path] = (ticks, marks);
      return marks;
    }

    private static string Named(string layer) => string.IsNullOrWhiteSpace(layer) ? "0" : layer.Trim();

    private static IReadOnlyList<DxfEntity> MarksIn(byte[] bytes)
    {
      DxfFile drawing;
      try
      {
        using (var stream = new MemoryStream(bytes))
        {
          drawing = DxfFile.Load(stream);
        }
      }
      catch (Exception)
      {
        return Array.Empty<DxfEntity>();
      }

      DxfLayerColors layers = DxfLayerColors.Of(drawing);
      return drawing.Entities
        .Where(entity => Movable.Contains(entity.EntityType)
          && DxfColorRoles.RoleOf(entity, layers) == DxfColorRole.PartMark)
        .ToList();
    }
  }
}
