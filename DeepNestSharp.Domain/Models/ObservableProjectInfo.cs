namespace DeepNestSharp.Ui.Models
{
  using System;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.Domain.Models;
  using DeepNestSharp.Domain.ViewModels;
  using Light.GuardClauses;
  using Microsoft.Toolkit.Mvvm.ComponentModel;

  public class ObservableProjectInfo : ObservableObject, IProjectInfo
  {
    private readonly IMainViewModel mainViewModel;
    private readonly IProjectInfo wrappedProjectInfo;
    private ObservableCollection<IDetailLoadInfo, DetailLoadInfo, ObservableDetailLoadInfo>? detailLoadInfos;
    private ObservableCollection<ISheetLoadInfo, SheetLoadInfo, ObservableSheetLoadInfo>? sheetLoadInfos;
    private ObservableSvgNestConfig? observableConfig;

    /// <summary>
    /// Initializes a new instance of the <see cref="ObservableProjectInfo"/> class.
    /// </summary>
    /// <param name="projectInfo">The ProjectInfo to wrap.</param>
    public ObservableProjectInfo(IMainViewModel mainViewModel)
    {
      this.mainViewModel = mainViewModel;
      this.wrappedProjectInfo = new ProjectInfo(mainViewModel.SvgNestConfigViewModel.SvgNestConfig);
    }

    public event EventHandler? IsDirtyChanged;

    // The wrapper collections MUST keep a stable identity while a project is open: rebuilding on
    // "Count == 0" (as this used to) hands every early caller a fresh instance, so the ListView
    // ends up bound to an orphaned empty copy while adds land in a newer one — items exist in the
    // data but never show. Rebuild ONLY on Load(), which raises PropertyChanged so bindings re-fetch.
    public IList<IDetailLoadInfo, DetailLoadInfo> DetailLoadInfos
    {
      get
      {
        if (this.detailLoadInfos == null)
        {
          this.detailLoadInfos = new ObservableCollection<IDetailLoadInfo, DetailLoadInfo, ObservableDetailLoadInfo>(this.wrappedProjectInfo.DetailLoadInfos, x => new ObservableDetailLoadInfo(x));
          this.detailLoadInfos.IsDirtyChanged += this.DetailLoadInfos_IsDirtyChanged;
        }

        return this.detailLoadInfos;
      }
    }

    public IList<ISheetLoadInfo, SheetLoadInfo> SheetLoadInfos
    {
      get
      {
        if (this.sheetLoadInfos == null)
        {
          sheetLoadInfos = new ObservableCollection<ISheetLoadInfo, SheetLoadInfo, ObservableSheetLoadInfo>(this.wrappedProjectInfo.SheetLoadInfos, x => new ObservableSheetLoadInfo(x));
        }

        return this.sheetLoadInfos;
      }
    }

    public ISvgNestConfig Config
    {
      get
      {
        if (this.observableConfig == null)
        {
          this.observableConfig = (ObservableSvgNestConfig)mainViewModel.SvgNestConfigViewModel.SvgNestConfig;
        }

        return this.observableConfig;
      }
    }

    /// <inheritdoc />
    public double CommonLineGapInches
    {
      get => this.wrappedProjectInfo.CommonLineGapInches;
      set
      {
        if (Math.Abs(this.wrappedProjectInfo.CommonLineGapInches - value) <= 1e-12)
        {
          return;
        }

        this.wrappedProjectInfo.CommonLineGapInches = value;
        OnPropertyChanged(nameof(CommonLineGapInches));
      }
    }

    /// <inheritdoc />
    public int CommonLineMaxClusterParts
    {
      get => this.wrappedProjectInfo.CommonLineMaxClusterParts;
      set
      {
        int normalized = CommonLineDefaults.NormalizeMaxClusterParts(value);
        if (this.wrappedProjectInfo.CommonLineMaxClusterParts == normalized)
        {
          return;
        }

        this.wrappedProjectInfo.CommonLineMaxClusterParts = normalized;
        OnPropertyChanged(nameof(CommonLineMaxClusterParts));
      }
    }

    public string LastNestResultJson
    {
      get => wrappedProjectInfo.LastNestResultJson;
      set => wrappedProjectInfo.LastNestResultJson = value;
    }

    private void DetailLoadInfos_IsDirtyChanged(object? sender, EventArgs e)
    {
      IsDirtyChanged?.Invoke(this, e);
    }

    public void Load(ISvgNestConfig config, string filePath)
    {
      // This is a fudge; need a better way of injecting Config; loathe to go Locator but AvalonDock's pushing that way.
      config.MustBe(mainViewModel.SvgNestConfigViewModel.SvgNestConfig);
      wrappedProjectInfo.Load(config, filePath);
      RebuildSplitParts(wrappedProjectInfo);

      // The loaded data repopulated the UNDERLYING lists; drop the wrapper collections so the
      // PropertyChanged below makes bindings re-fetch fresh copies of the loaded items.
      if (this.detailLoadInfos != null)
      {
        this.detailLoadInfos.IsDirtyChanged -= this.DetailLoadInfos_IsDirtyChanged;
      }

      this.detailLoadInfos = null;
      this.sheetLoadInfos = null;
      OnPropertyChanged(nameof(DetailLoadInfos));
      OnPropertyChanged(nameof(SheetLoadInfos));
      mainViewModel.SvgNestConfigViewModel.RaiseNotifyUpdatePropertyGrid();
    }

    public void Load(ProjectInfo source)
    {
      wrappedProjectInfo.Load(source);
      RebuildSplitParts(wrappedProjectInfo);
    }

    /// <summary>
    /// Puts back the file of any part that was split out of a drawing and whose copy has since gone.
    /// <para>
    /// Those copies live in a temp folder so that importing never writes into the operator's own
    /// folders, and Windows clears temp. Without this a project saved last month would open with every
    /// split part missing, which is a job that cannot be cut and no way to tell why. The drawing they
    /// came from is recorded on each row, so putting them back is just splitting it again.
    /// </para>
    /// </summary>
    private static void RebuildSplitParts(IProjectInfo projectInfo)
    {
      foreach (IDetailLoadInfo detail in projectInfo.DetailLoadInfos)
      {
        if (string.IsNullOrEmpty(detail.SourceDxfPath) || detail.IsExists)
        {
          continue;
        }

        string rebuilt = DeepNestLib.IO.DxfPartSplitter.Rebuild(detail.SourceDxfPath, detail.PartIndex);
        if (!string.IsNullOrEmpty(rebuilt))
        {
          detail.Path = rebuilt;
        }
      }
    }

    public string ToJson()
    {
      return wrappedProjectInfo.ToJson();
    }

    public void SaveState()
    {
      detailLoadInfos?.SaveState();
    }
  }
}
