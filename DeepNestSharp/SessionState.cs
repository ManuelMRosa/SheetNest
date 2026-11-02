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
    /// <summary>
    /// Which build's settings this file holds. Absent, and so zero, on anything written before the
    /// stamp existed. It is what lets a one-off correction to a stored preference happen exactly once
    /// instead of every time the app starts.
    /// </summary>
    public const int CurrentSettingsVersion = 1;

    /// <summary>The version this file was last written by; see <see cref="CurrentSettingsVersion"/>.</summary>
    public int SettingsVersion { get; set; }

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

    /// <summary>
    /// Physical gap requested between common-line parts, always stored in inches regardless of the
    /// drawing units selected in the UI. Null or an invalid value means the shipped default.
    /// </summary>
    public double? CommonLineGapInches { get; set; }

    /// <summary>
    /// Maximum parts allowed in one common-line cluster. Zero means Auto; manual limits are 2..500.
    /// Invalid values from hand-edited or future session files resolve safely to Auto.
    /// </summary>
    public int CommonLineMaxClusterParts { get; set; } =
      DeepNestLib.Placement.CommonLineDefaults.DefaultMaxClusterParts;

    /// <summary>The valid common-line gap represented by this session.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double EffectiveCommonLineGapInches
    {
      get
      {
        double? value = this.CommonLineGapInches;
        return value.HasValue && IsValidCommonLineGap(value.Value)
          ? value.Value
          : DeepNestLib.Placement.CommonLineDefaults.DefaultGapInches;
      }
    }

    /// <summary>The normalized common-line cluster policy represented by this session.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveCommonLineMaxClusterParts
      => DeepNestLib.Placement.CommonLineDefaults.NormalizeMaxClusterParts(
        this.CommonLineMaxClusterParts);

    /// <summary>The material usage target a new install starts with, in percent.</summary>
    public const int DefaultMaterialTargetPercent = 90;

    /// <summary>
    /// Material usage target in percent: a nest whose parts cover at least this much of the sheets used, less
    /// the offcut cut off the last one, is handed over without spending the rest of the time. -1 = default
    /// (90), 0 = off.
    /// </summary>
    public int MaterialTargetPercent { get; set; } = -1;

    /// <summary>The target this session represents, 0 (off) to 100; never saved, or out of range, is the default.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveMaterialTargetPercent
      => this.MaterialTargetPercent is >= 0 and <= 100 ? this.MaterialTargetPercent : DefaultMaterialTargetPercent;

    /// <summary>The longest time per sheet the operator may set, in seconds.</summary>
    public const int MaxNestSecondsPerSheet = 600;

    /// <summary>The time per sheet a new install nests with, in seconds.</summary>
    public const int DefaultNestSecondsPerSheet = 2;

    /// <summary>
    /// Time limit per sheet in seconds: the nest gets this much for every sheet the job is expected to take,
    /// and searches by the clock. -1 = default (<see cref="DefaultNestSecondsPerSheet"/>), 0 = no limit (the
    /// target, two rounds without improvement or Stop end it).
    /// </summary>
    public int NestSecondsPerSheet { get; set; } = -1;

    /// <summary>The time per sheet this session represents, 0 (no limit) to <see cref="MaxNestSecondsPerSheet"/>;
    /// never saved, or out of range, is the default.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveNestSecondsPerSheet
      => this.NestSecondsPerSheet is >= 0 and <= MaxNestSecondsPerSheet ? this.NestSecondsPerSheet : DefaultNestSecondsPerSheet;

    /// <summary>User-saved sheet size presets ("My sheets" in the Add Sheet menu); Quantity unused.</summary>
    public List<SessionSheet> SheetPresets { get; set; } = new List<SessionSheet>();

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

    /// <summary>Cut the skeleton into pieces with scrap cuts; null = default (off).</summary>
    public bool? ScrapCutsEnabled { get; set; }

    /// <summary>Scrap cut lines: 0 = both ways, 1 = vertical, 2 = horizontal; -1 = default (both).</summary>
    public int ScrapCutType { get; set; } = -1;

    /// <summary>Ideal distance between the vertical scrap cuts (drawing units); -1 = default, 0 = none.</summary>
    public double ScrapSpacingX { get; set; } = -1;

    /// <summary>Ideal distance between the horizontal scrap cuts; -1 = default, 0 = none.</summary>
    public double ScrapSpacingY { get; set; } = -1;

    /// <summary>How close a scrap cut comes to a part; -1 = default.</summary>
    public double ScrapPartGap { get; set; } = -1;

    /// <summary>How far a scrap cut runs past the sheet edge; -1 = default.</summary>
    public double ScrapEdgeOverlap { get; set; } = -1;

    /// <summary>
    /// Records the values owned by the running application when it closes. Called on an already-loaded
    /// session so preferences written by their own dialogs (including offcuts) survive.
    /// </summary>
    internal void CaptureApplicationClose(
      double sheetEdgeMargin,
      double unfoldKFactor,
      string? unfoldKFactorStandard,
      string? freeCadCmdPath,
      bool unfoldUnitInch,
      bool unitsMm,
      IEnumerable<SessionSheet> sheetPresets,
      bool autosaveEnabled,
      int autosaveMinutes,
      int defaultRotations,
      double commonLineGapInches,
      int commonLineMaxClusterParts = DeepNestLib.Placement.CommonLineDefaults.DefaultMaxClusterParts)
    {
      this.SheetEdgeMargin = sheetEdgeMargin;
      this.Sheets = new List<SessionSheet>();
      this.UnfoldKFactor = unfoldKFactor;
      this.UnfoldKFactorStandard = unfoldKFactorStandard;
      this.FreeCadCmdPath = freeCadCmdPath;
      this.UnfoldUnitInch = unfoldUnitInch;
      this.UnitsMm = unitsMm;
      this.SheetPresets = new List<SessionSheet>(sheetPresets ?? Array.Empty<SessionSheet>());
      this.AutosaveEnabled = autosaveEnabled;
      this.AutosaveMinutes = autosaveMinutes;
      this.DefaultRotations = defaultRotations;
      this.CommonLineGapInches = IsValidCommonLineGap(commonLineGapInches)
        ? commonLineGapInches
        : DeepNestLib.Placement.CommonLineDefaults.DefaultGapInches;
      this.CommonLineMaxClusterParts = DeepNestLib.Placement.CommonLineDefaults.NormalizeMaxClusterParts(
        commonLineMaxClusterParts);
    }

    private static bool IsValidCommonLineGap(double value)
      => value > DeepNestLib.Placement.CommonLineDefaults.DefaultGeometryToleranceInches
        && !double.IsNaN(value)
        && !double.IsInfinity(value);

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
        // Any write brings the file up to date, so a one-off correction never fires twice.
        this.SettingsVersion = CurrentSettingsVersion;
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
