namespace DeepNestSharp
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Text.Json;

  /// <summary>
  /// Small per-user session file (%LOCALAPPDATA%\SheetNest\session.json): the sheet stock and the
  /// sheet edge margin survive closing and reopening the app.
  /// </summary>
  public class SessionState
  {
    /// <summary>Sheet edge margin at close; -1 = never saved (keep the app default).</summary>
    public double SheetEdgeMargin { get; set; } = -1;

    public List<SessionSheet> Sheets { get; set; } = new List<SessionSheet>();

    private static string FilePath => Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SheetNest", "session.json");

    public static SessionState? Load()
    {
      try
      {
        if (File.Exists(FilePath))
        {
          return JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath));
        }
      }
      catch
      {
        // A corrupt session file must never block startup — fall through to defaults.
      }

      return null;
    }

    public void Save()
    {
      try
      {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
      }
      catch
      {
        // Best effort — losing the session beats crashing on close.
      }
    }
  }

  public class SessionSheet
  {
    public int Width { get; set; }

    public int Height { get; set; }

    public int Quantity { get; set; }
  }
}
