namespace DeepNestSharp.Ui.Views
{
  using System.Linq;
  using System.Threading.Tasks;
  using System.Windows;
  using System.Windows.Controls;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.Domain.ViewModels;
  using DeepNestSharp.RasterNest;

  public partial class MainWindow : Window
  {
    public MainWindow(IMainViewModel viewModel)
    {
      InitializeComponent();
      this.DataContext = viewModel;
      viewModel.AboutDialogService = new AboutDialogService(() => new AboutDialog());
      this.Loaded += MainWindow_Loaded;
    }

    public IMainViewModel ViewModel => (IMainViewModel)DataContext;

    private static readonly SheetPreset[] SheetPresets =
    {
      new SheetPreset("48 × 96 in", 48, 96),
      new SheetPreset("48 × 120 in", 48, 120),
      new SheetPreset("60 × 120 in", 60, 120),
      new SheetPreset("60 × 144 in", 60, 144),
      new SheetPreset("120 × 60 in", 120, 60),
    };

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
      // Start ready: ensure a project exists so the operator can load parts/sheets immediately.
      if (ViewModel.ActiveDocument == null && ViewModel.CreateNestProjectCommand.CanExecute(null))
      {
        ViewModel.CreateNestProjectCommand.Execute(null);
      }

      // Reflect the current "nests per run" cap in the control.
      if (this.nestsPerRun != null)
      {
        this.nestsPerRun.Value = DeepNestLib.SvgNest.MaxNests;
      }

      // Sensible defaults: always use all cores; drop the removed "Squeeze" arrangement.
      var cfg = ViewModel.SvgNestConfigViewModel.SvgNestConfig;
      cfg.UseParallel = true;
      if (cfg.PlacementType == PlacementTypeEnum.Squeeze)
      {
        cfg.PlacementType = PlacementTypeEnum.BoundingBox;
      }

      // CRITICAL for inch parts: the engine's curve/simplify tolerances are in the drawing's units.
      // The 0.72 default assumes the old SVG ~72-units/inch scale; on 1-unit/inch DXFs it's ~72× too
      // big, so the Douglas-Peucker pass (4×tol = 2.88") erases tabs/detail and the nest then ignores
      // real spacing/edge gaps. 0.01" keeps the geometry true so spacing and sheet margin are honoured.
      cfg.CurveTolerance = 0.01;

      this.sheetPresetCombo.ItemsSource = SheetPresets;

      // Auto-enable GPU nesting when the machine has a GPU. WPF's render tier is a dependency-free proxy:
      // tier >= 1 means hardware (GPU) acceleration is available; tier 0 = software-only (no usable GPU).
      if (this.gpuNestingToggle != null)
      {
        int gpuTier = System.Windows.Media.RenderCapability.Tier >> 16;
        bool hasGpu = gpuTier >= 1;
        this.gpuNestingToggle.IsChecked = hasGpu;
        this.gpuStatusText.Text = hasGpu
          ? "GPU detected — NEST uses the GPU engine by default."
          : "No GPU detected — NEST uses the CPU engine.";
      }
    }

    private void OnSheetPresetSelected(object sender, SelectionChangedEventArgs e)
    {
      if (this.sheetPresetCombo.SelectedItem is SheetPreset preset)
      {
        var sheet = (this.sheetsListView.SelectedItem
                     ?? (this.sheetsListView.Items.Count > 0 ? this.sheetsListView.Items[0] : null)) as ISheetLoadInfo;
        if (sheet != null)
        {
          sheet.Width = preset.Width;
          sheet.Height = preset.Height;
          this.sheetsListView.Items.Refresh();
        }

        this.sheetPresetCombo.SelectedIndex = -1; // reset so picking the same preset again works
      }
    }

    private sealed class SheetPreset
    {
      public SheetPreset(string name, int width, int height)
      {
        this.Name = name;
        this.Width = width;
        this.Height = height;
      }

      public string Name { get; }

      public int Width { get; }

      public int Height { get; }
    }

    private void OnNestsPerRunChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<object> e)
    {
      if (e.NewValue is int v && v > 0)
      {
        DeepNestLib.SvgNest.MaxNests = v;
      }
    }

    private async void OnNest(object sender, RoutedEventArgs e)
    {
      // One NEST button: GPU (raster) engine when GPU nesting is enabled in Settings, else the CPU (NFP) engine.
      bool useGpu = this.gpuNestingToggle?.IsChecked == true;
      if (!useGpu)
      {
        var nestCommand = (ViewModel.ActiveDocument as NestProjectViewModel)?.ExecuteNestCommand;
        if (nestCommand != null && nestCommand.CanExecute(null))
        {
          nestCommand.Execute(null);
        }

        return;
      }

      var project = (ViewModel.ActiveDocument as NestProjectViewModel)?.ProjectInfo;
      if (project == null || project.DetailLoadInfos.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox("Open a project and add parts first.", "GPU Nest", DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      // Read the UI-bound project data on the UI thread, then do the heavy nest off-thread so the app
      // stays responsive (it was freezing because the whole nest ran on the UI thread).
      var parts = project.DetailLoadInfos
        .Where(o => o.IsIncluded && !string.IsNullOrWhiteSpace(o.Path))
        .Select(o => (o.Path, o.Quantity))
        .ToList();
      var sheet = project.SheetLoadInfos.FirstOrDefault();
      int sheetW = sheet?.Width ?? 0;
      int sheetH = sheet?.Height ?? 0;
      var config = ViewModel.SvgNestConfigViewModel.SvgNestConfig;
      var placementType = config.PlacementType;
      int rotations = config.Rotations;
      double spacing = config.Spacing;          // part spacing (drawing units = inches)
      double margin = config.SheetSpacing;      // sheet edge margin

      var button = sender as Button;
      if (button != null)
      {
        button.IsEnabled = false;
      }

      try
      {
        var (result, error) = await Task.Run(() =>
        {
          var r = RasterNestService.Nest(parts, sheetW, sheetH, placementType, rotations, spacing, margin, 8.0, out string err);
          return (r, err);
        });

        if (result == null)
        {
          ViewModel.MessageService.DisplayMessageBox(error ?? "GPU nest produced no result.", "GPU Nest", DeepNestLib.MessageBoxIcon.Information);
          return;
        }

        // Show it in the results list + viewer + status bar so utilization / placed / sheets / fitness /
        // time all appear. The result must be IN TopNestResults first, otherwise the list's two-way
        // SelectedItem binding resets the selection (and the stats) back to null. (NFP engine untouched.)
        var vm = ViewModel.NestMonitorViewModel;
        vm.TopNestResults.SetSingleResult(result);
        vm.SelectedItem = result;
      }
      finally
      {
        if (button != null)
        {
          button.IsEnabled = true;
        }
      }
    }

    private async void OnExportDxf(object sender, RoutedEventArgs e)
    {
      var selected = ViewModel.NestMonitorViewModel?.SelectedItem;
      if (selected == null || selected.UsedSheets == null || selected.UsedSheets.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox(
          "Run a nest and select a result first, then export.",
          "Export DXF",
          DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      // Export one DXF per DISTINCT layout (the production plan tells how many of each to cut), so two
      // identical sheets don't each produce a duplicate DXF. Fall back to every sheet if no grouping.
      var layouts = this.dxfViewer.GetDistinctLayoutSheets();
      if (layouts != null && layouts.Count > 0)
      {
        foreach (var sheetPlacement in layouts)
        {
          await ViewModel.ExportSheetPlacementAsync(sheetPlacement);
        }
      }
      else
      {
        foreach (var sheetPlacement in selected.UsedSheets.ToList())
        {
          await ViewModel.ExportSheetPlacementAsync(sheetPlacement);
        }
      }
    }
  }
}
