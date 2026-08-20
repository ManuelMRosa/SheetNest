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
  /// 3. A seam can run at ANY angle, but only between STRAIGHT edges. Chords of a tessellated curve are
  /// excluded on purpose: a chord lies inside its arc, so welding to one gives the bulge away. A
  /// common-cut part is still clipped to 45 degree steps rather than rotating freely, because at
  /// arbitrary angles two copies almost never present each other parallel faces. So two identical
  /// scalene triangles nested head to tail still will not share their long edge unless the engine
  /// happened to leave them facing.
  /// </para>
  /// <para>
  /// 4. A seam is given up per GROUP, not per sheet: if one group's cut would reach into a neighbour
  /// only that group loses its seam, and the count of what was welded and what was abandoned is
  /// reported when the nest finishes. It used to roll the whole sheet back in silence.
  /// </para>
  /// <para>
  /// 5. The kerf is the whole mechanism: it is the gap parts are placed at and the width of the single
  /// cut that severs both. With no kerf known there is nothing to place to and the feature does nothing
  /// at all, which is why it is offered only for parts that came in from a SheetCam nest file: those
  /// carry tooling the kerf can be measured off, and nothing else does. The seam is then a kerf wide and
  /// exports as two lines, for the post to decide to cut once.
  /// </para>
  /// <para>
  /// 6. None of the tolerances here are anybody else's published values. They are starting points for
  /// the trade and they need calibrating against a kerf MEASURED on the machine.
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
