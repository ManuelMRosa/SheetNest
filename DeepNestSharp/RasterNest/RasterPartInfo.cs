namespace DeepNestSharp.RasterNest
{
  /// <summary>Plain per-part nesting data handed to the nesting engine (background-thread safe).</summary>
  internal sealed class RasterPartInfo
  {
    public string Path;
    public int Quantity;        // total to nest (required + extra)
    public int Rotations = -1;  // per-part allowed rotations; -1 = use the job's global setting
    public int Priority = 5;    // 1-10, LOWER nests first (1 = highest); 5 = normal
    public double Spacing = -1; // per-part gap to the neighbours it may NOT share a cut with (in); -1 = job default
    public DeepNestLib.NestProject.CommonCuttingMode Cc; // who it may share a cut edge with; None = nobody
    // Common-cutting contracts are deliberately isolated. Direct DXF/STEP uses one shared domain;
    // SheetCam uses the source .nest path, so an already-offset SheetCam toolpath can never be welded
    // to an exact direct-export contour (or to a different SheetCam job) merely because both modes allow it.
    public string CommonCuttingDomain = string.Empty;
    // Opts a nominal DXF/STEP contour into schema-aware resolved output: selected LINE/ARC spans stay at
    // the configured gap, then export as a closed cluster perimeter plus midpoint COMMON_LINE geometry.
    // False keeps SheetCam on its separate measured-kerf .nest return path.
    public bool RequiresMergedLines;
    public bool ValidateEntryLead;   // SheetCam tooling: warn when a cut chain has no exterior entry lead
    public bool Mirrored;       // nest this population X-flipped; its placements carry IsMirrored for the exporter

    // Tooling, when the part came from a SheetCam .nest — that file describes a part that is already
    // toolpathed, so the material it occupies is wider than its outline. Empty/0 for plain DXF parts,
    // which then nest exactly as before.
    public System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<DeepNestLib.SvgPoint>> ToolPaths; // lead-ins/outs/loops, part-local, drawing units
    public double Kerf;         // cut width in drawing units; the cut runs half of it outside the outline

    /// <summary>
    /// What "the same part" means for <see cref="DeepNestLib.NestProject.CommonCuttingMode.SamePart"/>.
    /// Null (the default) means the file path, which is the behaviour every ordinary job gets.
    /// </summary>
    /// <remarks>
    /// It exists for the one-lead-per-chain setup: SheetCam cannot be told to drop a lead in, so the job
    /// carries the same drawing twice under two names, one set up with a lead and one without. Those are
    /// two FILES, so keyed by path they read as two different drawings and Same part refuses to weld them
    /// - which strands the single leaded copy in a chain of its own and leaves the other chain with no
    /// entry at all, uncuttable. Naming both members of the pair to the same share group fixes that
    /// without touching what any other job does.
    /// </remarks>
    public string ShareGroup;
  }
}
