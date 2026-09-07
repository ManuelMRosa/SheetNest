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
    // Common-cutting contracts are deliberately isolated: two parts share a cut only when they name the
    // same non-empty contract, so allowing common cutting is never on its own enough to weld a pair.
    public string CommonCuttingDomain = string.Empty;
    // Opts a nominal DXF/STEP contour into schema-aware resolved output: selected LINE/ARC spans stay at
    // the configured gap, then export as a closed cluster perimeter plus midpoint COMMON_LINE geometry.
    // False means this part keeps ordinary spacing everywhere.
    public bool RequiresMergedLines;
    public bool Mirrored;       // nest this population X-flipped; its placements carry IsMirrored for the exporter
  }
}
