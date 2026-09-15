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

      if (Cache.TryGetValue(path, out (long Ticks, IReadOnlyList<DxfEntity> Marks) cached) && cached.Ticks == ticks)
      {
        return cached.Marks;
      }

      IReadOnlyList<DxfEntity> marks = Read(path);
      Cache[path] = (ticks, marks);
      return marks;
    }

    private static IReadOnlyList<DxfEntity> Read(string path)
    {
      DxfFile drawing;
      try
      {
        using (var stream = new MemoryStream(DxfPartSplitter.ReadAllBytesShared(path)))
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
