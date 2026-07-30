namespace DeepNestSharp.Domain.ViewModels
{
  using System;
  using System.Linq;
  using System.Threading.Tasks;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestSharp.Domain.Models;
  using DeepNestSharp.Domain.Services;
  using DeepNestSharp.Ui.Docking;
  using DeepNestSharp.Ui.Models;
  using Light.GuardClauses;
  using Microsoft.Toolkit.Mvvm.Input;

  public class NestProjectViewModel : FileViewModel, INestProjectViewModel
  {
    private int selectedDetailLoadInfoIndex;
    private IDetailLoadInfo selectedDetailLoadInfo;
    private int selectedSheetLoadInfoIndex;
    private ISheetLoadInfo selectedSheetLoadInfo;
    private AsyncRelayCommand executeNestCommand;
    private AsyncRelayCommand addPartCommand;
    private AsyncRelayCommand addPart3DCommand;
    private RelayCommand addSheetCommand;
    private RelayCommand clearPartsCommand;
    private RelayCommand<IDetailLoadInfo> removePartCommand;
    private RelayCommand<ISheetLoadInfo> removeSheetCommand;
    private RelayCommand<string> loadPartCommand;
    private IFileIoService fileIoService;
    private ObservableProjectInfo observableProjectInfo;

    /// <summary>
    /// Initializes a new instance of the <see cref="NestProjectViewModel"/> class.
    /// </summary>
    /// <param name="mainViewModel">MainViewModel singleton; the primary context; access this via the activeDocument property.</param>
    public NestProjectViewModel(IMainViewModel mainViewModel, IFileIoService fileIoService)
      : base(mainViewModel)
    {
      Initialise(mainViewModel, fileIoService);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NestProjectViewModel"/> class.
    /// </summary>
    /// <param name="mainViewModel">MainViewModel singleton; the primary context; access this via the activeDocument property.</param>
    /// <param name="filePath">Path to the file to open.</param>
    public NestProjectViewModel(IMainViewModel mainViewModel, string filePath, IFileIoService fileIoService)
      : base(mainViewModel, filePath)
    {
      Initialise(mainViewModel, fileIoService);
    }

    public IAsyncRelayCommand AddPartCommand => addPartCommand ?? (addPartCommand = new AsyncRelayCommand(OnAddPartAsync));

    /// <summary>Add a 3D (STEP/IGES) part: the file dialog is filtered to 3D; the unfold happens on load.</summary>
    public IAsyncRelayCommand AddPart3DCommand => addPart3DCommand ?? (addPart3DCommand = new AsyncRelayCommand(OnAddPart3DAsync));

    public IRelayCommand AddSheetCommand => addSheetCommand ?? (addSheetCommand = new RelayCommand(OnAddSheet));

    public IRelayCommand ClearPartsCommand => clearPartsCommand ?? (clearPartsCommand = new RelayCommand(OnClearParts));

    public IRelayCommand<IDetailLoadInfo> RemovePartCommand => removePartCommand ?? (removePartCommand = new RelayCommand<IDetailLoadInfo>(OnRemovePart));

    public IRelayCommand<ISheetLoadInfo> RemoveSheetCommand => removeSheetCommand ?? (removeSheetCommand = new RelayCommand<ISheetLoadInfo>(OnRemoveSheet));

    public AsyncRelayCommand ExecuteNestCommand => this.executeNestCommand ?? (this.executeNestCommand = new AsyncRelayCommand(this.OnExecuteNest, CanExecuteNest));

    private bool CanExecuteNest()
    {
      if (MainViewModel.NestMonitorViewModel.IsRunning || this.ProjectInfo.DetailLoadInfos.Count == 0)
      {
        return false;
      }

      return !this.ProjectInfo.DetailLoadInfos.Any(o => o is ObservableDetailLoadInfo cast && !cast.IsValid);
    }

    public override string FileDialogFilter => DeepNestLib.NestProject.ProjectInfo.FileDialogFilter;

    public IRelayCommand<string> LoadPartCommand => loadPartCommand ?? (loadPartCommand = new RelayCommand<string>(OnLoadPart));

    public IProjectInfo ProjectInfo => observableProjectInfo ?? (observableProjectInfo = new ObservableProjectInfo(MainViewModel));

    public IDetailLoadInfo SelectedDetailLoadInfo
    {
#pragma warning disable CS8603 // Possible null reference return.
      get => selectedDetailLoadInfo;
#pragma warning restore CS8603 // Possible null reference return.
      set => SetProperty(ref selectedDetailLoadInfo, value, nameof(SelectedDetailLoadInfo));
    }

    public int SelectedDetailLoadInfoIndex
    {
      get => selectedDetailLoadInfoIndex;
      set => SetProperty(ref selectedDetailLoadInfoIndex, value);
    }

    public ISheetLoadInfo SelectedSheetLoadInfo
    {
#pragma warning disable CS8603 // Possible null reference return.
      get => selectedSheetLoadInfo;
#pragma warning restore CS8603 // Possible null reference return.
      set => SetProperty(ref selectedSheetLoadInfo, value, nameof(SelectedSheetLoadInfo));
    }

    public int SelectedSheetLoadInfoIndex
    {
      get => selectedSheetLoadInfoIndex;
      set => SetProperty(ref selectedSheetLoadInfoIndex, value);
    }

    public override string TextContent { get => this.ProjectInfo.ToJson(); }

    public bool UsePriority => this.MainViewModel.SvgNestConfigViewModel.SvgNestConfig.UsePriority;

    protected override void LoadContent()
    {
      this.ProjectInfo.Load(this.MainViewModel.SvgNestConfigViewModel.SvgNestConfig, this.FilePath);
      this.ExecuteNestCommand.MustNotBeNull();
      this.executeNestCommand.NotifyCanExecuteChanged();
    }

    protected override void NotifyContentUpdated()
    {
      Contextualise();
      OnPropertyChanged(nameof(SelectedDetailLoadInfoIndex));
      OnPropertyChanged(nameof(SelectedDetailLoadInfo));
    }

    private void Initialise(IMainViewModel mainViewModel, IFileIoService fileIoService)
    {
      this.ProjectInfo.MustBe(observableProjectInfo);
      if (this.observableProjectInfo != null)
      {
        this.observableProjectInfo.IsDirtyChanged += this.ObservableProjectInfo_IsDirtyChanged;
      }

      mainViewModel.NestMonitorViewModel.PropertyChanged += this.NestMonitorViewModel_PropertyChanged;
      this.fileIoService = fileIoService;
    }

    private void NestMonitorViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
      if (MainViewModel.DispatcherService.InvokeRequired)
      {
        MainViewModel.DispatcherService.Invoke(() => NestMonitorViewModel_PropertyChanged(sender, e));
      }

      if (e.PropertyName == $"{nameof(INestMonitorViewModel.IsRunning)}")
      {
        MainViewModel.DispatcherService.Invoke(() => executeNestCommand?.NotifyCanExecuteChanged());
      }
    }

    private void ObservableProjectInfo_IsDirtyChanged(object sender, EventArgs e)
    {
      this.IsDirty = true;
    }

    private Task OnAddPartAsync() => AddPartsAsync(NoFitPolygon.FileDialogFilter);

    private Task OnAddPart3DAsync() => AddPartsAsync(DeepNestLib.IO.StepUnfoldService.FileDialogFilter3D);

    private async Task AddPartsAsync(string fileDialogFilter)
    {
      var filePaths = await this.fileIoService.GetOpenFilePathsAsync(fileDialogFilter);
      foreach (var filePath in filePaths)
      {
        if (string.IsNullOrWhiteSpace(filePath) || !this.fileIoService.Exists(filePath))
        {
          continue;
        }

        if (DeepNestLib.IO.StepUnfoldService.IsStepFile(filePath))
        {
          // Add Part is 2D-only by design. 3D imports go through File > Import 3D, which probes the
          // sheet thickness and asks for the K-factor — never unfold silently with defaults here.
          this.MainViewModel.MessageService.DisplayMessageBox(
            "To import a 3D part use File > Import 3D (STEP / IGES)... It detects the sheet thickness and lets you set the K-factor.",
            "Add Part",
            DeepNestLib.MessageBoxIcon.Information);
          continue;
        }
        else
        {
          // Nothing checked the file here before: the part went in as a bare path, the thumbnail swallowed
          // whatever went wrong, and the operator only found out at NEST - by which time the job is built
          // around a part that cannot be cut. Read it now and refuse what cannot be used, saying why.
          var reason = await Task.Run(() => DescribeLoadFailure(filePath));
          if (reason != null)
          {
            this.MainViewModel.MessageService.DisplayMessageBox(reason, "Add Part", MessageBoxIcon.Stop);
            continue;
          }

          observableProjectInfo?.DetailLoadInfos.Add(new DetailLoadInfo() { Path = filePath });
        }
      }

      Contextualise();
      this.IsDirty = true;
    }

    /// <summary>Null when the file yields a shape that can be nested; otherwise why it cannot, in words an
    /// operator can act on.</summary>
    private static string DescribeLoadFailure(string filePath)
    {
      var name = System.IO.Path.GetFileName(filePath);
      try
      {
        var detail = new NestExecutionHelper().LoadRawDetail(new System.IO.FileInfo(filePath));
        if (detail == null || !detail.TryConvertToNfp(0, out INfp nfp) || nfp.Points.Length < 3)
        {
          return $"{name} holds no shape that can be nested.";
        }

        return null;
      }
      catch (Exception ex)
      {
        return $"{name} cannot be used:{Environment.NewLine}{Environment.NewLine}{ex.GetBaseException().Message}";
      }
    }

    /// <summary>Metadata for one flat produced by a 3D unfold — lets the part be re-unfolded later
    /// (e.g. when the user changes its K-factor in Edit Part) and rebuilt if the temp DXF is gone.</summary>
    public readonly record struct UnfoldedPartInfo(
      string FlatDxfPath, string SourceStepPath, int UnfoldIndex,
      double KFactor, string KFactorStandard, bool UnfoldUnitInch, double ThicknessMm);

    /// <summary>Adds flats from a 3D unfold, each carrying its source STEP + K-factor + thickness.</summary>
    public void AddUnfoldedParts(System.Collections.Generic.IEnumerable<UnfoldedPartInfo> parts)
    {
      foreach (var p in parts)
      {
        if (string.IsNullOrWhiteSpace(p.FlatDxfPath))
        {
          continue;
        }

        observableProjectInfo?.DetailLoadInfos.Add(new DetailLoadInfo()
        {
          Path = p.FlatDxfPath,
          SourceStepPath = p.SourceStepPath,
          UnfoldIndex = p.UnfoldIndex,
          KFactor = p.KFactor,
          KFactorStandard = p.KFactorStandard,
          UnfoldUnitInch = p.UnfoldUnitInch,
          ThicknessMm = p.ThicknessMm,
        });
      }

      Contextualise();
      this.IsDirty = true;
    }

    /// <summary>Metadata for one part imported from a SheetCam .nest — lets the arrangement be written
    /// back into that file and the temp DXF rebuilt if it is gone.</summary>
    public readonly record struct NestPartInfo(
      string DxfPath, string NestSourcePath, string NestPartName, bool NestUnitInch, int Quantity, bool CommonLine);

    /// <summary>Adds the parts of a SheetCam .nest, each carrying its source file and the quantity the job wants.</summary>
    public void AddNestParts(System.Collections.Generic.IEnumerable<NestPartInfo> parts)
    {
      foreach (var p in parts)
      {
        if (string.IsNullOrWhiteSpace(p.DxfPath))
        {
          continue;
        }

        observableProjectInfo?.DetailLoadInfos.Add(new DetailLoadInfo()
        {
          Path = p.DxfPath,
          Quantity = p.Quantity,
          NestSourcePath = p.NestSourcePath,
          NestPartName = p.NestPartName,
          NestUnitInch = p.NestUnitInch,
          CommonLine = p.CommonLine,
        });
      }

      Contextualise();
      this.IsDirty = true;
    }

    /// <summary>Replaces the project's sheet stock with the given sizes — used when a SheetCam .nest is
    /// imported, so the job nests on the sheet SheetCam set it up for.</summary>
    public void SetSheets(System.Collections.Generic.IEnumerable<(int Width, int Height, int Quantity)> sheets)
    {
      if (observableProjectInfo == null)
      {
        return;
      }

      observableProjectInfo.SheetLoadInfos.Clear();
      foreach (var s in sheets)
      {
        if (s.Width > 0 && s.Height > 0 && s.Quantity > 0)
        {
          observableProjectInfo.SheetLoadInfos.Add(new SheetLoadInfo(s.Width, s.Height, s.Quantity));
        }
      }

      Contextualise();
      this.IsDirty = true;
    }

    private void OnAddSheet()
    {
      var newSheet = new SheetLoadInfo(this.ProjectInfo.Config);
      observableProjectInfo?.SheetLoadInfos.Add(newSheet);

      Contextualise();
      this.IsDirty = true;
    }

    private void OnClearParts()
    {
      observableProjectInfo?.DetailLoadInfos.Clear();
      Contextualise();
      this.IsDirty = true;
    }

    private void Contextualise()
    {
      OnPropertyChanged(nameof(ProjectInfo));

      // Use the property (lazy-inits the command), not the field: with the redesigned UI the NEST button
      // may run the raster engine and never touch ExecuteNestCommand, leaving the field null → NRE here
      // on Add/Remove Part/Sheet. The property guarantees the command exists before we refresh it.
      this.ExecuteNestCommand.NotifyCanExecuteChanged();
    }

    private async Task OnExecuteNest()
    {
      MainViewModel.SetSelectedToolView(this);
      await MainViewModel.NestMonitorViewModel.TryStartAsync(this).ConfigureAwait(false);
    }

    private void OnLoadPart(string path)
    {
      if (!string.IsNullOrWhiteSpace(path))
      {
        MainViewModel.LoadPart(path);
      }
    }

    private void OnRemovePart(IDetailLoadInfo arg)
    {
      if (arg != null)
      {
        this.ProjectInfo.DetailLoadInfos.Remove(arg);
        Contextualise();
      }
    }

    private void OnRemoveSheet(ISheetLoadInfo arg)
    {
      if (arg != null)
      {
        this.ProjectInfo.SheetLoadInfos.Remove(arg);
        Contextualise();
      }
    }

    protected override void SaveState()
    {
      observableProjectInfo.SaveState();
    }
  }
}