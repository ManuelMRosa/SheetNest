namespace DeepNestSharp.Ui.Views
{
  using System.Collections.Generic;
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
    // Sheets the LAST nest deducted from the stock, by size — Clear Result gives them back.
    private Dictionary<(int W, int H), int>? lastNestConsumed;

    public MainWindow(IMainViewModel viewModel)
    {
      InitializeComponent();
      this.DataContext = viewModel;
      viewModel.AboutDialogService = new AboutDialogService(() => new AboutDialog());
      this.Loaded += MainWindow_Loaded;
      this.Closing += MainWindow_Closing;
      viewModel.ActiveDocumentChanged += MainWindow_ActiveDocumentChanged;
    }

    public IMainViewModel ViewModel => (IMainViewModel)DataContext;

    // US stock sheet sizes most used in sheet-metal fabrication (mill/service-center standards),
    // width = the LONG side (matches the machine bed orientation; the nester grows the pack along
    // the long axis so the remnant is a full short-dimension strip). Grouped by the SHORT side
    // (36 / 48 / 60 / 72), then by length — the menu draws a separator between groups.
    private static readonly SheetPreset[] SheetPresets =
    {
      new SheetPreset("96 × 36 in   (8 × 3 ft)", 96, 36),
      new SheetPreset("120 × 36 in   (10 × 3 ft)", 120, 36),
      new SheetPreset("96 × 48 in   (8 × 4 ft)", 96, 48),
      new SheetPreset("120 × 48 in   (10 × 4 ft)", 120, 48),
      new SheetPreset("144 × 48 in   (12 × 4 ft)", 144, 48),
      new SheetPreset("96 × 60 in   (8 × 5 ft)", 96, 60),
      new SheetPreset("120 × 60 in   (10 × 5 ft)", 120, 60),
      new SheetPreset("144 × 60 in   (12 × 5 ft)", 144, 60),
      new SheetPreset("120 × 72 in   (10 × 6 ft)", 120, 72),
      new SheetPreset("144 × 72 in   (12 × 6 ft)", 144, 72),
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

      // Restore the previous session: the user's sheet edge margin and the sheet stock left
      // over when the app was last closed.
      var session = SessionState.Load();
      if (session != null)
      {
        if (session.SheetEdgeMargin >= 0)
        {
          cfg.SheetSpacing = session.SheetEdgeMargin;
        }

        if (ViewModel.ActiveDocument is NestProjectViewModel doc && doc.ProjectInfo.SheetLoadInfos.Count == 0)
        {
          foreach (var s in session.Sheets.Where(s => s.Width > 0 && s.Height > 0 && s.Quantity > 0))
          {
            doc.ProjectInfo.SheetLoadInfos.Add(new SheetLoadInfo(s.Width, s.Height, s.Quantity));
          }

          this.sheetsListView.Items.Refresh();
        }
      }
    }

    private void OnAdvancedSettings(object sender, RoutedEventArgs e)
    {
      var dialog = new AdvancedSettingsWindow
      {
        Owner = this,
        DataContext = ViewModel.SvgNestConfigViewModel, // the editor binds SelectedObject to SvgNestConfig
      };
      dialog.ShowDialog();
    }

    /// <summary>Discards the displayed nest result and returns the sheets it consumed to the stock.</summary>
    private void OnClearResult(object sender, RoutedEventArgs e)
    {
      bool hadResult = ViewModel.NestMonitorViewModel.SelectedItem != null;
      ViewModel.NestMonitorViewModel.Reset();

      if (hadResult && lastNestConsumed != null && ViewModel.ActiveDocument is NestProjectViewModel doc)
      {
        foreach (var kv in lastNestConsumed)
        {
          var row = doc.ProjectInfo.SheetLoadInfos.FirstOrDefault(s => s.Width == kv.Key.W && s.Height == kv.Key.H);
          if (row != null)
          {
            row.Quantity += kv.Value;
          }
          else
          {
            // The user removed the row after nesting — bring the size back so no stock is lost.
            doc.ProjectInfo.SheetLoadInfos.Add(new SheetLoadInfo(kv.Key.W, kv.Key.H, kv.Value));
          }
        }

        this.sheetsListView.Items.Refresh();
      }

      lastNestConsumed = null; // a result can only be given back once
    }

    /// <summary>
    /// A project became active (opened or created): restore the nest result saved inside it, or
    /// clear the previous project's result — the on-screen nest always belongs to the active project.
    /// </summary>
    private void MainWindow_ActiveDocumentChanged(object sender, System.EventArgs e)
    {
      if (!(ViewModel.ActiveDocument is NestProjectViewModel doc))
      {
        return;
      }

      var monitor = ViewModel.NestMonitorViewModel;
      string json = doc.ProjectInfo.LastNestResultJson;
      if (string.IsNullOrWhiteSpace(json))
      {
        monitor.Reset();
        lastNestConsumed = null;
        return;
      }

      try
      {
        var result = NestResult.FromJson(json);
        if (result == null || result.UsedSheets.Count == 0)
        {
          monitor.Reset();
          lastNestConsumed = null;
          return;
        }

        // Same clearance setup the nest itself hands the viewer, so manual editing of the restored
        // result enforces the same rules.
        if (this.dxfViewer != null)
        {
          this.dxfViewer.DefaultPartSpacing = System.Math.Max(0, ViewModel.SvgNestConfigViewModel.SvgNestConfig.Spacing);
          this.dxfViewer.PartSpacings = doc.ProjectInfo.DetailLoadInfos
            .Where(o => !string.IsNullOrWhiteSpace(o.Path))
            .GroupBy(o => o.Path, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
              g => g.Key,
              g => g.Min(o => o.CommonLine ? 0.0 : (o.Spacing >= 0 ? o.Spacing : System.Math.Max(0, ViewModel.SvgNestConfigViewModel.SvgNestConfig.Spacing))),
              System.StringComparer.OrdinalIgnoreCase);
        }

        monitor.TopNestResults.SetSingleResult(result);
        monitor.SelectedItem = result;

        // The project file stores the stock AS DEDUCTED by this nest, so Clear Result must be able
        // to give those sheets back — rebuild the consumed-by-size record from the result itself.
        var restoredConsumed = new Dictionary<(int W, int H), int>();
        foreach (var sp in result.UsedSheets)
        {
          var key = ((int)System.Math.Round(sp.Sheet.WidthCalculated), (int)System.Math.Round(sp.Sheet.HeightCalculated));
          restoredConsumed[key] = restoredConsumed.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        lastNestConsumed = restoredConsumed;
      }
      catch (System.Exception)
      {
        // A corrupt/incompatible embedded result must never block opening the project itself.
        monitor.Reset();
        lastNestConsumed = null;
      }
    }

    private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
      // Closing with a nest result on screen: offer to save the project first. Yes runs the same
      // Save as the File menu (Save As dialog when the project has no file yet — declining THAT
      // dialog aborts the close so no work is silently lost); No closes without saving; Cancel stays.
      if (ViewModel.NestMonitorViewModel.SelectedItem != null && ViewModel.ActiveDocument != null)
      {
        var answer = MessageBox.Show(
          this,
          "You have a nest result. Save the project before closing?",
          "SheetNest",
          MessageBoxButton.YesNoCancel,
          MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
        {
          e.Cancel = true;
          return;
        }

        if (answer == MessageBoxResult.Yes)
        {
          // Always via the Save As dialog (pre-filled with the project's own name/folder for saved
          // projects — plain Enter overwrites, typing renames). Cancelling the dialog aborts the
          // close so no work is silently lost.
          if (!ViewModel.Save(ViewModel.ActiveDocument, true))
          {
            e.Cancel = true;
            return;
          }
        }
      }

      var project = (ViewModel.ActiveDocument as NestProjectViewModel)?.ProjectInfo;
      var rows = project == null
        ? new List<SessionSheet>()
        : project.SheetLoadInfos
            .Select(s => new SessionSheet { Width = s.Width, Height = s.Height, Quantity = s.Quantity })
            .ToList();

      new SessionState
      {
        SheetEdgeMargin = System.Math.Max(0, ViewModel.SvgNestConfigViewModel.SvgNestConfig.SheetSpacing),
        Sheets = rows,
      }.Save();
    }

    /// <summary>Add Sheet opens a menu: the standard stock sizes plus "Custom size…" (Radan-style).</summary>
    private void OnAddSheetMenu(object sender, RoutedEventArgs e)
    {
      var menu = new ContextMenu();
      int lastShortSide = -1;
      foreach (var preset in SheetPresets)
      {
        if (lastShortSide >= 0 && preset.Height != lastShortSide)
        {
          menu.Items.Add(new Separator()); // visual break between the 48-wide and 60-wide groups
        }

        lastShortSide = preset.Height;
        var size = preset;
        var item = new MenuItem { Header = size.Name };
        item.Click += (_, __) => this.AddSheetOfSize(size.Width, size.Height, 1);
        menu.Items.Add(item);
      }

      menu.Items.Add(new Separator());
      var custom = new MenuItem { Header = "Custom size…" };
      custom.Click += (_, __) =>
      {
        var dialog = new AddSheetWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
          this.AddSheetOfSize(dialog.SheetWidth, dialog.SheetHeight, dialog.SheetQuantity);
        }
      };
      menu.Items.Add(custom);

      menu.PlacementTarget = sender as UIElement;
      menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
      menu.IsOpen = true;
    }

    private void AddSheetOfSize(int width, int height, int quantity)
    {
      if (ViewModel.ActiveDocument is NestProjectViewModel doc && doc.AddSheetCommand.CanExecute(null))
      {
        doc.AddSheetCommand.Execute(null);
        var sheet = doc.ProjectInfo.SheetLoadInfos.LastOrDefault();
        if (sheet != null)
        {
          sheet.Width = width;
          sheet.Height = height;
          sheet.Quantity = quantity;
        }

        this.sheetsListView.Items.Refresh();
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
      // MouseDoubleClick fires even for double-clicks on the qty spinner's repeat arrows or the ✕
      // button inside a row (WPF class handler with handledEventsToo) — and those clicks don't move
      // the selection, so acting on SelectedItem could edit the WRONG part. Walk up from the actual
      // click target: ignore clicks on interactive controls, and edit the row that was hit.
      var d = e.OriginalSource as System.Windows.DependencyObject;
      while (d != null && !(d is ListViewItem))
      {
        if (d is System.Windows.Controls.Primitives.ButtonBase || d is Xceed.Wpf.Toolkit.IntegerUpDown)
        {
          return;
        }

        d = System.Windows.Media.VisualTreeHelper.GetParent(d);
      }

      if (d is ListViewItem item && item.DataContext is IDetailLoadInfo part)
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

      // One result at a time: its sheets are already deducted from the stock, so nesting again
      // would consume more sheets on top of it. Clear Result gives them back and unlocks NEST.
      if (!ViewModel.NestMonitorViewModel.TopNestResults.IsEmpty)
      {
        ViewModel.MessageService.DisplayMessageBox(
          "There is already a nest result. Press Clear Result first — it returns the sheets that nest used to the stock.",
          "Nest",
          DeepNestLib.MessageBoxIcon.Information);
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
      // EVERY sheet entry participates; the engine picks the size that wastes least material at each
      // step (mixed stock — the bulk lands on the densest-packing size, the tail on the smallest
      // sheet that takes it). List order only breaks ties.
      var sheetStock = project.SheetLoadInfos
        .Where(s => s.Width > 0 && s.Height > 0 && s.Quantity > 0)
        .Select(s => (s.Width, s.Height, s.Quantity))
        .ToList();
      if (sheetStock.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox("Add at least one sheet in the Sheets tab first.", "Nest", DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      int sheetQty = sheetStock.Sum(s => s.Quantity);   // total sheets the job may use (for the warning)
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
          var r = RasterNestService.Nest(parts, sheetStock, placementType, rotations, spacing, margin, 24.0, out string err);
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

          // Group by path — the same DXF may legitimately be listed twice (e.g. once common-line,
          // once spaced); a plain ToDictionary would throw and crash the app AFTER the nest ran.
          // Keep the SMALLEST effective spacing so manual editing never blocks a clearance the
          // nester itself allowed.
          this.dxfViewer.PartSpacings = parts
            .GroupBy(p => p.Path, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
              g => g.Key,
              g => g.Min(p => p.Spacing >= 0 ? p.Spacing : System.Math.Max(0, spacing)),
              System.StringComparer.OrdinalIgnoreCase);
        }

        // Show it in the results list + viewer + status bar so utilization / placed / sheets / fitness /
        // time all appear. The result must be IN TopNestResults first, otherwise the list's two-way
        // SelectedItem binding resets the selection (and the stats) back to null.
        var vm = ViewModel.NestMonitorViewModel;
        vm.TopNestResults.SetSingleResult(result);
        vm.SelectedItem = result;

        // Consume the stock this nest used, right in the Sheets tab — the count visibly drops
        // (35 in stock, nest used 31 → the tab now shows 4) and closing the app persists what
        // physically remains.
        var used = new Dictionary<(int W, int H), int>();
        foreach (var sp in result.UsedSheets)
        {
          var key = ((int)System.Math.Round(sp.Sheet.WidthCalculated), (int)System.Math.Round(sp.Sheet.HeightCalculated));
          used[key] = used.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        var consumed = new Dictionary<(int W, int H), int>();
        foreach (var row in project.SheetLoadInfos)
        {
          if (used.TryGetValue((row.Width, row.Height), out int n) && n > 0)
          {
            int take = System.Math.Min(row.Quantity, n);
            row.Quantity -= take;
            used[(row.Width, row.Height)] = n - take;
            if (take > 0)
            {
              consumed[(row.Width, row.Height)] = consumed.TryGetValue((row.Width, row.Height), out int c) ? c + take : take;
            }
          }
        }

        lastNestConsumed = consumed;
        this.sheetsListView.Items.Refresh();

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

    private void OnNestReportPdf(object sender, RoutedEventArgs e)
    {
      var selected = ViewModel.NestMonitorViewModel?.SelectedItem;
      if (selected == null || selected.UsedSheets == null || selected.UsedSheets.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox(
          "Run a nest and select a result first, then save the report.",
          "Nest report",
          DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      // Report exactly what the viewer shows: one entry per distinct layout with its cut count.
      var plan = this.dxfViewer.GetProductionPlan();
      if (plan == null || plan.Count == 0)
      {
        plan = selected.UsedSheets.Select(s => ((DeepNestLib.Placement.ISheetPlacement)s, 1, "Sheet")).ToList();
      }

      var dialog = new Microsoft.Win32.SaveFileDialog
      {
        Filter = "PDF files (*.pdf)|*.pdf",
        FileName = "nest report.pdf",
        Title = "Save nest report",
      };
      if (dialog.ShowDialog(this) != true)
      {
        return;
      }

      try
      {
        Reports.NestReportPdf.Write(dialog.FileName, plan, selected.UnplacedParts?.Count ?? 0);
      }
      catch (System.Exception ex)
      {
        ViewModel.MessageService.DisplayMessageBox(
          $"Could not write the report: {ex.Message}",
          "Nest report",
          DeepNestLib.MessageBoxIcon.Error);
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
