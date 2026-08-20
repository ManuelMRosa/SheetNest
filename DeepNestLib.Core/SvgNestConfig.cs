[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DeepNestLib.CiTests")]

namespace DeepNestLib
{
#if NCRUNCH
  using System;
#endif
  using System.Text.Json.Serialization;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;

  public class SvgNestConfig : ISvgNestConfig
  {
    /// <summary>Set while values are being APPLIED rather than chosen, so they take effect for this
    /// session without being written down as what the operator prefers. Per thread, because it is a
    /// property of the work in hand and not of the program.</summary>
    [System.ThreadStatic]
    private static bool suppressPersist;

    /// <summary>Values held by THIS config. Empty means "whatever the operator has saved".</summary>
    private readonly System.Collections.Generic.Dictionary<string, object> own
      = new System.Collections.Generic.Dictionary<string, object>();

    /// <summary>Whether this config speaks for the operator. False for one read out of a file.</summary>
    private readonly bool isOperators;

    private object Read(string key)
      => this.own.TryGetValue(key, out object v) ? v : Settings.Default[key];

    /// <summary>
    /// Remember a value, and write it down only if it is the operator's own and they are choosing it.
    /// <para>Every property here used to read and write <c>Settings.Default</c> directly, which made this
    /// class a singleton wearing an object's clothes: two instances were never two configs, and merely
    /// DESERIALIZING one out of a .dnest overwrote the operator's saved preferences before a single line
    /// of the load code ran. That is why a user on issue #2 could not keep a setting turned off.</para>
    /// <para>The setter also called <c>Save()</c> and then <c>Upgrade()</c>, thirty times over. Upgrade
    /// pulls the values from the PREVIOUS installed version over the current ones and reloads, so calling
    /// it straight after a save can hand back the value just replaced. It belongs once at startup after an
    /// update, never on a write; the same pattern sits commented out in the app's SettingsService, so it
    /// was already suspected.</para>
    /// </summary>
    private void Write(string key, object value)
    {
      this.own[key] = value;
      if (this.isOperators && !suppressPersist)
      {
        Settings.Default[key] = value;
        Settings.Default.Save();
      }
    }

    /// <summary>
    /// Apply settings without adopting them. Inside the scope a value still takes effect, because the
    /// setting object itself is updated, but nothing reaches the file that holds the operator's choices.
    /// <para>This is what opening a project needs. A .dnest carries the spacing, margin and rotations its
    /// job was nested with and those have to apply, or reopening a job would nest it differently. What
    /// must not happen is the file's values being written down as this operator's preferences, which is
    /// how a setting somebody turned off came back on after every project they opened.</para>
    /// </summary>
    public static System.IDisposable ApplyWithoutAdopting() => new PersistenceSuppressed();

    private sealed class PersistenceSuppressed : System.IDisposable
    {
      private readonly bool previous;

      internal PersistenceSuppressed()
      {
        this.previous = suppressPersist;
        suppressPersist = true;
      }

      public void Dispose() => suppressPersist = this.previous;
    }

    public const int PopulationMin = 50;
    public const int PopulationMax = 800;
    public const int MultiplierMin = 1;
    public const int MultiplierMax = 100;
    public const int ParallelNestsMin = 1;
    public const int ParallelNestsMax = 30;

    /// <summary>
    /// A config that is nobody's in particular: it reads the operator's saved preferences as its starting
    /// point but never writes anything back. This is the DEFAULT on purpose. Deserializing a project used
    /// the parameterless constructor, and while every property wrote straight to the settings file that
    /// alone was enough to overwrite the preferences on this machine with those of whoever saved the file.
    /// Only the one config that speaks for the operator opts into writing, and it says so.
    /// </summary>
    public SvgNestConfig()
      : this(false)
    {
    }

    /// <summary>
    /// Carry the operator's settings across an update, ONCE.
    /// <para>Windows keeps each version's user settings in its own folder, so a new build starts with the
    /// defaults until it is told to bring the previous one's over. That is what <c>Upgrade()</c> is for,
    /// and it has to run exactly once per version: every setter used to call it after saving, which is how
    /// a value could be handed straight back after being changed.</para>
    /// </summary>
    internal static void CarrySettingsOverOnce()
    {
      if (Settings.Default.SettingsCarriedOver)
      {
        return;
      }

      Settings.Default.Upgrade();
      Settings.Default.SettingsCarriedOver = true;
      Settings.Default.Save();
    }

    /// <summary>The operator's own config when <paramref name="isOperators"/> is set: their saved
    /// preferences, and their choices written back.</summary>
    internal SvgNestConfig(bool isOperators)
    {
      this.isOperators = isOperators;
      if (isOperators)
      {
        CarrySettingsOverOnce();
      }

#if NCRUNCH
      throw new NotImplementedException();
#endif
    }

    /// <inheritdoc />
    public double Scale { get; set; } = 25;

    /// <inheritdoc />
    public double ClipperScale { get; set; } = 10000000;

    /// <inheritdoc />
    public bool ExploreConcave { get; set; } = false;

    /// <inheritdoc />
    public int Rotations { get; set; } = 4;

    /// <inheritdoc />
    /// <remarks>Default 0.25" — laser shops keep parts off the sheet edge (clamping/heat zone);
    /// 0.25 matches the 2× material-thickness rule for 1/8" plate.</remarks>
    public double SheetSpacing { get; set; } = 0.25;

    /// <inheritdoc />
    public bool UseHoles { get; set; } = false;

    /// <summary>
    /// Max bound for bezier->line segment conversion, in native SVG units.
    /// </summary>
    public double Tolerance { get; set; } = 2;

    /// <summary>
    /// Fudge factor for browser inaccuracy in SVG unit handling.
    /// </summary>
    public double ToleranceSvg { get; set; } = 0.005;

    /// <inheritdoc />
    public double TimeRatio { get; set; } = 0.5;

    /// <inheritdoc />
    public bool MergeLines
    {
      get
      {
        return (bool)this.Read("MergeLines");
      }

      set
      {
        this.Write("MergeLines", value);
      }
    }

    /// <inheritdoc />
    public bool ClipByHull
    {
      get
      {
        return (bool)this.Read("ClipByHull");
      }

      set
      {
        this.Write("ClipByHull", value);
      }
    }

    /// <inheritdoc />
    public double CurveTolerance
    {
      get
      {
        return (double)this.Read("CurveTolerance");
      }

      set
      {
        this.Write("CurveTolerance", value);
      }
    }

    /// <inheritdoc />
    public bool DifferentiateChildren
    {
      get
      {
        return (bool)this.Read("DifferentiateChildren");
      }

      set
      {
        this.Write("DifferentiateChildren", value);
      }
    }

    /// <inheritdoc />
    public bool DrawSimplification
    {
      get
      {
        return (bool)this.Read("DrawSimplification");
      }

      set
      {
        this.Write("DrawSimplification", value);
      }
    }

    [JsonIgnore]
    /// <inheritdoc />
    public bool ExportExecutions
    {
      get
      {
        return (bool)this.Read("ExportExecutions");
      }

      set
      {
        this.Write("ExportExecutions", value);
      }
    }

    /// <inheritdoc />
    public string ExportExecutionPath
    {
      get
      {
        return (string)this.Read("ExportExecutionPath");
      }

      set
      {
        this.Write("ExportExecutionPath", value);
      }
    }

    public string LastDebugFilePath
    {
      get
      {
        return (string)this.Read("LastDebugFilePath");
      }

      set
      {
        this.Write("LastDebugFilePath", value);
      }
    }

    [JsonIgnore]
    public string LastNestFilePath
    {
      get
      {
        return (string)this.Read("LastNestFilePath");
      }

      set
      {
        this.Write("LastNestFilePath", value);
      }
    }

    /// <inheritdoc />
    public int SaveAsFileTypeIndex
    {
      get
      {
        return (int)this.Read("SaveAsFileTypeIndex");
      }

      set
      {
        this.Write("SaveAsFileTypeIndex", value);
      }
    }

    /// <inheritdoc />
    public int SheetWidth
    {
      get
      {
        return (int)this.Read("SheetWidth");
      }

      set
      {
        this.Write("SheetWidth", value);
      }
    }

    /// <inheritdoc />
    public int SheetHeight
    {
      get
      {
        return (int)this.Read("SheetHeight");
      }

      set
      {
        this.Write("SheetHeight", value);
      }
    }

    /// <inheritdoc />
    public int SheetQuantity
    {
      get
      {
        return (int)this.Read("SheetQuantity");
      }

      set
      {
        this.Write("SheetQuantity", value);
      }
    }

    /// <inheritdoc />
    public PlacementTypeEnum PlacementType
    {
      get
      {
        return (PlacementTypeEnum)this.Read("PlacementType");
      }

      set
      {
        this.Write("PlacementType", (int)value);
      }
    }

    /// <inheritdoc />
    public bool Simplify
    {
      get
      {
        return (bool)this.Read("Simplify");
      }

      set
      {
        this.Write("Simplify", value);
      }
    }

    /// <inheritdoc />
    public bool OffsetTreePhase
    {
      get
      {
        return (bool)this.Read("OffsetTreePhase");
      }

      set
      {
        this.Write("OffsetTreePhase", value);
      }
    }

    /// <inheritdoc />
    public bool OverlapDetection
    {
      get
      {
        return (bool)this.Read("OverlapDetection");
      }

      set
      {
        this.Write("OverlapDetection", value);
      }
    }

    /// <inheritdoc />
    public double Spacing
    {
      get
      {
        return (double)this.Read("Spacing");
      }

      set
      {
        this.Write("Spacing", value);
      }
    }

    /// <inheritdoc />
    public int PopulationSize
    {
      get
      {
        var result = (int)this.Read("PopulationSize");
        if (result < PopulationMin) return PopulationMin;
        if (result > PopulationMax) return PopulationMax;
        return result;
      }

      set
      {
        this.Write("PopulationSize", value);
      }
    }

    /// <inheritdoc />
    public int ProcreationTimeout
    {
      get
      {
        var result = (int)this.Read("ProcreationTimeout");
        return result;
      }

      set
      {
        this.Write("ProcreationTimeout", value);
      }
    }

    /// <inheritdoc />
    public int MutationRate
    {
      get
      {
        var result = (int)this.Read("MutationRate");
        if (result < MutationRateMin)
        {
          return MutationRateMin;
        }

        if (result > MutationRateMax)
        {
          return MutationRateMax;
        }

        return result;
      }

      set
      {
        this.Write("MutationRate", value);
      }
    }

    public int MutationRateMin => 1;

    public int MutationRateMax => 60;

    /// <inheritdoc />
    public int Multiplier
    {
      get
      {
        var result = (int)this.Read("Multiplier");
        if (result < MutationRateMin)
        {
          return MultiplierMin;
        }

        if (result > MutationRateMax)
        {
          return MultiplierMax;
        }

        return result;
      }

      set
      {
        this.Write("Multiplier", value);
      }
    }

    /// <inheritdoc />
    public AnglesEnum StrictAngles
    {
      get
      {
        try
        {
          return (AnglesEnum)this.Read("StrictAngles");
        }
        catch (System.Exception)
        {
          return AnglesEnum.None;
        }
      }

      set
      {
        this.Write("StrictAngles", (int)value);
      }
    }

    /// <inheritdoc />
    public bool UseParallel
    {
      get
      {
        return (bool)this.Read("UseParallel");
      }

      set
      {
        this.Write("UseParallel", value);
      }
    }

    /// <inheritdoc />
    public int ParallelNests
    {
      get
      {
        var result = (int)this.Read("ParallelNests");
        if (result < ParallelNestsMin)
        {
          return ParallelNestsMin;
        }

        if (result > ParallelNestsMax)
        {
          return ParallelNestsMax;
        }

        return result;
      }

      set
      {
        this.Write("ParallelNests", value);
      }
    }

    /// <inheritdoc />
    public bool ShowPartPositions
    {
      get
      {
        return (bool)this.Read("ShowPartPositions");
      }

      set
      {
        this.Write("ShowPartPositions", value);
      }
    }

    /// <inheritdoc />
    public bool UseDllImport
    {
      get
      {
        return (bool)this.Read("UseDllImport");
      }

      set
      {
        this.Write("UseDllImport", value);
      }
    }

    /// <inheritdoc />
    public bool UseMinkowskiCache
    {
      get
      {
        return (bool)this.Read("UseMinkowskiCache");
      }

      set
      {
        this.Write("UseMinkowskiCache", value);
      }
    }

    /// <inheritdoc />
    public bool UsePriority
    {
      get
      {
        return (bool)this.Read("UsePriority");
      }

      set
      {
        this.Write("UsePriority", value);
      }
    }

    /// <inheritdoc />
    public double TopDiversity
    {
      get
      {
        return (double)this.Read("TopDiversity");
      }

      set
      {
        this.Write("TopDiversity", value);
      }
    }

    public string ToJson()
    {
      return SvgNestConfigJsonConverter.ToJson(this);
    }

    internal static ISvgNestConfig FromJson(string json)
    {
      return SvgNestConfigJsonConverter.FromJson(json);
    }
  }
}
