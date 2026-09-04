namespace DeepNestLib.Placement
{
  using System.Collections.Generic;
  using System.IO;
  using System.Threading.Tasks;
  using DeepNestLib.GeneticAlgorithm;
  using DeepNestLib.Geometry;

  public interface ISheetPlacement : IMinMaxXY
  {
    OriginalFitnessSheet Fitness { get; }

    INfp Hull { get; }

    double MaterialUtilization { get; }

    IReadOnlyList<IPartPlacement> PartPlacements { get; }

    IEnumerable<NoFitPolygon> PolygonsForExport { get; }

    PlacementTypeEnum PlacementType { get; }

    PolygonBounds RectBounds { get; }

    ISheet Sheet { get; }

    int SheetId { get; }

    int SheetSource { get; }

    INfp Simplify { get; }

    double TotalPartsArea { get; }

    double MergedLength { get; }

    /// <summary>
    /// Gets a value indicating whether this layout contains shared cut edges that must be merged on
    /// export, independently of the operator's global DXF preference.
    /// </summary>
    bool RequiresMergedLines { get; }

    /// <summary>
    /// Gets the intentional, first-class common-edge relationships captured by the nester. Null is
    /// permitted for placements written before common-line geometry was introduced.
    /// </summary>
    IReadOnlyList<CommonEdge> CommonEdges { get; }

    /// <summary>Gets the active common-line separation in the placement's drawing units.</summary>
    double CommonLineGap { get; }

    /// <summary>
    /// Gets the independent geometric comparison tolerance in the placement's drawing units.
    /// This is deliberately not inferred from <see cref="CommonLineGap"/>.
    /// </summary>
    double GeometryTolerance { get; }

    Task ExportDxf(Stream stream, bool mergeLines, bool differentiateChildren);

    string ToJson(bool writeIndented = false);

    string ToString();
  }
}
