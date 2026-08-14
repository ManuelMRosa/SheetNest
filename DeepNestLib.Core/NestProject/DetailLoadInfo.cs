namespace DeepNestLib.NestProject
{
  using System.IO;
  using System.Text.Json;
  using System.Text.Json.Serialization;
  using DeepNestLib.IO;

  public class DetailLoadInfo : Saveable, IDetailLoadInfo
  {
    [JsonIgnore]
    public string Name => new FileInfo(Path).Name;

    public string Path { get; set; }

    public int Quantity { get; set; } = 1;

    public int Extra { get; set; } = 0;

    public int MirrorQuantity { get; set; } = 0; // copies nested X-FLIPPED (left/right-hand pairs); the exported DXF cuts them mirrored

    public int Rotations { get; set; } = -1; // per-part: 1 fixed, 2 = 0/180, 4 = 90° steps, 36 free; -1 = unset (geometry-based suggestion in Edit Part)

    public int Priority { get; set; } = 5;   // 1-10, LOWER nests first (1 = highest); 5 = normal

    public double Spacing { get; set; } = -1; // per-part gap to neighbours (drawing units); -1 = job default

    public CommonCuttingMode CommonCutting { get; set; } = CommonCuttingMode.None; // who this part may share a cut edge with

    /// <summary>
    /// Gets who this part may share a cut edge with, once where it came from is taken into account.
    /// </summary>
    /// <remarks>
    /// <para>Common cutting is built on the kerf: it is both the gap the two parts are placed at and the
    /// width of the single cut that severs them. The kerf is MEASURED off the lead geometry of a SheetCam
    /// .nest, so a part that did not come from one has no kerf and nothing to share, and the engine agrees
    /// — its whole common-cut phase gives up when no part has a kerf.</para>
    /// <para>Answering None here rather than clearing <see cref="CommonCutting"/> is deliberate. Before
    /// common cutting was restricted to nest jobs the setting was offered for any part at all, so a project
    /// saved back then can carry it on a plain DXF (see <see cref="LegacyCommonLine"/>), and there is no
    /// longer a control to turn it off. Overwriting what the file says would rewrite the user's project
    /// behind their back on nothing more than opening it; this leaves the file alone and simply declines to
    /// act on it.</para>
    /// </remarks>
    [JsonIgnore]
    public CommonCuttingMode EffectiveCommonCutting
      => string.IsNullOrEmpty(this.NestSourcePath) ? CommonCuttingMode.None : this.CommonCutting;

    // This part's cut width in MILLIMETRES, whatever the drawing units are; -1 = not set, use the job's.
    // Millimetres because that is how a kerf is quoted and measured, and because it is already the unit
    // the value derived from a SheetCam nest arrives in.
    public double KerfMm { get; set; } = -1;

    /// <summary>
    /// Gets or sets common cutting as the plain on/off it used to be, so every existing caller still
    /// compiles and reads the same answer. <see cref="CommonCutting"/> is the real setting: turning this
    /// on means Unrestricted, and it reads true for SamePart too.
    /// </summary>
    [JsonIgnore]
    public bool CommonLine
    {
      get => this.CommonCutting != CommonCuttingMode.None;
      set => this.CommonCutting = value ? CommonCuttingMode.Unrestricted : CommonCuttingMode.None;
    }

    /// <summary>
    /// Gets or sets the boolean "CommonLine" that projects saved before common cutting had modes carry.
    /// READ ONLY in practice: the getter is always false and WhenWritingDefault drops it, so we never
    /// write it back. That is what makes the two properties order-independent in a file — a project can
    /// only ever carry one of them, so whichever comes first cannot overwrite the other.
    /// </summary>
    [JsonPropertyName("CommonLine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyCommonLine
    {
      get => false;
      set
      {
        if (value && this.CommonCutting == CommonCuttingMode.None)
        {
          this.CommonCutting = CommonCuttingMode.Unrestricted;
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
