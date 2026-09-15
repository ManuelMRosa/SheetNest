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
    private static readonly ConcurrentDictionary<string, (long Ticks, byte[] Bytes, IReadOnlyList<DxfEntity> Marks)> Cache
      = new ConcurrentDictionary<string, (long, byte[], IReadOnlyList<DxfEntity>)>(StringComparer.OrdinalIgnoreCase);

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

      long ticks;
      try
      {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
          return Array.Empty<DxfEntity>();
        }

        ticks = file.LastWriteTimeUtc.Ticks;
      }
      catch (IOException)
      {
        return Array.Empty<DxfEntity>();
      }

      return Entry(path, ticks).Marks;
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
        var file = new FileInfo(path);
        if (!file.Exists)
        {
          return Array.Empty<DxfEntity>();
        }

        byte[] bytes = Entry(path, file.LastWriteTimeUtc.Ticks).Bytes;
        return bytes == null ? Array.Empty<DxfEntity>() : MarksIn(bytes);
      }
      catch (IOException)
      {
        return Array.Empty<DxfEntity>();
      }
    }

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
        var file = new FileInfo(path);
        if (!file.Exists)
        {
          return Array.Empty<DxfLayer>();
        }

        byte[] bytes = Entry(path, file.LastWriteTimeUtc.Ticks).Bytes;
        if (bytes == null)
        {
          return Array.Empty<DxfLayer>();
        }

        using (var stream = new MemoryStream(bytes))
        {
          DxfFile drawing = DxfFile.Load(stream);
          DxfLayerColors layers = DxfLayerColors.Of(drawing);
          var used = new HashSet<string>(
            drawing.Entities
              .Where(entity => DxfColorRoles.RoleOf(entity, layers) == DxfColorRole.PartMark)
              .Select(entity => string.IsNullOrWhiteSpace(entity.Layer) ? "0" : entity.Layer.Trim()),
            StringComparer.OrdinalIgnoreCase);

          return drawing.Layers.Where(layer => used.Contains(layer.Name)).ToList();
        }
      }
      catch (Exception)
      {
        return Array.Empty<DxfLayer>();
      }
    }

    private static (byte[] Bytes, IReadOnlyList<DxfEntity> Marks) Entry(string path, long ticks)
    {
      if (Cache.TryGetValue(path, out (long Ticks, byte[] Bytes, IReadOnlyList<DxfEntity> Marks) cached)
        && cached.Ticks == ticks)
      {
        return (cached.Bytes, cached.Marks);
      }

      byte[] bytes;
      try
      {
        bytes = DxfPartSplitter.ReadAllBytesShared(path);
      }
      catch (Exception)
      {
        // A drawing that cannot be read is refused by the import, which says why in words the operator
        // can act on. Asking what is engraved on it is not the place to repeat that.
        Cache[path] = (ticks, null, Array.Empty<DxfEntity>());
        return (null, Array.Empty<DxfEntity>());
      }

      IReadOnlyList<DxfEntity> marks = MarksIn(bytes);
      Cache[path] = (ticks, bytes, marks);
      return (bytes, marks);
    }

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
        .Where(entity => DxfColorRoles.RoleOf(entity, layers) == DxfColorRole.PartMark)
        .ToList();
    }
  }
}
