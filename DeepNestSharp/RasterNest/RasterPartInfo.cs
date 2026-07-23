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
    public bool CommonLine;     // share the cut with neighbours — routed through the grid packer, not sparrow
    public bool Mirrored;       // nest this population X-flipped; its placements carry IsMirrored for the exporter

    // Tooling, when the part came from a SheetCam .nest — that file describes a part that is already
    // toolpathed, so the material it occupies is wider than its outline. Empty/0 for plain DXF parts,
    // which then nest exactly as before.
    public System.Collections.Generic.IReadOnlyList<System.Collections.Generic.IReadOnlyList<DeepNestLib.SvgPoint>> ToolPaths; // lead-ins/outs/loops, part-local, drawing units
    public double Kerf;         // cut width in drawing units; the cut runs half of it outside the outline
  }
}
