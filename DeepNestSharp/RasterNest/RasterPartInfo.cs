namespace DeepNestSharp.RasterNest
{
  /// <summary>Plain per-part nesting data handed to the nesting engine (background-thread safe).</summary>
  internal sealed class RasterPartInfo
  {
    public string Path;
    public int Quantity;        // total to nest (required + extra)
    public int Rotations = -1;  // per-part allowed rotations; -1 = use the job's global setting
    public int Priority = 5;    // 1-10, LOWER nests first (1 = highest); 5 = normal
    public double Spacing = -1; // per-part gap to neighbours (in); 0 = common-line (touching); -1 = job default
    public bool Mirrored;       // nest this population X-flipped; its placements carry IsMirrored for the exporter
  }
}
