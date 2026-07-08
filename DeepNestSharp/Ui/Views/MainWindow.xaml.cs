namespace DeepNestSharp.Ui.Views
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Threading.Tasks;
  using System.Windows;
  using System.Windows.Controls;
  using System.Windows.Input;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.Domain;
  using DeepNestSharp.Domain.Models;
  using DeepNestSharp.Domain.ViewModels;
  using DeepNestSharp.RasterNest;

  public partial class MainWindow : Window
  {
    // Sheets the LAST nest deducted from the stock, by size — Clear Result gives them back.
    private Dictionary<(int W, int H), int>? lastNestConsumed;

    // Set once the window is closed, so an async op that finishes afterwards (e.g. a slow 3D unfold)
    // doesn't touch a dead window (would throw "set Owner on a closed Window").
    private bool isClosed;
    private string updateUrl;
    private System.Windows.Threading.DispatcherTimer autosaveTimer;
    private bool autosaveEnabled = true;
    private int autosaveMinutes = 5;

    public MainWindow(IMainViewModel viewModel)
    {
      InitializeComponent();
      this.DataContext = viewModel;
      viewModel.AboutDialogService = new AboutDialogService(() => new AboutDialog());
      this.Loaded += MainWindow_Loaded;
      this.Closing += MainWindow_Closing;
      this.Closed += (s, e) => this.isClosed = true;
      viewModel.ActiveDocumentChanged += MainWindow_ActiveDocumentChanged;

      // Autosave (SigmaNEST "Auto Save WS"): snapshot the dirty project periodically; a crash then
      // offers recovery on the next start (clean saves/closes clear the snapshot). Enable/interval
      // live in Settings > Application Settings and the session restores them on load.
      this.autosaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMinutes(5) };
      this.autosaveTimer.Tick += this.OnAutosaveTick;
      this.autosaveTimer.Start();
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

      // Fire-and-forget update check: lights up the status-bar link if a newer release exists.
      // Silent on every failure (offline shop PCs) and sends nothing — a single anonymous GET.
      _ = this.CheckForUpdatesOnStartupAsync();

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

      // Restore the previous session's sheet edge margin. Sheets are NOT restored: the app starts
      // with an empty Sheets tab because each launch is usually a different job — sheet stock
      // belongs to (and travels with) each saved .dnest project.
      var session = SessionState.Load();
      if (session != null)
      {
        if (session.SheetEdgeMargin >= 0)
        {
          cfg.SheetSpacing = session.SheetEdgeMargin;
        }

        // Restore the 3D-unfold (FreeCAD) settings.
        if (session.UnfoldKFactor >= 0)
        {
          DeepNestLib.IO.StepUnfoldService.KFactor = session.UnfoldKFactor;
        }

        if (!string.IsNullOrWhiteSpace(session.UnfoldKFactorStandard))
        {
          DeepNestLib.IO.StepUnfoldService.KFactorStandard = session.UnfoldKFactorStandard;
        }

        if (!string.IsNullOrWhiteSpace(session.FreeCadCmdPath))
        {
          DeepNestLib.IO.StepUnfoldService.FreeCadCmdPathOverride = session.FreeCadCmdPath;
        }

        if (session.UnfoldUnitInch.HasValue)
        {
          DeepNestLib.IO.StepUnfoldService.UnfoldUnitInch = session.UnfoldUnitInch.Value;
        }

        // Autosave settings (Settings > Application Settings).
        this.ApplyAutosaveSettings(
          session.AutosaveEnabled ?? true,
          session.AutosaveMinutes >= 1 && session.AutosaveMinutes <= 60 ? session.AutosaveMinutes : 5);
      }

      // The config normalization above — and the initial WPF binding pass, which runs AFTER Loaded
      // (Xceed up-downs push their first values) — ping the project's IsDirty. A fresh EMPTY project
      // has no user work, and the title asterisk must mean "YOUR changes are unsaved", so reset the
      // flag once the binding churn settles. The content guard keeps real work (recovered projects,
      // a part added in the first second) dirty.
      this.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new System.Action(() =>
      {
        if (!this.isClosed
            && ViewModel.ActiveDocument is NestProjectViewModel fresh
            && string.IsNullOrEmpty(fresh.FilePath)
            && fresh.ProjectInfo.DetailLoadInfos.Count == 0
            && fresh.ProjectInfo.SheetLoadInfos.Count == 0)
        {
          fresh.IsDirty = false;
        }
      }));

      // Crash recovery LAST (after the config/session churn, so the recovered doc's deliberate
      // dirty flag survives): an autosave snapshot only exists after an unclean exit.
      this.OfferAutosaveRecovery();
    }

    /// <summary>Offers to restore the autosaved project left behind by a crashed session.</summary>
    private void OfferAutosaveRecovery()
    {
      try
      {
        if (!Autosave.TryGetPending(out string originalPath, out string savedAt))
        {
          return;
        }

        string name = string.IsNullOrWhiteSpace(originalPath) ? "an unsaved project" : System.IO.Path.GetFileName(originalPath);
        string when = string.IsNullOrWhiteSpace(savedAt) ? string.Empty : $" (autosaved {savedAt})";
        var answer = ViewModel.MessageService.DisplayOkCancel(
          $"SheetNest closed unexpectedly with unsaved changes in {name}{when}.\n\nRecover the autosaved project?",
          "Recovery",
          DeepNestLib.MessageBoxIcon.Information);
        if (answer == DeepNestLib.MessageBoxResult.OK)
        {
          ViewModel.OnLoadNestProject(Autosave.RecoveryPath);
          if (ViewModel.ActiveDocument is NestProjectViewModel recovered)
          {
            // Point the document back at the REAL file (empty = never saved -> Save As) and keep it
            // dirty: recovered work is unsaved work until the user saves it.
            recovered.FilePath = originalPath ?? string.Empty;
            recovered.IsDirty = true;
          }
        }

        Autosave.Clear();
      }
      catch (Exception ex)
      {
        // Recovery is best-effort and must never block startup — but a failure is worth a local log.
        CrashReporter.Save(ex, "autosave-recovery");
        Autosave.Clear(); // a snapshot that cannot be recovered must not re-prompt forever
      }
    }

    /// <summary>Startup check: light up the status-bar link if a newer release exists; silent otherwise.</summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
      try
      {
        var result = await UpdateChecker.CheckAsync();
        if (this.isClosed || result == null || !UpdateChecker.IsNewer(result.Value.Latest, UpdateChecker.CurrentVersion))
        {
          return;
        }

        this.ShowUpdateNotice(result.Value.Latest, result.Value.Url);
      }
      catch
      {
        // never disturb startup
      }
    }

    private void ShowUpdateNotice(Version latest, string url)
    {
      this.updateUrl = url;
      this.updateNotice.Text = $"Update available: {latest.ToString(3)}";
      this.updateNotice.Visibility = Visibility.Visible;
    }

    /// <summary>Help menu: check GitHub on demand and report the outcome.</summary>
    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
      try
      {
        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        var result = await UpdateChecker.CheckAsync();
        Mouse.OverrideCursor = null;
        if (this.isClosed)
        {
          return;
        }

        var current = UpdateChecker.CurrentVersion;
        if (result == null)
        {
          ViewModel.MessageService.DisplayMessageBox(
            "Could not check for updates. Check your internet connection.",
            "Check for Updates",
            DeepNestLib.MessageBoxIcon.Information);
          return;
        }

        if (!UpdateChecker.IsNewer(result.Value.Latest, current))
        {
          ViewModel.MessageService.DisplayMessageBox(
            $"You are up to date (SheetNest {current.ToString(3)}).",
            "Check for Updates",
            DeepNestLib.MessageBoxIcon.Information);
          return;
        }

        this.ShowUpdateNotice(result.Value.Latest, result.Value.Url);
        var answer = ViewModel.MessageService.DisplayOkCancel(
          $"SheetNest {result.Value.Latest.ToString(3)} is available (you have {current.ToString(3)}).\n\nOpen the download page?",
          "Check for Updates",
          DeepNestLib.MessageBoxIcon.Information);
        if (answer == DeepNestLib.MessageBoxResult.OK)
        {
          UpdateChecker.OpenDownloadPage(result.Value.Url);
        }
      }
      catch (Exception ex)
      {
        Mouse.OverrideCursor = null;
        ViewModel.MessageService.DisplayMessageBox(ex.Message, "Check for Updates", DeepNestLib.MessageBoxIcon.Stop);
      }
    }

    private void OnUpdateNoticeClick(object sender, MouseButtonEventArgs e)
    {
      if (!string.IsNullOrEmpty(this.updateUrl))
      {
        UpdateChecker.OpenDownloadPage(this.updateUrl);
      }
    }

    private void Document_PropertyChangedForMru(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
      if (e.PropertyName == nameof(NestProjectViewModel.FilePath)
          && sender is NestProjectViewModel doc
          && !string.IsNullOrWhiteSpace(doc.FilePath))
      {
        SessionState.PushRecentProject(doc.FilePath);
      }

      // A save (IsDirty -> false) means there is nothing left to recover.
      if (e.PropertyName == nameof(NestProjectViewModel.IsDirty)
          && sender is NestProjectViewModel saved
          && !saved.IsDirty)
      {
        Autosave.Clear();
      }
    }

    /// <summary>Every 5 minutes: snapshot the dirty project (with its on-screen nest result) for recovery.</summary>
    private void OnAutosaveTick(object sender, System.EventArgs e)
    {
      try
      {
        if (ViewModel?.ActiveDocument is NestProjectViewModel doc && doc.IsDirty)
        {
          // Same embed Save performs, so a recovery brings the on-screen nest result back too.
          doc.ProjectInfo.LastNestResultJson =
            (ViewModel.NestMonitorViewModel.SelectedItem as NestResult)?.ToJson(false) ?? string.Empty;
          Autosave.Write(doc.TextContent, doc.FilePath);
        }
      }
      catch
      {
        // autosave must never disturb the user
      }
    }

    /// <summary>Rebuilds File > Recent Projects from the session on every open (prunes dead paths).</summary>
    private void OnRecentProjectsOpened(object sender, RoutedEventArgs e)
    {
      this.recentProjectsMenu.Items.Clear();
      var recents = (SessionState.Load()?.RecentProjects ?? new List<string>())
        .Where(System.IO.File.Exists)
        .ToList();
      if (recents.Count == 0)
      {
        this.recentProjectsMenu.Items.Add(new MenuItem { Header = "(empty)", IsEnabled = false });
        return;
      }

      foreach (var path in recents)
      {
        var item = new MenuItem
        {
          Header = System.IO.Path.GetFileName(path),
          ToolTip = path,
        };
        string captured = path;
        item.Click += (_, __) => ViewModel.LoadNestProjectInteractive(captured);
        this.recentProjectsMenu.Items.Add(item);
      }
    }

    /// <summary>Applies the autosave settings to the running timer and remembers them for the session save.</summary>
    private void ApplyAutosaveSettings(bool enabled, int minutes)
    {
      this.autosaveEnabled = enabled;
      this.autosaveMinutes = minutes;
      if (this.autosaveTimer != null)
      {
        this.autosaveTimer.Interval = System.TimeSpan.FromMinutes(minutes);
        this.autosaveTimer.IsEnabled = enabled;
      }
    }

    private void OnAppSettings(object sender, RoutedEventArgs e)
    {
      var dialog = new AppSettingsWindow(this.autosaveEnabled, this.autosaveMinutes) { Owner = this };
      if (dialog.ShowDialog() != true)
      {
        return;
      }

      this.ApplyAutosaveSettings(dialog.AutosaveEnabled, dialog.AutosaveMinutes);

      // Persist immediately (load-modify-save keeps the MRU and the other session fields intact).
      var session = SessionState.Load() ?? new SessionState();
      session.AutosaveEnabled = this.autosaveEnabled;
      session.AutosaveMinutes = this.autosaveMinutes;
      session.Save();
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

      // The project no longer carries this result (a save from here on writes it without one, and
      // the session's close-time stock logic must treat any FRESH nest after this as unsaved).
      if (ViewModel.ActiveDocument is NestProjectViewModel clearedDoc)
      {
        clearedDoc.ProjectInfo.LastNestResultJson = string.Empty;
      }

      this.UpdateNestedInfo(null);

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

      // Recent Projects: record opened files now, and saved-for-the-first-time files when their
      // FilePath appears (Save As raises PropertyChanged). Subscription is made idempotent.
      if (!string.IsNullOrWhiteSpace(doc.FilePath))
      {
        SessionState.PushRecentProject(doc.FilePath);
      }

      doc.PropertyChanged -= this.Document_PropertyChangedForMru;
      doc.PropertyChanged += this.Document_PropertyChangedForMru;

      var monitor = ViewModel.NestMonitorViewModel;
      string json = doc.ProjectInfo.LastNestResultJson;
      if (string.IsNullOrWhiteSpace(json))
      {
        monitor.Reset();
        lastNestConsumed = null;
        this.UpdateNestedInfo(null);
        return;
      }

      try
      {
        var result = NestResult.FromJson(json);
        if (result == null || result.UsedSheets.Count == 0)
        {
          monitor.Reset();
          lastNestConsumed = null;
          this.UpdateNestedInfo(null);
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
        this.UpdateNestedInfo(result);
      }
      catch (System.Exception)
      {
        // A corrupt/incompatible embedded result must never block opening the project itself.
        monitor.Reset();
        lastNestConsumed = null;
        this.UpdateNestedInfo(null);
      }
    }

    /// <summary>
    /// Feeds the per-row "Available X/Y" badges in the Parts and Sheets panels: sets each row's
    /// NestedCount (sheets consumed / parts placed by the on-screen result; 0 = no result) and the
    /// wrappers render availability from it. The badges are ALWAYS visible — with no result every
    /// row simply reads N/N.
    /// </summary>
    private void UpdateNestedInfo(INestResult result)
    {
      if (!(ViewModel.ActiveDocument is NestProjectViewModel doc))
      {
        return;
      }

      var sheetRows = doc.ProjectInfo.SheetLoadInfos.OfType<ObservableSheetLoadInfo>().ToList();
      var partRows = doc.ProjectInfo.DetailLoadInfos.OfType<ObservableDetailLoadInfo>().ToList();

      if (result == null)
      {
        sheetRows.ForEach(s => s.NestedCount = 0);
        partRows.ForEach(p => p.NestedCount = 0);
        return;
      }

      // Sheets: sheets of this size the nest consumed (recorded at deduction time); the wrapper
      // renders availability as Quantity / (Quantity + NestedCount).
      foreach (var s in sheetRows)
      {
        int used = 0;
        if (lastNestConsumed != null)
        {
          lastNestConsumed.TryGetValue((s.Width, s.Height), out used);
        }

        s.NestedCount = used;
      }

      // Parts: placements carry the name the parser stored (full path for DXF, file name for SVG).
      // Hand each name's count out to matching rows in listed order, capped at each row's own
      // demand — the same matching the mixed-stock consumption uses, so a DXF listed twice (e.g.
      // once common-line, once spaced) splits sensibly instead of double-counting.
      static string SafeFileName(string path)
      {
        try
        {
          return System.IO.Path.GetFileName(path);
        }
        catch (System.ArgumentException)
        {
          return path;
        }
      }

      var assigned = new Dictionary<ObservableDetailLoadInfo, int>();
      partRows.ForEach(p => assigned[p] = 0);
      var placedByName = result.UsedSheets
        .SelectMany(sp => sp.PartPlacements)
        .GroupBy(p => p.Part.Name ?? string.Empty, System.StringComparer.OrdinalIgnoreCase);
      foreach (var group in placedByName)
      {
        int left = group.Count();
        foreach (int pass in new[] { 0, 1 })
        {
          foreach (var row in partRows)
          {
            if (left == 0)
            {
              break;
            }

            bool exact = string.Equals(row.Path, group.Key, System.StringComparison.OrdinalIgnoreCase);
            bool match = pass == 0 ? exact : !exact && string.Equals(SafeFileName(row.Path), group.Key, System.StringComparison.OrdinalIgnoreCase);
            if (!match)
            {
              continue;
            }

            int take = System.Math.Min(System.Math.Max(0, row.Quantity + row.Extra + row.MirrorQuantity - assigned[row]), left);
            assigned[row] += take;
            left -= take;
          }

          if (left == 0)
          {
            break;
          }
        }
      }

      partRows.ForEach(p => p.NestedCount = assigned[p]);
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

      // A close that reaches this point is CLEAN (saved, discarded by choice, or nothing dirty) —
      // the autosave snapshot must not trigger a bogus recovery prompt on the next start.
      Autosave.Clear();

      // Only the sheet edge margin persists across sessions. Sheet stock is deliberately NOT part of
      // the session anymore — the app starts empty and each saved .dnest project carries its own
      // stock (with its embedded nest result, whose Clear Result returns the sheets it consumed).
      new SessionState
      {
        SheetEdgeMargin = System.Math.Max(0, ViewModel.SvgNestConfigViewModel.SvgNestConfig.SheetSpacing),
        Sheets = new List<SessionSheet>(),
        UnfoldKFactor = DeepNestLib.IO.StepUnfoldService.KFactor,
        UnfoldKFactorStandard = DeepNestLib.IO.StepUnfoldService.KFactorStandard,
        FreeCadCmdPath = DeepNestLib.IO.StepUnfoldService.FreeCadCmdPathOverride,
        UnfoldUnitInch = DeepNestLib.IO.StepUnfoldService.UnfoldUnitInch,
        RecentProjects = SessionState.Load()?.RecentProjects ?? new List<string>(), // preserve the MRU
        AutosaveEnabled = this.autosaveEnabled,
        AutosaveMinutes = this.autosaveMinutes,
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

    /// <summary>Edits a stock row through the sheet dialog — the panel shows no quantity at all.</summary>
    private void OnEditSheet(object sender, RoutedEventArgs e)
    {
      if ((sender as Button)?.Tag is ISheetLoadInfo row)
      {
        OpenEditSheet(row);
      }
    }

    /// <summary>Double-click on a stock row opens the same Edit sheet dialog as the ✎ button.</summary>
    private void OnSheetsListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      // Same guard as the parts list: MouseDoubleClick fires for double-clicks on the W/H spinners'
      // repeat arrows or the row buttons too, and those don't move the selection — walk up from the
      // real click target and edit the row that was actually hit.
      var d = e.OriginalSource as System.Windows.DependencyObject;
      while (d != null && !(d is ListViewItem))
      {
        if (d is System.Windows.Controls.Primitives.ButtonBase || d is Xceed.Wpf.Toolkit.IntegerUpDown)
        {
          return;
        }

        d = System.Windows.Media.VisualTreeHelper.GetParent(d);
      }

      if (d is ListViewItem item && item.DataContext is ISheetLoadInfo row)
      {
        OpenEditSheet(row);
      }
    }

    private void OpenEditSheet(ISheetLoadInfo row)
    {
      var dialog = new AddSheetWindow { Owner = this };
      dialog.PrefillForEdit(row.Width, row.Height, row.Quantity);
      if (dialog.ShowDialog() == true)
      {
        row.Width = dialog.SheetWidth;
        row.Height = dialog.SheetHeight;
        row.Quantity = dialog.SheetQuantity;
        this.sheetsListView.Items.Refresh();
      }
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
      => await AddPartsToActiveProject(d => d.AddPartCommand, autoEditSingle: true);

    /// <summary>
    /// Import 3D (STEP/IGES): pick file(s) → probe thickness → show the K-factor dialog → unfold every
    /// sheet-metal solid → add the flats. Off-UI unfolds with a busy cursor; guarded against a closed window.
    /// </summary>
    private async void OnImport3DClicked(object sender, RoutedEventArgs e)
    {
      var doc = ViewModel.ActiveDocument as NestProjectViewModel;
      if (doc == null)
      {
        return;
      }

      var picker = new Microsoft.Win32.OpenFileDialog
      {
        Filter = DeepNestLib.IO.StepUnfoldService.FileDialogFilter3D,
        Multiselect = true,
        Title = "Import 3D (STEP / IGES)",
      };
      if (picker.ShowDialog(this) != true)
      {
        return;
      }

      foreach (var file in picker.FileNames)
      {
        try
        {
          // 1. Probe: detect thickness + solid count (off the UI thread).
          System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.AppStarting;
          (int SolidCount, double[] ThicknessMm) probe;
          try
          {
            probe = await System.Threading.Tasks.Task.Run(() => DeepNestLib.IO.StepUnfoldService.ProbeThickness(file));
          }
          finally
          {
            System.Windows.Input.Mouse.OverrideCursor = null;
          }

          if (this.isClosed || !this.IsLoaded)
          {
            return;
          }

          // 2. Ask for K-factor / standard / unit (pre-filled with the detected thickness).
          var opts = new Import3DWindow(System.IO.Path.GetFileName(file), probe.SolidCount, probe.ThicknessMm) { Owner = this };
          if (opts.ShowDialog() != true)
          {
            continue;
          }

          // 3. Unfold with the chosen options (off the UI thread) and add the flats.
          System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.AppStarting;
          (System.Collections.Generic.IReadOnlyList<string> Paths, int Skipped) result;
          try
          {
            result = await System.Threading.Tasks.Task.Run(() => DeepNestLib.IO.StepUnfoldService.GetUnfoldedParts(file));
          }
          finally
          {
            System.Windows.Input.Mouse.OverrideCursor = null;
          }

          if (this.isClosed || !this.IsLoaded)
          {
            return;
          }

          // Map each produced flat to its metadata. Thickness: if no solid was skipped, index-align it to
          // the probe result (exact for the common single-solid case); otherwise use the sheet's single
          // thickness if uniform, else 0 (unknown — display only). The dialog wrote the chosen K/std/unit
          // to the StepUnfoldService statics on OK.
          double uniformThickness = 0;
          bool thicknessVaries = false;
          foreach (var t in probe.ThicknessMm)
          {
            if (t > 0)
            {
              if (uniformThickness == 0)
              {
                uniformThickness = t;
              }
              else if (System.Math.Abs(uniformThickness - t) > 1e-9)
              {
                thicknessVaries = true;
              }
            }
          }

          double fallbackThickness = thicknessVaries ? 0 : uniformThickness;
          var infos = new System.Collections.Generic.List<NestProjectViewModel.UnfoldedPartInfo>();
          for (int i = 0; i < result.Paths.Count; i++)
          {
            double thick = (result.Paths.Count == probe.SolidCount && i < probe.ThicknessMm.Length)
              ? probe.ThicknessMm[i]
              : fallbackThickness;
            infos.Add(new NestProjectViewModel.UnfoldedPartInfo(
              result.Paths[i], file, i,
              DeepNestLib.IO.StepUnfoldService.KFactor,
              DeepNestLib.IO.StepUnfoldService.KFactorStandard,
              DeepNestLib.IO.StepUnfoldService.UnfoldUnitInch,
              thick));
          }

          doc.AddUnfoldedParts(infos);

          if (result.Skipped > 0)
          {
            ViewModel.MessageService.DisplayMessageBox(
              $"Imported {result.Paths.Count} part(s). {result.Skipped} solid(s) were skipped (not recognized as sheet metal).",
              "Import 3D",
              DeepNestLib.MessageBoxIcon.Information);
          }
        }
        catch (DeepNestLib.IO.StepUnfoldException ex)
        {
          System.Windows.Input.Mouse.OverrideCursor = null;
          ViewModel.MessageService.DisplayMessageBox(ex.Message, "Import 3D", DeepNestLib.MessageBoxIcon.Stop);
        }
        catch (System.Exception ex)
        {
          System.Windows.Input.Mouse.OverrideCursor = null;
          CrashReporter.Show(ex, "import-3d", this);
        }
      }
    }

    private async System.Threading.Tasks.Task AddPartsToActiveProject(
      System.Func<NestProjectViewModel, Microsoft.Toolkit.Mvvm.Input.IAsyncRelayCommand> commandSelector,
      bool autoEditSingle)
    {
      var doc = ViewModel.ActiveDocument as NestProjectViewModel;
      if (doc == null)
      {
        return;
      }

      int before = doc.ProjectInfo.DetailLoadInfos.Count;
      await commandSelector(doc).ExecuteAsync(null);

      // The user may have closed the window while a slow 3D unfold ran — don't touch a dead window.
      if (this.isClosed || !this.IsLoaded)
      {
        return;
      }

      // Radan-style insert flow: adding a single part opens Edit Part right away. Skipped for 3D
      // imports (autoEditSingle=false) — their unfold is slow, so the part loads async in the grid.
      var infos = doc.ProjectInfo.DetailLoadInfos;
      if (autoEditSingle && infos.Count == before + 1 && infos[infos.Count - 1] is IDetailLoadInfo added)
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

    private async void OpenEditPart(IDetailLoadInfo part)
    {
      if (this.isClosed || !this.IsLoaded)
      {
        return;
      }

      bool is3D = !string.IsNullOrEmpty(part.SourceStepPath);
      double oldK = part.KFactor;
      string oldStd = part.KFactorStandard;

      var cfg = ViewModel.SvgNestConfigViewModel.SvgNestConfig;
      var dialog = new EditPartWindow(part, cfg.Rotations, System.Math.Max(0, cfg.Spacing))
      {
        Owner = this,
      };
      if (dialog.ShowDialog() != true)
      {
        return;
      }

      this.partsListView.Items.Refresh(); // plain (non-observable) fields may have changed

      bool kChanged = is3D
        && (System.Math.Abs(part.KFactor - oldK) > 1e-9
            || !string.Equals(part.KFactorStandard, oldStd, System.StringComparison.OrdinalIgnoreCase));
      if (kChanged)
      {
        await ReunfoldPart(part, oldK, oldStd);
      }
    }

    /// <summary>Re-runs the FreeCAD unfold for a 3D part with its new K-factor and swaps in the new flat,
    /// refreshing the thumbnail / size / area. Off the UI thread with a busy cursor. Reverts K on failure.</summary>
    private async System.Threading.Tasks.Task ReunfoldPart(IDetailLoadInfo part, double oldK, string oldStd)
    {
      var obs = part as DeepNestSharp.Domain.Models.ObservableDetailLoadInfo;
      string oldPath = part.Path;
      System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.AppStarting;
      try
      {
        var result = await System.Threading.Tasks.Task.Run(() => DeepNestLib.IO.StepUnfoldService.GetUnfoldedParts(
          part.SourceStepPath, part.KFactor, part.KFactorStandard, part.UnfoldUnitInch));

        if (this.isClosed || !this.IsLoaded || result.Paths.Count == 0)
        {
          return;
        }

        int idx = (part.UnfoldIndex >= 0 && part.UnfoldIndex < result.Paths.Count) ? part.UnfoldIndex : 0;
        string newFlat = result.Paths[idx];

        part.Path = newFlat;
        DeepNestSharp.Ui.Converters.PartPreviewConverter.Invalidate(oldPath);
        DeepNestSharp.Ui.Converters.PartPreviewConverter.Invalidate(newFlat);
        obs?.InvalidateGeometry();
        this.partsListView.Items.Refresh();
      }
      catch (DeepNestLib.IO.StepUnfoldException ex)
      {
        part.KFactor = oldK; // revert so Edit Part shows the value that actually produced the current flat
        part.KFactorStandard = oldStd;
        ViewModel.MessageService.DisplayMessageBox(ex.Message, "Re-unfold", DeepNestLib.MessageBoxIcon.Stop);
      }
      catch (System.Exception ex)
      {
        CrashReporter.Show(ex, "reunfold-3d", this);
      }
      finally
      {
        System.Windows.Input.Mouse.OverrideCursor = null;
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
        .SelectMany(o =>
        {
          var populations = new List<RasterPartInfo>
          {
            new RasterPartInfo
            {
              Path = o.Path,
              Quantity = o.Quantity + o.Extra,               // required + spares
              Rotations = o.Rotations,                       // -1 = engine default
              Priority = o.Priority,                         // higher nests first
              Spacing = o.CommonLine ? 0.0 : o.Spacing,      // common-line = touch; -1 = job default
            },
          };

          if (o.MirrorQuantity > 0)
          {
            // Second population of the SAME part, nested X-flipped (left/right-hand pairs).
            populations.Add(new RasterPartInfo
            {
              Path = o.Path,
              Quantity = o.MirrorQuantity,
              Rotations = o.Rotations,
              Priority = o.Priority,
              Spacing = o.CommonLine ? 0.0 : o.Spacing,
              Mirrored = true,
            });
          }

          return populations;
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
        // Hidden end-to-end test hook for the crash reporter (set SHEETNEST_CRASH_TEST=1).
        if (System.Environment.GetEnvironmentVariable("SHEETNEST_CRASH_TEST") == "1")
        {
          throw new System.InvalidOperationException("Synthetic crash for testing the problem-report dialog.");
        }

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
        this.UpdateNestedInfo(result);

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
      catch (System.Exception ex)
      {
        // A third-party DXF the importer chokes on (or an engine bug) must never kill the app —
        // this async void handler would otherwise crash the process (real-world user report).
        // Log it, offer the consent-based GitHub report, and stay alive.
        CrashReporter.Show(ex, "nest", this);
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
