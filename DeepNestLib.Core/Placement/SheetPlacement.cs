namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Text.Json;
  using System.Text.Json.Serialization;
  using System.Threading.Tasks;
  using DeepNestLib.GeneticAlgorithm;
  using DeepNestLib.Geometry;
  using DeepNestLib.IO;

  /// <summary>
  /// Represents a sheet that has had parts placed on it in the nest.
  /// </summary>
  public class SheetPlacement : Saveable, ISheetPlacement
  {
    public const string FileDialogFilter = "DeepNest SheetPlacement (*.dnsp)|*.dnsp|Json (*.json)|*.json|All files (*.*)|*.*";

    private NoFitPolygon hull;
    private double clipperScale;

    [JsonConstructor]
    public SheetPlacement(
      PlacementTypeEnum placementType,
      ISheet sheet,
      IReadOnlyList<IPartPlacement> partPlacements,
      double mergedLength,
      bool requiresMergedLines = false,
      IReadOnlyList<CommonEdge> commonEdges = null,
      double commonLineGap = 0D,
      double geometryTolerance = 0D)
      : this(
          placementType,
          sheet,
          partPlacements,
          mergedLength,
          new TestSvgNestConfig().ClipperScale,
          requiresMergedLines,
          commonEdges,
          commonLineGap,
          geometryTolerance)
    {
    }

    public SheetPlacement(
      PlacementTypeEnum placementType,
      ISheet sheet,
      IReadOnlyList<IPartPlacement> partPlacements,
      double mergedLength,
      double clipperScale,
      bool requiresMergedLines = false,
      IReadOnlyList<CommonEdge> commonEdges = null,
      double commonLineGap = 0D,
      double geometryTolerance = 0D)
    {
      this.PlacementType = placementType;
      this.Sheet = sheet;
      this.PartPlacements = partPlacements;
      this.MergedLength = mergedLength;
      this.CommonEdges = commonEdges != null && commonEdges.Count > 0 ? commonEdges : null;
      this.CommonLineGap = this.CommonEdges == null ? 0D : commonLineGap;
      this.GeometryTolerance = this.CommonEdges == null ? 0D : geometryTolerance;
      this.RequiresMergedLines = requiresMergedLines || this.CommonEdges != null;
      this.clipperScale = clipperScale;
      this.Fitness = new OriginalFitnessSheet(this);
    }

    /// <summary>
    /// Gets memoised sheet.Id; to maintain legacy - monitor if sheet.Id is ever getting updated (may be Liskov breach in Sheet?).
    /// </summary>
    public int SheetId => Sheet.Id;

    /// <summary>
    /// Gets memoised sheet.Source; to maintain legacy - monitor if sheet.Id is ever getting updated (may be Liskov breach in Sheet?).
    /// </summary>
    public int SheetSource => Sheet.Source;

    [JsonInclude]
    public PlacementTypeEnum PlacementType { get; private set; }

    [JsonInclude]
    public ISheet Sheet { get; private set; }

    [JsonInclude]
    public IReadOnlyList<IPartPlacement> PartPlacements { get; private set; } = new List<IPartPlacement>();

    [JsonIgnore]
    public PolygonBounds RectBounds => CombinedRectBounds(this.PartPlacements);

    [JsonIgnore]
    public INfp Hull
    {
      get
      {
        if (hull == null)
        {
          hull = CombinedPoints(this.PartPlacements).GetHull();
        }

        return hull;
      }
    }

    [JsonIgnore]
    public INfp Simplify
    {
      get
      {
#if NCRUNCH
        var clipperScale = this.clipperScale == 0 ? 10000000 : this.clipperScale;
#else
        var clipperScale = this.clipperScale;
#endif
        var allpoints = CombinedPoints(this.PartPlacements);
        var clipperNfp = NfpHelper.NfpToClipperCoordinates(allpoints, clipperScale);

        var combinedNfp = new List<List<ClipperLib.IntPoint>>();
        var clipper = new ClipperLib.Clipper();
        _ = clipper.AddPaths(clipperNfp, ClipperLib.PolyType.ptSubject, true);
        _ = clipper.Execute(ClipperLib.ClipType.ctUnion, combinedNfp, ClipperLib.PolyFillType.pftNonZero, ClipperLib.PolyFillType.pftNonZero);

        return NfpSimplifier.SimplifyFunction(combinedNfp[0].ToArray().ToNestCoordinates(clipperScale), false, 1, false);
      }
    }

    [JsonIgnore]
    public double TotalPartsArea => this.PartPlacements.Sum(p => p.Part.NetArea);

    [JsonIgnore]
    public double MaterialUtilization
    {
      get
      {
        try
        {

          return Math.Abs(TotalPartsArea / this.Sheet.Area);
        }
        catch (Exception ex)
        {
          System.Diagnostics.Debug.Print(ex.Message);
          System.Diagnostics.Debug.Print(ex.StackTrace);
          throw;
        }
      }
    }

    [JsonIgnore]
    public OriginalFitnessSheet Fitness { get; }

    [JsonIgnore]
    public double MaxX => PartPlacements.Max(pp => pp.MaxX);

    [JsonIgnore]
    public double MaxY => PartPlacements.Max(pp => pp.MaxY);

    [JsonIgnore]
    public double MinX => PartPlacements.Min(pp => pp.MinX);

    [JsonIgnore]
    public double MinY => PartPlacements.Min(pp => pp.MinY);

    public double MergedLength { get; }

    /// <summary>
    /// Gets a value indicating whether this layout contains shared cut edges that must be merged on
    /// export. False is omitted so sheet placements written before this metadata existed and new ordinary
    /// layouts have the same JSON shape.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequiresMergedLines { get; }

    /// <inheritdoc />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CommonEdge> CommonEdges { get; }

    /// <inheritdoc />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double CommonLineGap { get; }

    /// <inheritdoc />
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double GeometryTolerance { get; }

    [JsonIgnore]
    public IEnumerable<NoFitPolygon> PolygonsForExport
    {
      get
      {
        return this.PartPlacements.Select(
                o =>
                {
                  var result = new NoFitPolygon(o.Part, WithChildren.Included);
                  result.Sheet = this.Sheet;
                  result.X = o.X;
                  result.Y = o.Y;
                  result.IsMirrored = o.IsMirrored; // carry the mirror to the exporter (it reloads the original file)
                  return result;
                });
      }
    }

    public async Task ExportDxf(Stream stream, bool mergeLines, bool differentiateChildren)
    {
      await new DxfExporter().Export(stream, this, mergeLines || this.RequiresMergedLines, differentiateChildren);
    }

    public static SheetPlacement LoadFromFile(string fileName)
    {
      using (StreamReader inputFile = new StreamReader(fileName))
      {
        return FromJson(inputFile.ReadToEnd());
      }
    }

    public static SheetPlacement FromJson(string json)
    {
      var options = new JsonSerializerOptions();
      options.Converters.Add(new SheetJsonConverter());
      options.Converters.Add(new NfpJsonConverter());
      options.Converters.Add(new PartPlacementJsonConverter());
      return JsonSerializer.Deserialize<SheetPlacement>(json, options);
    }

    public override string ToJson(bool writeIndented = false)
    {
      var options = new JsonSerializerOptions();
      options.Converters.Add(new SheetJsonConverter());
      options.Converters.Add(new NfpJsonConverter());
      options.Converters.Add(new PartPlacementJsonConverter());
      options.WriteIndented = writeIndented;
      return JsonSerializer.Serialize(this, options);
    }

    public override string ToString()
    {
      return this.Fitness.ToString();
    }

    internal static PolygonBounds CombinedRectBounds(IReadOnlyList<IPartPlacement> partPlacements)
    {
      NoFitPolygon allpoints = CombinedPoints(partPlacements);
      return GeometryUtil.GetPolygonBounds(allpoints);
    }

    internal static NoFitPolygon CombinedPoints(IReadOnlyList<IPartPlacement> partPlacements)
    {
      var allPoints = new List<SvgPoint>();
      for (var partIndex = 0; partIndex < partPlacements.Count; partIndex++)
      {
        var length = partPlacements[partIndex].Part.Points.Length;
        for (var pointIndex = 0; pointIndex < length; pointIndex++)
        {
          var part = partPlacements[partIndex].Part.Points[pointIndex];
          var placement = partPlacements[partIndex];
          allPoints.Add(
              new SvgPoint(
               part.X + placement.X,
               part.Y + placement.Y));
        }
      }

      return new NoFitPolygon(allPoints);
    }
  }
}
