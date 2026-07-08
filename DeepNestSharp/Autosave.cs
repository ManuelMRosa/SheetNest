namespace DeepNestSharp
{
  using System;
  using System.IO;
  using System.Text.Json;

  /// <summary>
  /// AutoCAD-style autosave: every few minutes the dirty project is snapshotted to
  /// %LOCALAPPDATA%\SheetNest\autosave\recovery.dnest. A clean save/close deletes the snapshot, so
  /// one existing at startup means the app died with unsaved work — offer to recover it.
  /// </summary>
  public static class Autosave
  {
    private static string Dir => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SheetNest", "autosave");

    public static string RecoveryPath => Path.Combine(Dir, "recovery.dnest");

    private static string MetaPath => Path.Combine(Dir, "recovery.meta.json");

    /// <summary>Snapshots the project json; remembers the original file so recovery can re-point it.</summary>
    public static void Write(string projectJson, string originalFilePath)
    {
      try
      {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(RecoveryPath, projectJson);
        File.WriteAllText(MetaPath, JsonSerializer.Serialize(new Meta
        {
          OriginalPath = originalFilePath ?? string.Empty,
          SavedAtLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        }));
      }
      catch
      {
        // autosave must never disturb the user
      }
    }

    /// <summary>True (with details) when a snapshot from a crashed session exists.</summary>
    public static bool TryGetPending(out string originalPath, out string savedAt)
    {
      originalPath = string.Empty;
      savedAt = string.Empty;
      try
      {
        if (!File.Exists(RecoveryPath))
        {
          return false;
        }

        if (File.Exists(MetaPath))
        {
          var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath));
          originalPath = meta?.OriginalPath ?? string.Empty;
          savedAt = meta?.SavedAtLocal ?? string.Empty;
        }

        return true;
      }
      catch
      {
        return false;
      }
    }

    /// <summary>Removes the snapshot (clean save/close, or the user declined recovery).</summary>
    public static void Clear()
    {
      try
      {
        if (File.Exists(RecoveryPath))
        {
          File.Delete(RecoveryPath);
        }

        if (File.Exists(MetaPath))
        {
          File.Delete(MetaPath);
        }
      }
      catch
      {
        // best effort
      }
    }

    private sealed class Meta
    {
      public string OriginalPath { get; set; }

      public string SavedAtLocal { get; set; }
    }
  }
}
