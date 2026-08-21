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

    /// <summary>Most-recently-used project files, newest first (File > Recent Projects).</summary>
    public List<string> RecentProjects { get; set; } = new List<string>();

    /// <summary>Autosave the open project (auto-save workspace); null = default (enabled).</summary>
    public bool? AutosaveEnabled { get; set; }

    /// <summary>Minutes between autosaves; -1 = default (5).</summary>
    public int AutosaveMinutes { get; set; } = -1;

    /// <summary>Global default rotation code for unedited parts (1/2/4/8/36); -1 = default (4).</summary>
    public int DefaultRotations { get; set; } = -1;

    /// <summary>Drawing units are millimeters (true) vs inches (false); null = default (inches).</summary>
    public bool? UnitsMm { get; set; }

    /// <summary>User-saved sheet size presets ("My sheets" in the Add Sheet menu); Quantity unused.</summary>
    public List<SessionSheet> SheetPresets { get; set; } = new List<SessionSheet>();

    /// <summary>How hard the nester searches each sheet: 0 fast, 1 normal, 2 best; -1 = default (fast).
    /// Kept per user and not in the project, because it is a preference about this operator's own time.</summary>
    public int NestEffort { get; set; } = -1;

    /// <summary>Cut the last sheet's leftover off as a rectangular offcut; null = default (off).</summary>
    public bool? PreferRectangularOffcut { get; set; }

    /// <summary>Offcut direction: 0 = end of sheet, 1 = side, 2 = both (L-shaped), 3 = auto (best remnant); -1 = default (end).</summary>
    public int OffcutDirection { get; set; } = -1;

    /// <summary>Gap between the packed parts and the offcut cut line; -1 = default (the part spacing).</summary>
    public double OffcutSpacing { get; set; } = -1;

    /// <summary>Narrowest offcut strip worth cutting (drawing units); -1/0 = automatic (5% of the sheet side).
    /// Superseded by the per-axis pair below, which it seeds when they have never been set.</summary>
    public double OffcutMinWidth { get; set; } = -1;

    /// <summary>Smallest remnant worth cutting off, across and up (drawing units); -1/0 = automatic.</summary>
    public double OffcutMinX { get; set; } = -1;

    public double OffcutMinY { get; set; } = -1;

    /// <summary>Largest remnant still worth cutting off; past it the sheet is kept whole. -1/0 = no limit.</summary>
    public double OffcutMaxX { get; set; } = -1;

    public double OffcutMaxY { get; set; } = -1;

    /// <summary>How far the offcut cut runs past the sheet edge at each end; -1/0 = flush with the edge.</summary>
    public double OffcutEdgeOverlap { get; set; } = -1;

    /// <summary>
    /// How close to parallel, how long and how far apart two faces have to be to be cut as one.
    /// Null = the shipped defaults.
    /// </summary>
    /// <remarks>
    /// Here, and NOT in the nest config, on purpose. Opening a project runs a full copy of the saved
    /// config over the live one, so a calibration would be silently replaced by whatever was in force
    /// on the machine that saved that job. These are properties of the machine in this shop, they are
    /// not properties of the job, and they must survive opening someone else's file.
    /// </remarks>
    public DeepNestLib.NestProject.CommonCuttingTolerances? CommonCutting { get; set; }

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

    /// <summary>
    /// Records a project in the MRU list (newest first, case-insensitive dedupe, capped at 8).
    /// Load-modify-save so it never clobbers the other session fields; best effort.
    /// </summary>
    public static void PushRecentProject(string path)
    {
      if (string.IsNullOrWhiteSpace(path))
      {
        return;
      }

      try
      {
        var session = Load() ?? new SessionState();
        session.RecentProjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        session.RecentProjects.Insert(0, path);
        if (session.RecentProjects.Count > 8)
        {
          session.RecentProjects.RemoveRange(8, session.RecentProjects.Count - 8);
        }

        session.Save();
      }
      catch
      {
        // MRU is a convenience — never let it break opening/saving
      }
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
