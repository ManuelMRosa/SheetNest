namespace DeepNestLib.NestProject
{
  public interface IProjectInfo
  {
    ISvgNestConfig Config { get; }

    IList<IDetailLoadInfo, DetailLoadInfo> DetailLoadInfos { get; }

    IList<ISheetLoadInfo, SheetLoadInfo> SheetLoadInfos { get; }

    /// <summary>
    /// Gets or sets the nest result that was on screen when the project was saved (NestResult json,
    /// the .dnr format), so reopening the project restores the nest. Null/empty = no result saved.
    /// </summary>
    string LastNestResultJson { get; set; }

    /// <summary>Gets or sets this job's cut width in MILLIMETRES; -1 = not set.</summary>
    double KerfMm { get; set; }

    void Load(ISvgNestConfig config, string filePath);

    void Load(ProjectInfo source);

    string ToJson();
  }
}