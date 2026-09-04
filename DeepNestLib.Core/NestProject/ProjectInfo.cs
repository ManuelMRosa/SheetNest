namespace DeepNestLib.NestProject
{
  using System;
  using System.IO;
  using System.Text.Json;
  using System.Text.Json.Nodes;
  using System.Text.Json.Serialization;
  using DeepNestLib.Placement;
  using Light.GuardClauses;

  public class ProjectInfo : IProjectInfo
  {
    public const string FileDialogFilter = "DeepNest Projects (*.dnest)|*.dnest|Json (*.json)|*.json|All files (*.*)|*.*";
    private ISvgNestConfig config;
    private double commonLineGapInches = CommonLineDefaults.DefaultGapInches;
    private int commonLineMaxClusterParts = CommonLineDefaults.DefaultMaxClusterParts;
    private WrappableList<ISheetLoadInfo, SheetLoadInfo> wrappableSheetLoadInfos;

    public ProjectInfo(ISvgNestConfig config)
    {
      this.config = config;
      this.CommonLineGapInches = config?.CommonLineGapInches ?? CommonLineDefaults.DefaultGapInches;
      this.CommonLineMaxClusterParts = config?.CommonLineMaxClusterParts
        ?? CommonLineDefaults.DefaultMaxClusterParts;
    }

    [JsonInclude]
    public IList<IDetailLoadInfo, DetailLoadInfo> DetailLoadInfos { get; private set; } = new WrappableList<IDetailLoadInfo, DetailLoadInfo>();

    [JsonInclude]
    public IList<ISheetLoadInfo, SheetLoadInfo> SheetLoadInfos
    {
      get
      {
        if (wrappableSheetLoadInfos == null)
        {
          // New projects start with NO stock — the user adds real sheet sizes via Add Sheet.
          // (Seeding a ConfigSheetLoadInfo here surfaced the legacy 3000x1500 x10 mm defaults
          // as a bogus inches sheet on every fresh install.)
          wrappableSheetLoadInfos = new WrappableList<ISheetLoadInfo, SheetLoadInfo>();
        }

        return wrappableSheetLoadInfos;
      }

      private set
      {
        wrappableSheetLoadInfos = (WrappableList<ISheetLoadInfo, SheetLoadInfo>)value;
      }
    }

    /// <summary>
    /// Gets or sets the nest result on screen at save time (NestResult json, the .dnr format).
    /// Optional: absent in older project files, and unknown to older apps (both ignore it safely).
    /// </summary>
    [JsonInclude]
    public string LastNestResultJson { get; set; }

    [JsonInclude]
    public ISvgNestConfig Config
    {
      get
      {
        return config ?? (config = SvgNest.Config);
      }

      private set
      {
        config = value;
      }
    }

    /// <inheritdoc />
    /// <remarks>
    /// This is deliberately not a second JSON field. <see cref="ToJson"/> writes the snapshot into
    /// Config.CommonLineGapInches, preserving the existing project format while keeping concurrently
    /// open documents independent from the shared runtime config.
    /// </remarks>
    [JsonIgnore]
    public double CommonLineGapInches
    {
      get => this.commonLineGapInches;
      set => this.commonLineGapInches = IsValidCommonLineGap(value)
        ? value
        : CommonLineDefaults.DefaultGapInches;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Like <see cref="CommonLineGapInches"/>, this is a document-owned snapshot patched into Config
    /// during serialization so concurrently open projects remain independent.
    /// </remarks>
    [JsonIgnore]
    public int CommonLineMaxClusterParts
    {
      get => this.commonLineMaxClusterParts;
      set => this.commonLineMaxClusterParts = CommonLineDefaults.NormalizeMaxClusterParts(value);
    }

    public static ProjectInfo FromJson(ISvgNestConfig config, string json)
    {
      try
      {
        if (!string.IsNullOrWhiteSpace(json))
        {
          var options = new JsonSerializerOptions();
          options.Converters.Add(new DetailLoadInfoJsonConverter());
          options.Converters.Add(new WrappableListJsonConverter<IDetailLoadInfo, DetailLoadInfo>());
          options.Converters.Add(new WrappableListJsonConverter<ISheetLoadInfo, SheetLoadInfo>());
          options.Converters.Add(new SheetLoadInfoJsonConverter());
          options.Converters.Add(new SvgNestConfigJsonConverter());
          var result = JsonSerializer.Deserialize<ProjectInfo>(json, options);
          var deserialized = result.config;
          result.config = config;
          result.CommonLineGapInches = deserialized?.CommonLineGapInches
            ?? CommonLineDefaults.DefaultGapInches;
          result.CommonLineMaxClusterParts = deserialized?.CommonLineMaxClusterParts
            ?? CommonLineDefaults.DefaultMaxClusterParts;
          if (deserialized != null)
          {
            // The job's settings, applied and not adopted. Without the scope every project open wrote the
            // file's values down as this operator's preferences, so a setting they had turned off came
            // back on for ever, and the same for the other twenty-nine that travel in a .dnest.
            using (SvgNestConfig.ApplyWithoutAdopting())
            {
              SvgNestConfigJsonConverter.CopyJobSettings(deserialized, config);
            }
          }

          return result;
        }

        return new ProjectInfo(config);
      }
      catch (Exception ex)
      {
        return new ProjectInfo(config);
      }
    }

    /// <summary>
    /// Loads the current instance with <see cref="ProjectInfo"/> passed in, maintaining collection instances.
    /// </summary>
    /// <param name="source">Source of ProjectInfo to set.</param>
    public void Load(ProjectInfo source)
    {
      this.LastNestResultJson = source.LastNestResultJson;
      this.CommonLineGapInches = source.CommonLineGapInches;
      this.CommonLineMaxClusterParts = source.CommonLineMaxClusterParts;
      this.DetailLoadInfos.Clear();
      foreach (var p in source.DetailLoadInfos)
      {
        this.DetailLoadInfos.Add(p);
      }

#if DeepNestPort
      var sheetInfoSource = source.SheetLoadInfos.FirstOrDefault();
      if (sheetInfoSource != null)
      {
        var sheetInfoTarget = this.SheetLoadInfos.Single();
        sheetInfoTarget.Width = sheetInfoSource.Width;
        sheetInfoTarget.Height = sheetInfoSource.Height;
        sheetInfoTarget.Quantity = sheetInfoSource.Quantity;
      }
#else
      this.SheetLoadInfos.Clear();
      foreach (var s in source.SheetLoadInfos)
      {
        this.SheetLoadInfos.Add(s);
      }
#endif
    }

    public string ToJson()
    {
      Config.MustBe(this.config);
      SheetLoadInfos.MustNotBeNull();

      var options = new JsonSerializerOptions();
      options.Converters.Add(new DetailLoadInfoJsonConverter());
      options.Converters.Add(new WrappableListJsonConverter<IDetailLoadInfo, DetailLoadInfo>());
      options.Converters.Add(new WrappableListJsonConverter<ISheetLoadInfo, SheetLoadInfo>());
      options.Converters.Add(new SheetLoadInfoJsonConverter());
      options.WriteIndented = true;
      options.Converters.Add(new SvgNestConfigJsonConverter());
      string json = JsonSerializer.Serialize(this, options);

      // Config is a process-wide instance shared by all tabs. Patch only this job-owned value in the
      // serialized tree instead of temporarily mutating that live instance (a nest may be running on
      // another thread while autosave serializes a document).
      JsonObject root = JsonNode.Parse(json)?.AsObject();
      if (root?[nameof(Config)] is JsonObject serializedConfig)
      {
        serializedConfig[nameof(ISvgNestConfig.CommonLineGapInches)] = this.CommonLineGapInches;
        serializedConfig[nameof(ISvgNestConfig.CommonLineMaxClusterParts)] = this.CommonLineMaxClusterParts;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
      }

      return json;
    }

    public static ProjectInfo LoadFromFile(ISvgNestConfig config, string fileName)
    {
      using (StreamReader inputFile = new StreamReader(fileName))
      {
        return FromJson(config, inputFile.ReadToEnd());
      }
    }

    public void Load(ISvgNestConfig config, string filePath)
    {
      var source = LoadFromFile(config, filePath);
      Load(source);
    }

    public void Save(string fileName)
    {
      using (StreamWriter outputFile = new StreamWriter(fileName))
      {
        outputFile.WriteLine(this.ToJson());
      }
    }

    private static bool IsValidCommonLineGap(double value)
      => !double.IsNaN(value)
        && !double.IsInfinity(value)
        && value > CommonLineDefaults.DefaultGeometryToleranceInches;
  }
}
