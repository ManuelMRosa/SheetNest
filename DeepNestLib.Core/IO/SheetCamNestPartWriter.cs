namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// Materialises the parts of a <see cref="SheetCamNestFile"/> as temporary DXF files.
  /// <para>
  /// The app identifies a part by a path on disk: <c>DetailLoadInfo.Path</c> is the only geometry
  /// reference, the nest button is gated on the file existing, thumbnails need a .dxf, and the DXF
  /// exporter silently drops any polygon whose name is not a .dxf. Writing the geometry out is far
  /// cheaper than teaching all four about file-less parts, and it is what the 3D STEP import already
  /// does (see <see cref="StepUnfoldService"/>).
  /// </para>
  /// <para>
  /// Coordinates are preserved exactly (only scaled), so a part's local frame still matches the one the
  /// .nest positions are expressed in. <c>RawDetail.ToNfp</c> copies points verbatim, so the frame
  /// survives the trip back in as well — which is what makes placements directly writable as positions.
  /// </para>
  /// </summary>
  public static class SheetCamNestPartWriter
  {
    /// <summary>
    /// Writes every part of the file to its own DXF and returns them in file order. The directory is
    /// derived from the source path, its timestamp and the scale, so re-importing the same file reuses
    /// the same DXFs and an edited file gets fresh ones.
    /// </summary>
    /// <param name="nest">Parsed source file.</param>
    /// <param name="nestPath">Path the file was read from — identifies the cache directory.</param>
    /// <param name="scale">Millimetres to drawing units, i.e. 1/25.4 for an inch drawing.</param>
    public static IReadOnlyList<(SheetCamNestPart Part, string DxfPath)> WriteAll(SheetCamNestFile nest, string nestPath, double scale)
    {
      var dir = CacheDir(nestPath, scale);
      Directory.CreateDirectory(dir);

      var results = new List<(SheetCamNestPart, string)>();
      var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var part in nest.Parts)
      {
        var name = UniqueFileName(part.Name, used);
        var path = Path.Combine(dir, name + ".dxf");
        Write(part, path, scale);
        results.Add((part, path));
      }

      return results;
    }

    /// <summary>
    /// Re-creates a single part's DXF at <paramref name="dxfPath"/> from its source file. The temp
    /// directory is not ours to keep — Windows reclaims it — so a project saved and reopened later has to
    /// be able to rebuild, exactly as a 3D-unfolded part re-unfolds from its STEP.
    /// </summary>
    /// <returns>True when the part was found and written.</returns>
    public static bool TryRebuild(string nestPath, string partName, double scale, string dxfPath)
    {
      try
      {
        var nest = SheetCamNestFile.Load(nestPath);
        var part = nest.Parts.FirstOrDefault(p => string.Equals(p.Name, partName, StringComparison.Ordinal));
        if (part == null)
        {
          return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dxfPath));
        Write(part, dxfPath, scale);
        return true;
      }
      catch
      {
        // A missing/edited source file just leaves the part unloadable, same as any other missing part.
        return false;
      }
    }

    /// <summary>Writes one part — outer contour plus holes — as closed LWPOLYLINEs.</summary>
    public static void Write(SheetCamNestPart part, string dxfPath, double scale)
    {
      var file = new DxfFile();

      // R12 cannot hold LWPOLYLINE; the rest of the app writes R2000 for the same reason.
      file.Header.Version = DxfAcadVersion.R2000;
      file.Views.Clear();

      AddContour(file, part.External, scale);
      foreach (var hole in part.Internals)
      {
        AddContour(file, hole, scale);
      }

      file.Save(dxfPath, true);
    }

    private static void AddContour(DxfFile file, IReadOnlyList<SvgPoint> points, double scale)
    {
      if (points == null || points.Count < 3)
      {
        return;
      }

      var polyline = new DxfLwPolyline(points.Select(p => new DxfLwPolylineVertex { X = p.X * scale, Y = p.Y * scale }))
      {
        IsClosed = true,
      };

      file.Entities.Add(polyline);
    }

    /// <summary>
    /// Stable per-(file + scale) temp directory. As in <see cref="StepUnfoldService"/> this hashes with
    /// SHA256 rather than string.GetHashCode, which is randomised per process in .NET 6 and so would not
    /// resolve to the same directory on the next run.
    /// </summary>
    private static string CacheDir(string nestPath, double scale)
    {
      long ticks = 0;
      try
      {
        ticks = File.GetLastWriteTimeUtc(nestPath).Ticks;
      }
      catch
      {
        // missing file -> ticks 0; loading would have failed already
      }

      var key = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}", nestPath, ticks, scale);
      var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key))).Substring(0, 16);
      return Path.Combine(Path.GetTempPath(), "sheetnest_nest", hash);
    }

    /// <summary>Part names are free text; make them safe as filenames and unique within the directory.</summary>
    private static string UniqueFileName(string partName, HashSet<string> used)
    {
      var safe = new StringBuilder();
      foreach (var c in partName ?? string.Empty)
      {
        safe.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
      }

      var baseName = safe.ToString().Trim();
      if (baseName.Length == 0)
      {
        baseName = "part";
      }

      var candidate = baseName;
      for (int i = 2; !used.Add(candidate); i++)
      {
        candidate = baseName + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
      }

      return candidate;
    }
  }
}
