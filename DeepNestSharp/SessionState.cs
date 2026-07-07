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

    /// <summary>Bend-allowance K-factor for 3D (STEP/IGES) unfold; -1 = never saved (keep default).</summary>
    public double UnfoldKFactor { get; set; } = -1;

    /// <summary>K-factor standard for 3D unfold ("ansi"/"din"); null/empty = keep default.</summary>
    public string? UnfoldKFactorStandard { get; set; }

    /// <summary>Optional override path to freecadcmd.exe (debug); null/empty = use the bundled copy.</summary>
    public string? FreeCadCmdPath { get; set; }

    /// <summary>Interpret unfolded 3D parts in inches (true) vs millimeters (false); null = keep default.</summary>
    public bool? UnfoldUnitInch { get; set; }

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
