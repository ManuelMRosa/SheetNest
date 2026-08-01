namespace DeepNestLib.NestProject
{
  using System.Text.Json.Serialization;

  /// <summary>
  /// Who a part is allowed to share a cut edge with. Common cutting nests parts touching so the shared
  /// edge is cut ONCE instead of twice, saving both the material between them and half the cutting.
  /// </summary>
  /// <remarks>
  /// <para>KNOWN LIMITS OF THIS FEATURE, so nobody has to rediscover them the hard way:</para>
  /// <para>
  /// 1. The nesting engine (sparrow) cannot see common cutting AT ALL. It is handed one already-dilated
  /// polygon per part type and nothing else, so it has no notion of a clearance that depends on WHICH
  /// two parts are involved. It is therefore impossible for the engine to place a part tight against its
  /// own kind and spaced from a different one in the same step. Every pairwise decision happens after it,
  /// in RasterCompact, which only TRANSLATES (down, then left): it never reorders, rotates or re-fits.
  /// </para>
  /// <para>
  /// 2. That is also why there is no equivalent of a maximum shared-group size. Capping a group requires
  /// the packer to form the groups, and ours never sees them; it would take a pre-tiler that builds the
  /// module and hands it to the engine as a single part. RasterCompact.Compact already has the hook (its
  /// `groups` parameter and rigid-module support) and no production path calls it.
  /// </para>
  /// <para>
  /// 3. Only horizontal and vertical edges are ever snapped together (SnapCommonLineEdges takes just
  /// axis-aligned edges), which is why a part in any mode but None has its rotations clipped to 90 degree
  /// steps. A common-cut part never rotates freely, so a scalene triangle - where common cutting would pay
  /// most - does not share an edge by this route.
  /// </para>
  /// <para>
  /// 4. The snap is all-or-nothing for the whole pass: if any pair's tooling ends up invading a
  /// neighbour, every snap on the sheet is rolled back. The nest is still correct, just without common
  /// cutting, and nothing says so out loud.
  /// </para>
  /// <para>
  /// 5. "Shared edge" means two different things depending on where the part came from. A plain DXF has
  /// no kerf to know about, so compaction leaves the lines COINCIDENT and the DXF export merges them into
  /// one cut. A part carrying SheetCam tooling ends up one kerf apart instead, not coincident, and it is
  /// the post processor - not SheetNest - that decides to cut it once.
  /// </para>
  /// </remarks>
  [JsonConverter(typeof(JsonStringEnumConverter))]
  public enum CommonCuttingMode
  {
    /// <summary>This part never shares a cut edge; it keeps its full spacing to everything.</summary>
    None = 0,

    /// <summary>This part may share a cut edge with any other part that also allows it.</summary>
    Unrestricted = 1,

    /// <summary>
    /// This part shares a cut edge only with copies of ITSELF (the same drawing). A mirrored copy counts
    /// as a different part. Against anything else it keeps its full spacing.
    /// </summary>
    SamePart = 2,
  }
}
