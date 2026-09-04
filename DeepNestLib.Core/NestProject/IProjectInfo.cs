namespace DeepNestLib.NestProject
{
  public interface IProjectInfo
  {
    ISvgNestConfig Config { get; }

    /// <summary>
    /// Gets or sets this document's canonical common-line gap in inches. The application config is
    /// process-wide, so geometry-affecting project values must also have a per-document snapshot.
    /// </summary>
    double CommonLineGapInches { get; set; }

    /// <summary>
    /// Gets or sets this document's common-line cluster limit. Zero selects automatic sizing;
    /// manual limits are restricted to 2..500 parts.
    /// </summary>
    int CommonLineMaxClusterParts { get; set; }

    IList<IDetailLoadInfo, DetailLoadInfo> DetailLoadInfos { get; }

    IList<ISheetLoadInfo, SheetLoadInfo> SheetLoadInfos { get; }

    /// <summary>
    /// Gets or sets the nest result that was on screen when the project was saved (NestResult json,
    /// the .dnr format), so reopening the project restores the nest. Null/empty = no result saved.
    /// </summary>
    string LastNestResultJson { get; set; }

    void Load(ISvgNestConfig config, string filePath);

    void Load(ProjectInfo source);

    string ToJson();
  }
}
