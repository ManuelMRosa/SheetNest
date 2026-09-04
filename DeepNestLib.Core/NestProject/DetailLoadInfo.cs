namespace DeepNestLib.NestProject
{
  using System.IO;
  using System.Text.Json;
  using System.Text.Json.Serialization;
  using DeepNestLib.IO;

  public class DetailLoadInfo : Saveable, IDetailLoadInfo
  {
    private CommonCuttingMode commonCutting = CommonCuttingMode.None;
    private bool hasExplicitCommonCutting;

    [JsonIgnore]
    public string Name => new FileInfo(Path).Name;

    public string Path { get; set; }

    public int Quantity { get; set; } = 1;

    public int Extra { get; set; } = 0;

    public int MirrorQuantity { get; set; } = 0; // copies nested X-FLIPPED (left/right-hand pairs); the exported DXF cuts them mirrored

    public int Rotations { get; set; } = -1; // per-part: 1 fixed, 2 = 0/180, 4 = 90° steps, 36 free; -1 = unset (geometry-based suggestion in Edit Part)

    public int Priority { get; set; } = 5;   // 1-10, LOWER nests first (1 = highest); 5 = normal

    public double Spacing { get; set; } = -1; // per-part gap to neighbours (drawing units); -1 = job default

    /// <summary>Who this part may share a cut edge with.</summary>
    /// <remarks>
    /// The backing flag distinguishes an explicit modern value (including <see cref="CommonCuttingMode.None"/>)
    /// from a value supplied by the legacy <c>CommonLine</c> JSON shim. A hand-edited file may contain
    /// both fields in either order; the modern mode must always win.
    /// </remarks>
    public CommonCuttingMode CommonCutting
    {
      get => this.commonCutting;
      set
      {
        this.commonCutting = value;
        this.hasExplicitCommonCutting = true;
      }
    }

    /// <summary>
    /// A direct DXF/STEP part has had common cutting explicitly confirmed by the operator. Missing/false
    /// keeps modes left behind by older builds inactive; SheetCam parts do not need this confirmation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool DirectCommonCuttingEnabled { get; set; }

    /// <summary>
    /// Gets whether this part has an output contract that can represent one shared cut. SheetCam owns
    /// its already-toolpathed contract; a direct part is supported only when its effective geometry is a
    /// DXF. STEP/IGES imports qualify through the unfolded DXF stored in <see cref="Path"/>.
    /// </summary>
    [JsonIgnore]
    public bool SupportsCommonCutting
    {
      get
      {
        if (!string.IsNullOrWhiteSpace(this.NestSourcePath))
        {
          return true;
        }

        try
        {
          return string.Equals(System.IO.Path.GetExtension(this.Path), ".dxf", System.StringComparison.OrdinalIgnoreCase);
        }
        catch (System.ArgumentException)
        {
          return false;
        }
      }
    }

    /// <summary>
    /// Gets who this part may share a cut edge with, once where it came from is taken into account.
    /// </summary>
    /// <remarks>
    /// <para>The two supported sources have separate cut contracts. A SheetCam part brings tooling and
    /// a measured kerf, so its existing toolpaths are seated one kerf apart. A direct DXF/STEP part has
    /// no tooling yet: its nominal full edges stay at the configured Common Line Gap and export as a
    /// resolved cluster perimeter plus one midpoint common cut. Direct common cutting is never inferred
    /// merely from a stored mode.</para>
    /// <para>Direct DXF/STEP parts may opt in through <see cref="DirectCommonCuttingEnabled"/>. Answering
    /// None here rather than clearing <see cref="CommonCutting"/> is deliberate: a project saved by a build
    /// that exposed common cutting globally can carry an unconfirmed mode on a plain part. Opening that
    /// project must not silently reactivate or erase it; Edit Part lets the operator confirm it.</para>
    /// </remarks>
    [JsonIgnore]
    public CommonCuttingMode EffectiveCommonCutting
      => this.SupportsCommonCutting
        && (!string.IsNullOrWhiteSpace(this.NestSourcePath) || this.DirectCommonCuttingEnabled)
        ? this.CommonCutting
        : CommonCuttingMode.None;

    /// <summary>
    /// Gets or sets common cutting as the plain on/off it used to be, so every existing caller still
    /// compiles and reads the same answer. <see cref="CommonCutting"/> is the real setting: turning this
    /// on means Unrestricted, and it reads true for SamePart too. Assigning this compatibility property
    /// is an explicit caller choice, so on a direct DXF/STEP it also confirms (or withdraws) the direct
    /// common-cutting opt-in. Legacy JSON is handled separately by <see cref="LegacyCommonLine"/> and
    /// never confirms it.
    /// </summary>
    [JsonIgnore]
    public bool CommonLine
    {
      get => this.CommonCutting != CommonCuttingMode.None;
      set
      {
        this.CommonCutting = value ? CommonCuttingMode.Unrestricted : CommonCuttingMode.None;
        if (string.IsNullOrWhiteSpace(this.NestSourcePath))
        {
          this.DirectCommonCuttingEnabled = value && this.SupportsCommonCutting;
        }
      }
    }

    /// <summary>
    /// Gets or sets the boolean "CommonLine" that projects saved before common cutting had modes carry.
    /// READ ONLY in practice: the getter is always false and WhenWritingDefault drops it, so we never
    /// write it back. During deserialization it supplies a fallback only until an explicit modern
    /// <see cref="CommonCutting"/> value is seen; that modern value wins even when a hand-edited file
    /// contains both properties, in either order.
    /// </summary>
    [JsonPropertyName("CommonLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyCommonLine
    {
      get => false;
      set
      {
        if (!this.hasExplicitCommonCutting)
        {
          this.commonCutting = value ? CommonCuttingMode.Unrestricted : CommonCuttingMode.None;
        }
      }
    }

    // Colour this part is drawn in (0xRRGGBB); -1 = none chosen, so it takes the palette colour for its
    // POSITION in the part list (part 1 the first colour, part 2 the second...). Persists to the .dnest;
    // a project saved before this existed simply has no such property and comes back as -1.
    public int ColorRgb { get; set; } = -1;

    [JsonIgnore]
    public bool IsExists => new FileInfo(this.Path).Exists;

    public bool IsIncluded { get; set; } = true;

    public bool IsPriority { get; set; } = false;

    public bool IsMultiplied { get; set; } = true;

    public AnglesEnum StrictAngle { get; set; } = AnglesEnum.None;

    // 3D-unfold provenance: when a part comes from a STEP/IGES import, these let SheetNest re-unfold it
    // (e.g. after the user changes the K-factor in Edit Part) and rebuild the flat if the temp DXF is
    // gone. Empty SourceStepPath = a plain 2D part (never 3D-unfolded). All persist to the .dnest.
    public string SourceStepPath { get; set; } = string.Empty;

    public int UnfoldIndex { get; set; } = 0; // index into StepUnfoldService.GetUnfoldedParts(...).Paths for THIS part

    public double KFactor { get; set; } = 0.40;

    public string KFactorStandard { get; set; } = "ansi";

    public bool UnfoldUnitInch { get; set; } = true;

    public double ThicknessMm { get; set; } = 0; // detected sheet thickness (mm); 0 = unknown (display only)

    // SheetCam .nest provenance: when a part came in from a SheetCam nest file, these say which file and
    // which <Part> in it, so the arrangement can be written back into that same document and the temp DXF
    // rebuilt if it is gone. Empty NestSourcePath = not from a nest file. Both persist to the .dnest.
    public string NestSourcePath { get; set; } = string.Empty;

    public string NestPartName { get; set; } = string.Empty;

    public bool NestUnitInch { get; set; } = true; // drawing units the nest geometry was scaled into (its mm / 25.4)

    public override string ToJson(bool writeIndented = false)
    {
      var options = new JsonSerializerOptions();
      options.WriteIndented = writeIndented;
      return JsonSerializer.Serialize(this, options);
    }
  }
}
