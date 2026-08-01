namespace DeepNestLib.NestProject
{
  public interface IDetailLoadInfo
  {
    bool IsIncluded { get; set; }

    bool IsMultiplied { get; set; }

    bool IsPriority { get; set; }

    /// <summary>
    /// Gets the name of the file (excluding path).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets or sets the full name of the file (including path).
    /// </summary>
    string Path { get; set; }

    int Quantity { get; set; }

    /// <summary>
    /// Gets or sets extra (spare) parts to nest on top of <see cref="Quantity"/> — e.g. one spare
    /// in case a cut is scrapped. Nested identically; only the paperwork distinguishes them.
    /// </summary>
    int Extra { get; set; }

    /// <summary>
    /// Gets or sets how many MIRRORED (X-flipped) copies to nest on top of <see cref="Quantity"/> +
    /// <see cref="Extra"/> — left-hand/right-hand sheet-metal pairs. The exported DXF cuts these mirrored.
    /// </summary>
    int MirrorQuantity { get; set; }

    /// <summary>
    /// Gets or sets the per-part allowed rotations (1 = fixed, 2 = 0/180, 4 = 90° steps, 36 = free).
    /// Values &lt;= 0 fall back to the engine's configured default.
    /// </summary>
    int Rotations { get; set; }

    /// <summary>Gets or sets the nesting priority 1-10 (LOWER nests first; 1 = highest); 5 = normal.</summary>
    int Priority { get; set; }

    /// <summary>
    /// Gets or sets this part's spacing to neighbouring parts (drawing units). Two parts end up
    /// (spacingA + spacingB) / 2 apart. Negative = use the job default.
    /// </summary>
    double Spacing { get; set; }

    /// <summary>
    /// Gets or sets who this part may share a cut edge with. Sharing nests the two touching so the
    /// shared edge is cut once. <see cref="Spacing"/> still applies to everyone it may NOT share with.
    /// </summary>
    CommonCuttingMode CommonCutting { get; set; }

    /// <summary>
    /// Gets or sets common cutting as a plain on/off. True for any mode but
    /// <see cref="CommonCuttingMode.None"/>; setting it true means
    /// <see cref="CommonCuttingMode.Unrestricted"/>.
    /// </summary>
    bool CommonLine { get; set; }

    /// <summary>
    /// Gets or sets the colour this part is drawn in, as 0xRRGGBB. -1 means the user has not chosen one,
    /// so it takes the palette colour for its POSITION in the part list.
    /// </summary>
    int ColorRgb { get; set; }

    AnglesEnum StrictAngle { get; set; }

    bool IsExists { get; }

    /// <summary>
    /// Gets or sets the original STEP/IGES file this part was unfolded from. Empty = a plain 2D part
    /// (never 3D-unfolded). When set, the part can be re-unfolded (e.g. on a K-factor change).
    /// </summary>
    string SourceStepPath { get; set; }

    /// <summary>Gets or sets this part's index into the unfolded flats produced from the source STEP.</summary>
    int UnfoldIndex { get; set; }

    /// <summary>Gets or sets the bend-allowance K-factor used to unfold this 3D part.</summary>
    double KFactor { get; set; }

    /// <summary>Gets or sets the bend-table standard ("ansi" / "din") used to unfold this 3D part.</summary>
    string KFactorStandard { get; set; }

    /// <summary>Gets or sets whether this 3D part was unfolded into inches (true) or millimetres (false).</summary>
    bool UnfoldUnitInch { get; set; }

    /// <summary>Gets or sets the detected sheet thickness (mm) of this 3D part; 0 = unknown.</summary>
    double ThicknessMm { get; set; }

    /// <summary>
    /// Gets or sets the SheetCam .nest file this part came from. Empty = not imported from a nest file.
    /// When set, the nested arrangement can be written back into that document.
    /// </summary>
    string NestSourcePath { get; set; }

    /// <summary>Gets or sets the name of the &lt;Part&gt; element this part came from in <see cref="NestSourcePath"/>.</summary>
    string NestPartName { get; set; }

    /// <summary>Gets or sets whether the nest geometry was scaled into inches (true) or left in millimetres.</summary>
    bool NestUnitInch { get; set; }
  }
}