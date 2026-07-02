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

    // US stock sheet sizes, width = the LONG side (matches the machine bed orientation; the nester
    // grows the pack along the long axis so the remnant is a full short-dimension strip).
    private static readonly SheetPreset[] SheetPresets =
    {
      new SheetPreset("96 × 48 in   (8 × 4 ft)", 96, 48),
      new SheetPreset("120 × 48 in   (10 × 4 ft)", 120, 48),
      new SheetPreset("120 × 60 in   (10 × 5 ft)", 120, 60),
      new SheetPreset("144 × 48 in   (12 × 4 ft)", 144, 48),
      new SheetPreset("144 × 60 in   (12 × 5 ft)", 144, 60),
    };

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
      // Start ready: ensure a project exists so the operator can load parts/sheets immediately.
      if (ViewModel.ActiveDocument == null && ViewModel.CreateNestProjectCommand.CanExecute(null))
      {
        ViewModel.CreateNestProjectCommand.Execute(null);
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

      // Part spacing default is 10 (legacy SVG-scale units) — that's TEN INCHES between parts on inch DXFs,
      // which blows up the raster spacing-halo so large it dissolves interlockable concavities (triangles
      // stop rotating) and leaves huge gaps in every nest. Replace only the EXACT legacy default with a
      // sane 0.25"; any other persisted value is a deliberate user setting and must not be clobbered.
      // A persisted 0 is ALSO normalized to 0.25" (the shop rule: spacing ≥ 2× material thickness, so
      // 0.25 covers up to 1/8" plate): a global default of "every part touches" is never what a laser
      // job wants — true common-line cutting is a deliberate per-part choice in Edit Part.
      if (cfg.Spacing == 10.0 || cfg.Spacing <= 0.0)
      {
        cfg.Spacing = 0.25;
      }

      this.sheetPresetCombo.ItemsSource = SheetPresets;
    }

    private void OnSheetPresetSelected(object sender, SelectionChangedEventArgs e)
    {
      if (this.sheetPresetCombo.SelectedItem is SheetPreset preset)
      {
        var sheet = (this.sheetsListView.SelectedItem
                     ?? (this.sheetsListView.Items.Count > 0 ? this.sheetsListView.Items[0] : null)) as ISheetLoadInfo;

        // No sheet yet? Picking a stock size should just create one — not silently do nothing.
        if (sheet == null && ViewModel.ActiveDocument is NestProjectViewModel doc && doc.AddSheetCommand.CanExecute(null))
        {
          doc.AddSheetCommand.Execute(null);
          sheet = doc.ProjectInfo.SheetLoadInfos.LastOrDefault();
        }

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

    private async void OnAddPartClicked(object sender, RoutedEventArgs e)
    {
      var doc = ViewModel.ActiveDocument as NestProjectViewModel;
      if (doc == null)
      {
        return;
      }

      int before = doc.ProjectInfo.DetailLoadInfos.Count;
      await doc.AddPartCommand.ExecuteAsync(null);

      // Radan-style insert flow: adding a single part opens Edit Part right away (quantity,
      // orientations, priority). A multi-file add skips it — a dialog per file would be a nuisance.
      var infos = doc.ProjectInfo.DetailLoadInfos;
      if (infos.Count == before + 1 && infos[infos.Count - 1] is IDetailLoadInfo added)
      {
        OpenEditPart(added);
      }
    }

    private void OnEditPart(object sender, RoutedEventArgs e)
    {
      if ((sender as FrameworkElement)?.Tag is IDetailLoadInfo part)
      {
        OpenEditPart(part);
      }
    }

    private void OnPartsListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      if (this.partsListView.SelectedItem is IDetailLoadInfo part)
      {
        OpenEditPart(part);
      }
    }

    private void OpenEditPart(IDetailLoadInfo part)
    {
      var cfg = ViewModel.SvgNestConfigViewModel.SvgNestConfig;
      var dialog = new EditPartWindow(part, cfg.Rotations, System.Math.Max(0, cfg.Spacing))
      {
        Owner = this,
      };
      if (dialog.ShowDialog() == true)
      {
        this.partsListView.Items.Refresh(); // plain (non-observable) fields may have changed
      }
    }

    private async void OnNest(object sender, RoutedEventArgs e)
    {
      // SheetNest has ONE engine: the raster nester (pure C#, CPU — runs on any machine). The old
      // NFP/GA engine was removed from the product: slower and consistently worse on real parts.
      var project = (ViewModel.ActiveDocument as NestProjectViewModel)?.ProjectInfo;
      if (project == null || project.DetailLoadInfos.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox("Open a project and add parts first.", "Nest", DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      // Read the UI-bound project data on the UI thread, then do the heavy nest off-thread so the app
      // stays responsive (it was freezing because the whole nest ran on the UI thread).
      var parts = project.DetailLoadInfos
        .Where(o => o.IsIncluded && !string.IsNullOrWhiteSpace(o.Path))
        .Select(o => new RasterPartInfo
        {
          Path = o.Path,
          Quantity = o.Quantity + o.Extra,               // required + spares
          Rotations = o.Rotations,                       // -1 = engine default
          Priority = o.Priority,                         // higher nests first
          Spacing = o.CommonLine ? 0.0 : o.Spacing,      // common-line = touch; -1 = job default
        })
        .ToList();
      var sheet = project.SheetLoadInfos.FirstOrDefault();
      int sheetW = sheet?.Width ?? 0;
      int sheetH = sheet?.Height ?? 0;
      int sheetQty = sheet?.Quantity ?? 0;   // the Sheets tab Qty = how many sheets the job may use
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
          // 24 px/inch (was 8): triples the raster resolution so the safety halo + spacing gaps shrink from
          // ~0.125"+ down to ~0.04" — much tighter nesting with the placement still overlap-free (verified
          // in GpuNestLab). Slower, but the closed-marking optimization keeps it fast enough for real jobs.
          var r = RasterNestService.Nest(parts, sheetW, sheetH, sheetQty, placementType, rotations, spacing, margin, 24.0, out string err);
          return (r, err);
        });

        if (result == null)
        {
          ViewModel.MessageService.DisplayMessageBox(error ?? "Nest produced no result.", "Nest", DeepNestLib.MessageBoxIcon.Information);
          return;
        }

        // Hand the per-part spacings to the viewer BEFORE showing the result, so manual nesting
        // (drag/rotate/nudge) enforces the same clearances the nester used — common-line parts may
        // touch, spaced parts keep (spacingA + spacingB) / 2.
        if (this.dxfViewer != null)
        {
          this.dxfViewer.DefaultPartSpacing = System.Math.Max(0, spacing);
          this.dxfViewer.PartSpacings = parts.ToDictionary(
            p => p.Path,
            p => p.Spacing >= 0 ? p.Spacing : System.Math.Max(0, spacing),
            System.StringComparer.OrdinalIgnoreCase);
        }

        // Show it in the results list + viewer + status bar so utilization / placed / sheets / fitness /
        // time all appear. The result must be IN TopNestResults first, otherwise the list's two-way
        // SelectedItem binding resets the selection (and the stats) back to null.
        var vm = ViewModel.NestMonitorViewModel;
        vm.TopNestResults.SetSingleResult(result);
        vm.SelectedItem = result;

        // Not enough sheets for the whole order? Say so clearly — don't let a partial nest pass as done.
        int unplacedCount = result.UnplacedParts?.Count ?? 0;
        if (unplacedCount > 0)
        {
          ViewModel.MessageService.DisplayMessageBox(
            $"{unplacedCount} part(s) did not fit on the {sheetQty} available sheet(s).\n\n" +
            "Increase the sheet Qty in the Sheets tab (or add another sheet) and nest again.",
            "Not enough sheets",
            DeepNestLib.MessageBoxIcon.Warning);
        }
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
