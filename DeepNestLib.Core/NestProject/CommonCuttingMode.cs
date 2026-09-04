namespace DeepNestLib.NestProject
{
  using System.Text.Json.Serialization;

  /// <summary>
  /// Who a part is allowed to share a cut edge with. Direct DXF/STEP nominal contours remain separated by
  /// the configured Common Line Gap and export one midpoint cut inside a resolved cluster. SheetCam parts
  /// align their already-offset toolpaths one measured kerf apart. Either contract cuts the seam once.
  /// </summary>
  /// <remarks>
  /// <para>KNOWN LIMITS OF THIS FEATURE, so nobody has to rediscover them the hard way:</para>
  /// <para>
  /// 1. Sparrow receives every part with its ordinary spacing and cannot choose a pair-specific clearance.
  /// After that safe baseline, the direct V1 planner deliberately forms full-edge rigid clusters at the
  /// configured gap and records their indexed outside edges. Compatibility alone never removes spacing.
  /// </para>
  /// <para>
  /// 2. There is no user maximum cluster size. V1 builds consistent full-edge chains/grids from the
  /// candidate it was given, then compacts each connected component as a rigid module and revalidates the
  /// complete graph. A failed component returns to the ordinary-spacing baseline.
  /// </para>
  /// <para>
  /// 3. Direct V1 accepts quarter-turn placements and complete, equal-length STRAIGHT outside edges only.
  /// Chords of a tessellated curve and partial overlaps are excluded. SheetCam's legacy measured-kerf
  /// alignment remains angle-independent because it returns the existing toolpaths rather than rebuilding
  /// a nominal cluster boundary.
  /// </para>
  /// <para>
  /// 4. A direct seam is accepted only with a valid CommonEdge record, normal clearance everywhere outside
  /// its indexed edge/corner junction, and a valid resolved topology. A failed move never leaves an
  /// unrecorded pair at Common Line Gap. SheetCam retains its tooling-footprint collision check.
  /// </para>
  /// <para>
  /// 5. There are two deliberately separate domains. Direct DXF and unfolded STEP/IGES parts have no
  /// toolpath yet: their nominal edges stay at Common Line Gap and export as CUT_OUTER cluster boundaries
  /// plus one midpoint COMMON_LINE. SheetCam parts keep their measured-kerf toolpaths and return to their
  /// owning .nest job. The two domains never share with each other or another SheetCam job.
  /// </para>
  /// <para>
  /// 6. Common Line Gap and Geometry Tolerance are independent physical settings for the direct resolver.
  /// The older internal angle/gap/overlap tolerances apply only to SheetCam measured-kerf snapping.
  /// </para>
  /// </remarks>
  [JsonConverter(typeof(JsonStringEnumConverter))]
  public enum CommonCuttingMode
  {
    /// <summary>This part never shares a cut edge; it keeps its full spacing to everything.</summary>
    None = 0,

    /// <summary>This part may share a cut edge with any compatible part that also allows it.</summary>
    Unrestricted = 1,

    /// <summary>
    /// This part shares a cut edge only with copies of ITSELF (the same drawing). A mirrored copy counts
    /// as a different part. Against anything else it keeps its full spacing.
    /// </summary>
    SamePart = 2,
  }
}
