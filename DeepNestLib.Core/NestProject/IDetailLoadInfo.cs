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
    /// Gets or sets the per-part allowed rotations (1 = fixed, 2 = 0/180, 4 = 90° steps, 36 = free).
    /// Values &lt;= 0 fall back to the engine's configured default.
    /// </summary>
    int Rotations { get; set; }

    /// <summary>Gets or sets the nesting priority 0-10 (higher nests first); 5 = normal.</summary>
    int Priority { get; set; }

    /// <summary>
    /// Gets or sets this part's spacing to neighbouring parts (drawing units). Two parts end up
    /// (spacingA + spacingB) / 2 apart. Negative = use the job default.
    /// </summary>
    double Spacing { get; set; }

    /// <summary>
    /// Gets or sets common-line cutting: copies of this part nest TOUCHING each other (spacing 0),
    /// so shared edges are cut once.
    /// </summary>
    bool CommonLine { get; set; }

    AnglesEnum StrictAngle { get; set; }

    bool IsExists { get; }
  }
}