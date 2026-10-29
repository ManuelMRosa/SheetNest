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
  using SheetNest.Localization;

  public partial class MainWindow : Window
  {
    // Sheets the LAST nest deducted from the stock, by size — Clear Result gives them back.
    private Dictionary<(int W, int H), int>? lastNestConsumed;

    // Set once the window is closed, so an async op that finishes afterwards (e.g. a slow 3D unfold)
    // doesn't touch a dead window (would throw "set Owner on a closed Window").
    private bool isClosed;
    private string updateUrl;
    private System.Windows.Threading.DispatcherTimer autosaveTimer;
    private System.Threading.CancellationTokenSource nestCts;
    private System.Diagnostics.Stopwatch nestStopwatch;   // elapsed time while a nest runs (drives the live clock)
    private System.Windows.Threading.DispatcherTimer nestTimer; // ticks the elapsed clock ~2×/s during a nest
    private bool unitsMm; // drawing units are millimeters (SessionState.UnitsMm); inches by default
    private List<(int W, int H)> userSheetPresets = new List<(int W, int H)>(); // "My sheets" (SessionState.SheetPresets)
    private bool autosaveEnabled = true;
    private int autosaveMinutes = 5;
    // Per-user defaults for the NEXT job. The process-wide config is temporarily overwritten whenever
    // a document becomes active, so it cannot also be the source of truth for session preferences.
    private double sessionCommonLineGapInches = CommonLineDefaults.DefaultGapInches;
    private int sessionCommonLineMaxClusterParts = CommonLineDefaults.DefaultMaxClusterParts;

    private DeepNestSharp.RasterNest.NestEffort nestEffort = DeepNestSharp.RasterNest.NestEffortDefaults.Default; // how hard the nester searches (SessionState)
    private bool preferRectOffcut; // cut the last sheet's leftover off as a rectangular offcut (SessionState)
    private int offcutDirection; // 0 = end, 1 = side, 2 = both (SessionState.OffcutDirection)
    private double offcutSpacing = -1; // gap parts→offcut cut line; -1 = default to part spacing (SessionState)
    private double offcutMinWidth = -1; // narrowest strip worth cutting; <=0 = automatic 5% rule (SessionState)
    private double offcutMinX = -1; // the remnant size window, the remnant cutoff (SessionState)
    private double offcutMinY = -1;
    private double offcutMaxX = -1;
    private double offcutMaxY = -1;
    private double offcutEdgeOverlap = -1; // how far the cut runs past the sheet edge; <=0 = flush

    public MainWindow(IMainViewModel viewModel)
    {
      InitializeComponent();
      this.DataContext = viewModel;

      // The Nests panel follows the viewer: its cards are redrawn whenever the layouts change, and its
      // selection moves when the viewer changes layout on its own (undoing an edit made on another one).
      this.dxfViewer.LayoutsChanged += (s, e) => this.RefreshNestCards();
      System.ComponentModel.DependencyPropertyDescriptor
        .FromProperty(DeepNestSharp.Ui.UserControls.DxfViewer.SheetIndexProperty, typeof(DeepNestSharp.Ui.UserControls.DxfViewer))
        .AddValueChanged(this.dxfViewer, (s, e) => this.SelectShownNest());
      viewModel.AboutDialogService = new AboutDialogService(() => new AboutDialog());
      this.Loaded += MainWindow_Loaded;
      this.Closing += MainWindow_Closing;
      this.Closed += (s, e) => this.isClosed = true;
      viewModel.ActiveDocumentChanged += MainWindow_ActiveDocumentChanged;

      // Autosave (auto-save workspace): snapshot the dirty project periodically; a crash then
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

    // Standard metric mill sizes (mm), width = the LONG side; grouped by the short side like above.
    private static readonly SheetPreset[] SheetPresetsMm =
    {
      new SheetPreset("2000 × 1000 mm", 2000, 1000),
      new SheetPreset("3000 × 1000 mm", 3000, 1000),
      new SheetPreset("2500 × 1250 mm", 2500, 1250),
      new SheetPreset("3000 × 1250 mm", 3000, 1250),
      new SheetPreset("3000 × 1500 mm", 3000, 1500),
      new SheetPreset("4000 × 1500 mm", 4000, 1500),
      new SheetPreset("6000 × 1500 mm", 6000, 1500),
      new SheetPreset("4000 × 2000 mm", 4000, 2000),
      new SheetPreset("6000 × 2000 mm", 6000, 2000),
    };

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
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
      this.BuildLanguageMenu(LanguagePreference.Read());
      (this.sessionCommonLineGapInches, this.sessionCommonLineMaxClusterParts) =
        CommonLinePreferencesFrom(session);
      cfg.CommonLineGapInches = this.sessionCommonLineGapInches;
      cfg.CommonLineMaxClusterParts = this.sessionCommonLineMaxClusterParts;
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

        if (session.UnitsMm.HasValue)
        {
          this.unitsMm = session.UnitsMm.Value;
        }

        if (session.SheetPresets != null)
        {
          this.userSheetPresets = session.SheetPresets
            .Where(sp => sp.Width > 0 && sp.Height > 0)
            .Select(sp => (sp.Width, sp.Height))
            .ToList();
        }

        if (session.UnfoldUnitInch.HasValue)
        {
          DeepNestLib.IO.StepUnfoldService.UnfoldUnitInch = session.UnfoldUnitInch.Value;
        }

        this.ApplyUnits();

        // Autosave settings (Settings > Advanced Settings).
        this.ApplyAutosaveSettings(
          session.AutosaveEnabled ?? true,
          session.AutosaveMinutes >= 1 && session.AutosaveMinutes <= 60 ? session.AutosaveMinutes : 5);

        // A Fast stored by a build that overwrote the "never chosen" marker on close says nothing about
        // what the operator wanted, so it is forgotten once and the current default applies. Saving here
        // is what makes it once: the write stamps the settings version, and a Fast picked deliberately
        // after this point is kept.
        if (DeepNestSharp.RasterNest.NestEffortDefaults.NeedsEffortReset(session.NestEffort, session.SettingsVersion))
        {
          session.NestEffort = DeepNestSharp.RasterNest.NestEffortDefaults.Unset;
          session.Save();
        }

        this.nestEffort = DeepNestSharp.RasterNest.NestEffortDefaults.FromPersisted(session.NestEffort);
        this.RefreshNestEffortMenu();

        // Global default rotations (plain in-memory config property — resets to 4 without this).
        if (session.DefaultRotations > 0)
        {
          cfg.Rotations = session.DefaultRotations;
        }

        if (session.PreferRectangularOffcut.HasValue)
        {
          this.preferRectOffcut = session.PreferRectangularOffcut.Value;
        }

        if (session.OffcutDirection >= 0 && session.OffcutDirection <= 3)
        {
          this.offcutDirection = session.OffcutDirection;
        }

        if (session.OffcutSpacing >= 0)
        {
          this.offcutSpacing = session.OffcutSpacing;
        }

        if (session.OffcutMinWidth >= 0)
        {
          this.offcutMinWidth = session.OffcutMinWidth;
        }

        // The single minimum came first and applied to both axes. Seed the pair from it so a session that
        // already had one does not silently lose it the first time the new dialog is opened.
        this.offcutMinX = session.OffcutMinX >= 0 ? session.OffcutMinX : this.offcutMinWidth;
        this.offcutMinY = session.OffcutMinY >= 0 ? session.OffcutMinY : this.offcutMinWidth;
        this.offcutMaxX = session.OffcutMaxX;
        this.offcutMaxY = session.OffcutMaxY;
        this.offcutEdgeOverlap = session.OffcutEdgeOverlap;
      }

      // Create the initial document only AFTER restoring the session. ProjectInfo snapshots the
      // geometry-affecting common-line gap at construction time; creating it earlier made a session
      // value such as 0.015" drive the first nest while Save/Autosave still wrote the old 0.010".
      if (ViewModel.ActiveDocument == null && ViewModel.CreateNestProjectCommand.CanExecute(null))
      {
        ViewModel.CreateNestProjectCommand.Execute(null);
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

        string name = string.IsNullOrWhiteSpace(originalPath) ? Strings.Main_RecoverUnsavedName : System.IO.Path.GetFileName(originalPath);
        string when = string.IsNullOrWhiteSpace(savedAt) ? string.Empty : string.Format(Strings.Main_RecoverWhenFormat, savedAt);
        var answer = ViewModel.MessageService.DisplayOkCancel(
          string.Format(Strings.Msg_RecoverAutosave, name, when),
          Strings.Msg_RecoveryTitle,
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
      this.updateNotice.Text = string.Format(Strings.Main_UpdateAvailableFormat, latest.ToString(3));
      this.updateNotice.Visibility = Visibility.Visible;
    }

    /// <summary>Help menu: check GitHub on demand and report the outcome.</summary>
    private void OnSendFeedback(object sender, RoutedEventArgs e)
    {
      try
      {
        // Feedback lands in the project's GitHub issues — the same single inbox the crash
        // reporter uses; version and OS are pre-filled so users don't have to know them.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
          FileName = CrashReporter.BuildFeedbackUrl(),
          UseShellExecute = true,
        });
      }
      catch
      {
        ViewModel.MessageService.DisplayMessageBox(
          Strings.Msg_FeedbackNoBrowser,
          Strings.Msg_SendFeedbackTitle,
          DeepNestLib.MessageBoxIcon.Information);
      }
    }

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
            Strings.Msg_UpdateCheckFailed,
            Strings.Msg_CheckUpdatesTitle,
            DeepNestLib.MessageBoxIcon.Information);
          return;
        }

        if (!UpdateChecker.IsNewer(result.Value.Latest, current))
        {
          ViewModel.MessageService.DisplayMessageBox(
            string.Format(Strings.Msg_UpToDateFormat, current.ToString(3)),
            Strings.Msg_CheckUpdatesTitle,
            DeepNestLib.MessageBoxIcon.Information);
          return;
        }

        this.ShowUpdateNotice(result.Value.Latest, result.Value.Url);
        var answer = ViewModel.MessageService.DisplayOkCancel(
          string.Format(Strings.Msg_UpdateAvailableFormat, result.Value.Latest.ToString(3), current.ToString(3)),
          Strings.Msg_CheckUpdatesTitle,
          DeepNestLib.MessageBoxIcon.Information);
        if (answer == DeepNestLib.MessageBoxResult.OK)
        {
          UpdateChecker.OpenDownloadPage(result.Value.Url);
        }
      }
      catch (Exception ex)
      {
        Mouse.OverrideCursor = null;
        ViewModel.MessageService.DisplayMessageBox(ex.Message, Strings.Msg_CheckUpdatesTitle, DeepNestLib.MessageBoxIcon.Stop);
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
        this.recentProjectsMenu.Items.Add(new MenuItem { Header = Strings.Menu_RecentEmpty, IsEnabled = false });
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

    private void OnAdvancedSettings(object sender, RoutedEventArgs e)
    {
      var cfg = ViewModel.SvgNestConfigViewModel.SvgNestConfig;
      double previousCommonLineGapInches = cfg.CommonLineGapInches;
      int previousCommonLineMaxClusterParts = cfg.CommonLineMaxClusterParts;
      var dialog = new AdvancedSettingsWindow(
        cfg,
        this.autosaveEnabled,
        this.autosaveMinutes,
        this.unitsMm,
        commonLineGapInches: cfg.CommonLineGapInches,
        effort: this.nestEffort,
        commonLineMaxClusterParts: cfg.CommonLineMaxClusterParts)
      {
        Owner = this,
      };
      if (dialog.ShowDialog() != true)
      {
        return;
      }

      this.ApplyAutosaveSettings(dialog.AutosaveEnabled, dialog.AutosaveMinutes);
      cfg.CommonLineGapInches = dialog.CommonLineGapInches;
      cfg.CommonLineMaxClusterParts = dialog.CommonLineMaxClusterParts;
      // Confirming Advanced Settings is an explicit operator choice: apply it to this document and also
      // remember it for future new jobs. Merely activating/opening a document never writes these fields.
      this.sessionCommonLineGapInches = cfg.CommonLineGapInches;
      this.sessionCommonLineMaxClusterParts = cfg.CommonLineMaxClusterParts;
      if ((System.Math.Abs(previousCommonLineGapInches - cfg.CommonLineGapInches) > 1e-12
            || previousCommonLineMaxClusterParts != cfg.CommonLineMaxClusterParts)
          && ViewModel.ActiveDocument is NestProjectViewModel project)
      {
        // Unlike machine/search preferences, this value changes placed geometry and therefore belongs
        // in the .dnest. Mark the job so Save/Autosave actually writes the new value.
        project.ProjectInfo.CommonLineGapInches = cfg.CommonLineGapInches;
        project.ProjectInfo.CommonLineMaxClusterParts = cfg.CommonLineMaxClusterParts;
        project.IsDirty = true;
      }

      this.unitsMm = dialog.UnitsMm;
      this.ApplyUnits();

      // The settings-backed config values persisted in their setters; the rest goes to the session
      // immediately (load-modify-save keeps the MRU and the other session fields intact).
      var session = SessionState.Load() ?? new SessionState();
      session.AutosaveEnabled = this.autosaveEnabled;
      session.AutosaveMinutes = this.autosaveMinutes;
      session.DefaultRotations = cfg.Rotations;
      session.SheetEdgeMargin = System.Math.Max(0, cfg.SheetSpacing);
      session.UnitsMm = this.unitsMm;
      session.CommonLineGapInches = cfg.CommonLineGapInches;
      session.CommonLineMaxClusterParts = cfg.CommonLineMaxClusterParts;
      this.nestEffort = dialog.Effort;
      session.NestEffort = (int)this.nestEffort;
      session.Save();

      // Two places set the same preference, so the toolbar is rebuilt from the field rather than left
      // showing whatever it showed before the dialog was opened.
      this.RefreshNestEffortMenu();
    }

    /// <summary>
    /// Settings > Language: Automatic (Windows), then every language SheetNest was built with, each in its own
    /// words. A pick is saved at once and takes effect the next time SheetNest opens, when every window is
    /// built in it.
    /// </summary>
    private void BuildLanguageMenu(string? saved)
    {
      var languageMenu = new MenuItem { Header = Strings.Menu_Language };
      var choices = new List<(string Header, string? Culture)> { (Strings.Menu_LanguageAutomatic, null) };
      choices.AddRange(Languages.Available(AppContext.BaseDirectory).Select(c => (Languages.DisplayName(c), (string?)c.Name)));
      foreach (var (header, culture) in choices)
      {
        var item = new MenuItem
        {
          Header = header,
          IsCheckable = true,
          IsChecked = string.Equals(culture ?? string.Empty, saved ?? string.Empty, StringComparison.OrdinalIgnoreCase),
        };
        item.Click += (_, __) => this.OnLanguageChosen(languageMenu, item, culture);
        languageMenu.Items.Add(item);
      }

      this.settingsMenu.Items.Add(new Separator());
      this.settingsMenu.Items.Add(languageMenu);
    }

    private void OnLanguageChosen(MenuItem languageMenu, MenuItem chosen, string? culture)
    {
      // A checkable item toggles itself on click, so the ticks are put back from the choice.
      foreach (var item in languageMenu.Items.OfType<MenuItem>())
      {
        item.IsChecked = ReferenceEquals(item, chosen);
      }

      if (string.Equals(LanguagePreference.Read() ?? string.Empty, culture ?? string.Empty, StringComparison.OrdinalIgnoreCase))
      {
        return;
      }

      LanguagePreference.Write(culture);
      ViewModel.MessageService.DisplayMessageBox(Strings.Msg_LanguageNextStart, Strings.Menu_Language, DeepNestLib.MessageBoxIcon.Information);
    }

    /// <summary>The arrow half of the NEST split button. Anchored under the button so the menu reads as
    /// belonging to it rather than appearing at the pointer.</summary>
    private void OnNestEffortMenu(object sender, RoutedEventArgs e)
    {
      if (this.nestEffortButton.ContextMenu is System.Windows.Controls.ContextMenu menu)
      {
        menu.PlacementTarget = this.nestEffortButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
      }
    }

    private void OnNestEffortChosen(object sender, RoutedEventArgs e)
    {
      if (sender is System.Windows.Controls.MenuItem item
          && item.Tag is string tag
          && int.TryParse(tag, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int chosen))
      {
        this.nestEffort = DeepNestSharp.RasterNest.NestEffortDefaults.FromPersisted(chosen);
        var session = SessionState.Load() ?? new SessionState();
        session.NestEffort = (int)this.nestEffort;
        session.Save();
      }

      // Unconditionally: a checkable MenuItem toggles itself on click, so even the item that was already
      // ticked has to be put back the way the field says, or clicking the current effort unticks it.
      this.RefreshNestEffortMenu();
    }

    /// <summary>
    /// Rebuilds the ticks and the tooltip from <see cref="nestEffort"/>, never from whichever control was
    /// clicked, so the toolbar and Advanced Settings cannot drift apart.
    /// </summary>
    private void RefreshNestEffortMenu()
    {
      if (this.effortFastItem == null)
      {
        return; // called before the toolbar exists
      }

      this.effortFastItem.IsChecked = this.nestEffort == DeepNestSharp.RasterNest.NestEffort.Fast;
      this.effortNormalItem.IsChecked = this.nestEffort == DeepNestSharp.RasterNest.NestEffort.Normal;
      this.effortBestItem.IsChecked = this.nestEffort == DeepNestSharp.RasterNest.NestEffort.Best;

      string name = this.nestEffort switch
      {
        DeepNestSharp.RasterNest.NestEffort.Normal => Strings.Main_EffortNameNormal,
        DeepNestSharp.RasterNest.NestEffort.Best => Strings.Main_EffortNameBest,
        _ => Strings.Main_EffortNameFast,
      };

      // Says what it actually buys. It reads as "how long you wait", but what it changes is how TIGHTLY
      // the parts end up packed — and on a common-line job that tightness is what decides whether the
      // last part of a row gets to share its cut or sits on its own at the normal gap.
      this.nestEffortButton.ToolTip = string.Format(Strings.Main_EffortTipFormat, name);
    }

    private void OnOffcutSettings(object sender, RoutedEventArgs e)
    {
      var (limits, spacing, edgeOverlap) = this.EffectiveOffcutLimits();
      var dialog = new OffcutSettingsWindow(this.preferRectOffcut, this.offcutDirection, spacing, limits, edgeOverlap, this.unitsMm) { Owner = this };
      if (dialog.ShowDialog() != true)
      {
        return;
      }

      this.preferRectOffcut = dialog.OffcutEnabled;
      this.offcutDirection = dialog.Direction;
      this.offcutSpacing = dialog.Spacing;
      this.offcutMinX = dialog.Limits.MinX;
      this.offcutMinY = dialog.Limits.MinY;
      this.offcutMaxX = dialog.Limits.MaxX;
      this.offcutMaxY = dialog.Limits.MaxY;
      this.offcutEdgeOverlap = dialog.EdgeOverlap;

      var session = SessionState.Load();
      session.PreferRectangularOffcut = this.preferRectOffcut;
      session.OffcutDirection = this.offcutDirection;
      session.OffcutSpacing = this.offcutSpacing;
      session.OffcutMinX = this.offcutMinX;
      session.OffcutMinY = this.offcutMinY;
      session.OffcutMaxX = this.offcutMaxX;
      session.OffcutMaxY = this.offcutMaxY;
      session.OffcutEdgeOverlap = this.offcutEdgeOverlap;
      session.Save();

      // Reflect the new settings on the on-screen result immediately (overlay only — the packing
      // itself only changes on the next NEST).
      if (this.dxfViewer != null)
      {
        this.dxfViewer.OffcutOptions = this.CurrentOffcutOptions();
        this.dxfViewer.RefreshRender();
      }
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
        ReturnStock(doc.ProjectInfo.SheetLoadInfos, lastNestConsumed);
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

      if (IsFreshUnsavedProject(doc))
      {
        ApplyCommonLinePreferencesToNewProject(
          doc.ProjectInfo,
          this.sessionCommonLineGapInches,
          this.sessionCommonLineMaxClusterParts);
      }

      // ProjectInfo keeps this geometry-affecting value per document while the runtime config is shared.
      // Reapply the active tab's snapshot before nesting/export/UI consumers can observe another tab's gap.
      ViewModel.SvgNestConfigViewModel.SvgNestConfig.CommonLineGapInches =
        doc.ProjectInfo.CommonLineGapInches;
      ViewModel.SvgNestConfigViewModel.SvgNestConfig.CommonLineMaxClusterParts =
        doc.ProjectInfo.CommonLineMaxClusterParts;
      this.ApplyUnits();

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
          this.dxfViewer.SheetEdgeMargin = System.Math.Max(0, ViewModel.SvgNestConfigViewModel.SvgNestConfig.SheetSpacing);
          this.dxfViewer.PartSpacings = doc.ProjectInfo.DetailLoadInfos
            .Where(o => !string.IsNullOrWhiteSpace(o.Path))
            .GroupBy(o => o.Path, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
              g => g.Key,
              g => g.Min(o => o.Spacing >= 0 ? o.Spacing : System.Math.Max(0, ViewModel.SvgNestConfigViewModel.SvgNestConfig.Spacing)),
              System.StringComparer.OrdinalIgnoreCase);
        }

        this.ApplyPartColours(doc.ProjectInfo.DetailLoadInfos);

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
    /// Feeds the counts on the part and sheet cards: sets each row's NestedCount (sheets consumed / parts
    /// placed by the on-screen result; 0 = no result) and the wrappers derive Remaining and Total from it.
    /// The counts are ALWAYS shown; with no result nothing is nested and everything remains.
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

      // Sheets: sheets of this size the nest consumed (recorded at deduction time); the wrapper's
      // Quantity is already what is left, and its TotalCount adds this back.
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
          Strings.Msg_SaveBeforeClosing,
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

      // Sheet stock is deliberately NOT part of the session anymore — the app starts empty and each
      // saved .dnest project carries its own stock. Update the existing session instead of replacing it:
      // replacing it used to erase every preference not named here, including NestEffort.
      var session = SessionState.Load() ?? new SessionState();
      session.CaptureApplicationClose(
        System.Math.Max(0, ViewModel.SvgNestConfigViewModel.SvgNestConfig.SheetSpacing),
        DeepNestLib.IO.StepUnfoldService.KFactor,
        DeepNestLib.IO.StepUnfoldService.KFactorStandard,
        DeepNestLib.IO.StepUnfoldService.FreeCadCmdPathOverride,
        DeepNestLib.IO.StepUnfoldService.UnfoldUnitInch,
        this.unitsMm,
        this.userSheetPresets.Select(p => new SessionSheet { Width = p.W, Height = p.H }),
        this.autosaveEnabled,
        this.autosaveMinutes,
        ViewModel.SvgNestConfigViewModel.SvgNestConfig.Rotations,
        this.sessionCommonLineGapInches,
        this.sessionCommonLineMaxClusterParts);
      session.Save();
    }

    internal static (double GapInches, int MaxClusterParts) CommonLinePreferencesFrom(SessionState? session)
      => session == null
        ? (CommonLineDefaults.DefaultGapInches, CommonLineDefaults.DefaultMaxClusterParts)
        : (session.EffectiveCommonLineGapInches, session.EffectiveCommonLineMaxClusterParts);

    internal static void ApplyCommonLinePreferencesToNewProject(
      IProjectInfo project,
      double gapInches,
      int maxClusterParts)
    {
      if (project == null)
      {
        return;
      }

      // ProjectInfo owns normalization, keeping this handoff identical to deserialization and the dialog.
      project.CommonLineGapInches = gapInches;
      project.CommonLineMaxClusterParts = maxClusterParts;
    }

    private static bool IsFreshUnsavedProject(NestProjectViewModel document)
      => document != null
        && string.IsNullOrWhiteSpace(document.FilePath)
        && document.ProjectInfo.DetailLoadInfos.Count == 0
        && document.ProjectInfo.SheetLoadInfos.Count == 0
        && string.IsNullOrWhiteSpace(document.ProjectInfo.LastNestResultJson);

    /// <summary>Reflects the drawing units (in/mm) in every unit-labelled UI element.</summary>
    private void ApplyUnits()
    {
      this.Resources["UnitsLabel"] = this.unitsMm ? "mm" : "in";
      if (this.dxfViewer != null)
      {
        this.dxfViewer.UnitsMm = this.unitsMm;
        this.dxfViewer.CommonLineGap = ViewModel.SvgNestConfigViewModel.SvgNestConfig.CommonLineGapInches
          * (this.unitsMm ? CommonLineDefaults.InchesToMillimeters : 1.0);
      }

      this.RefreshNestCards(); // their sizes carry the units
    }

    /// <summary>Persists the user's sheet presets right away (load-modify-save, like the MRU).</summary>
    private void SaveUserSheetPresets()
    {
      var session = SessionState.Load() ?? new SessionState();
      session.SheetPresets = this.userSheetPresets.Select(p => new SessionSheet { Width = p.W, Height = p.H }).ToList();
      session.Save();
    }

    /// <summary>Add Sheet opens a menu: "My sheets" (user presets) + standard stock sizes + "Custom size…".</summary>
    private void OnAddSheetMenu(object sender, RoutedEventArgs e)
    {
      var menu = new ContextMenu();
      string unit = this.unitsMm ? "mm" : "in";

      if (this.userSheetPresets.Count > 0)
      {
        menu.Items.Add(new MenuItem { Header = Strings.Main_MySheets, IsEnabled = false, FontWeight = FontWeights.Bold });
        foreach (var preset in this.userSheetPresets.OrderBy(p => p.H).ThenBy(p => p.W))
        {
          var size = preset;
          var item = new MenuItem { Header = $"{size.W} × {size.H} {unit}" };
          item.Click += (_, __) => this.AddSheetOfSize(size.W, size.H, 1);
          menu.Items.Add(item);
        }

        var removeMenu = new MenuItem { Header = Strings.Main_RemoveFromMySheets };
        foreach (var preset in this.userSheetPresets.OrderBy(p => p.H).ThenBy(p => p.W))
        {
          var size = preset;
          var item = new MenuItem { Header = $"{size.W} × {size.H} {unit}" };
          item.Click += (_, __) =>
          {
            this.userSheetPresets.Remove(size);
            this.SaveUserSheetPresets();
          };
          removeMenu.Items.Add(item);
        }

        menu.Items.Add(removeMenu);
        menu.Items.Add(new Separator());
      }

      int lastShortSide = -1;
      foreach (var preset in this.unitsMm ? SheetPresetsMm : SheetPresets)
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
      var custom = new MenuItem { Header = Strings.Main_CustomSize };
      custom.Click += (_, __) =>
      {
        var dialog = new AddSheetWindow(this.unitsMm) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
          this.AddSheetOfSize(dialog.SheetWidth, dialog.SheetHeight, dialog.SheetQuantity, dialog.SheetUnlimited);
          if (dialog.RememberAsPreset && !this.userSheetPresets.Contains((dialog.SheetWidth, dialog.SheetHeight)))
          {
            this.userSheetPresets.Add((dialog.SheetWidth, dialog.SheetHeight));
            this.SaveUserSheetPresets();
          }
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
      var dialog = new AddSheetWindow(this.unitsMm) { Owner = this };
      dialog.PrefillForEdit(row.Width, row.Height, row.Quantity, row.Unlimited);
      if (dialog.ShowDialog() == true)
      {
        row.Width = dialog.SheetWidth;
        row.Height = dialog.SheetHeight;
        row.Quantity = dialog.SheetQuantity;
        row.Unlimited = dialog.SheetUnlimited;
        this.sheetsListView.Items.Refresh();
      }
    }

    private void AddSheetOfSize(int width, int height, int quantity, bool unlimited = false)
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
          sheet.Unlimited = unlimited;
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
        Title = Strings.Main_Import3DDialogTitle,
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
              Languages.Count(result.Paths.Count, Strings.Main_ImportedParts_One, Strings.Main_ImportedParts_Other) + " "
              + Languages.Count(result.Skipped, Strings.Main_SkippedSolids_One, Strings.Main_SkippedSolids_Other),
              Strings.Msg_Import3DTitle,
              DeepNestLib.MessageBoxIcon.Information);
          }
        }
        catch (DeepNestLib.IO.StepUnfoldException ex)
        {
          System.Windows.Input.Mouse.OverrideCursor = null;
          ViewModel.MessageService.DisplayMessageBox(ex.Message, Strings.Msg_Import3DTitle, DeepNestLib.MessageBoxIcon.Stop);
        }
        catch (System.Exception ex)
        {
          System.Windows.Input.Mouse.OverrideCursor = null;
          CrashReporter.Show(ex, "import-3d", this);
        }
      }
    }

    /// <summary>
    /// Hands the job's part colours to everything that draws a part: the nest viewer, and the thumbnails in
    /// the parts list — which ARE the legend, one row per colour.
    /// <para>Built from EVERY row that has a path, included or not. Keyed off only the included ones,
    /// ticking a part's checkbox would silently shift all the other colours, and that checkbox is bound
    /// straight to the view model with no hook here to catch it. An excluded part just holds a colour
    /// nobody uses.</para>
    /// </summary>
    private void ApplyPartColours(System.Collections.Generic.IEnumerable<DeepNestLib.NestProject.IDetailLoadInfo> parts)
    {
      var map = PartColors.Build(parts?.Select(o => (o.Path, o.ColorRgb)));
      if (this.dxfViewer != null)
      {
        this.dxfViewer.PartColours = map;
      }

      DeepNestSharp.Ui.Converters.PartPreviewConverter.SetColours(map);

      // Tell each row the colour it ENDED UP with, so its swatch shows the real thing even when the user
      // never picked one. Only the user's own choice is saved with the project; this is display only.
      foreach (var row in parts ?? System.Linq.Enumerable.Empty<DeepNestLib.NestProject.IDetailLoadInfo>())
      {
        if (row is DeepNestSharp.Domain.Models.ObservableDetailLoadInfo observable)
        {
          observable.EffectiveColorRgb = PartColors.ToRgb(PartColors.For(map, row.Path));
        }
      }

      this.partsListView?.Items.Refresh();
    }

    /// <summary>True while the Nests list is being filled or re-selected to match the viewer, so its own
    /// SelectionChanged does not push that selection straight back into the viewer.</summary>
    private bool syncingNests;

    /// <summary>
    /// Rebuilds the Nests panel from the layouts the viewer holds, in the viewer's order so card n is layout
    /// n, and selects the one on screen. Runs on a new result or none, after every edit, undo or redo that
    /// moves a part, and after a units change, because the sizes carry the units.
    /// </summary>
    private void RefreshNestCards()
    {
      if (this.nestsList == null || this.dxfViewer == null)
      {
        return;
      }

      var plan = this.dxfViewer.GetProductionPlan();
      var cards = NestCard.From(plan, this.unitsMm);
      for (int i = 0; i < cards.Count; i++)
      {
        cards[i].Thumbnail = NestCard.DrawThumbnail(plan[i].Sheet);
      }

      this.syncingNests = true;
      this.nestsList.ItemsSource = cards;
      this.nestsList.SelectedIndex = cards.Count == 0 ? -1 : Math.Max(0, Math.Min(this.dxfViewer.SheetIndex, cards.Count - 1));
      this.syncingNests = false;
      this.nestsEmptyHint.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Picking a nest card puts that layout in the viewer.</summary>
    private void OnNestsListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
      if (!this.syncingNests && this.nestsList.SelectedIndex >= 0)
      {
        this.dxfViewer.SheetIndex = this.nestsList.SelectedIndex;
      }
    }

    /// <summary>The viewer can change layout by itself, undoing an edit made on another one, and the selected
    /// card has to follow it.</summary>
    private void SelectShownNest()
    {
      if (this.nestsList == null || this.nestsList.Items.Count == 0)
      {
        return;
      }

      this.syncingNests = true;
      this.nestsList.SelectedIndex = Math.Max(0, Math.Min(this.dxfViewer.SheetIndex, this.nestsList.Items.Count - 1));
      this.syncingNests = false;
    }

    /// <summary>One palette entry as the popup's swatch grid shows it.</summary>
    private sealed class PaletteSwatch
    {
      public int Rgb { get; set; }

      public string Label { get; set; }

      public System.Windows.Media.Brush Brush { get; set; }
    }

    /// <summary>The row whose colour the popup is currently editing.</summary>
    private DeepNestLib.NestProject.IDetailLoadInfo partBeingColoured;

    /// <summary>True while the canvas is being SET to the part's current colour — that is not the user
    /// choosing anything, and acting on it would recolour a part just by opening the picker.</summary>
    private bool suppressPartColorCanvas;

    /// <summary>Opens the colour picker over the swatch that was clicked.</summary>
    private void OnPartColorClick(object sender, RoutedEventArgs e)
    {
      if (!(sender is FrameworkElement swatch) || !(swatch.Tag is DeepNestLib.NestProject.IDetailLoadInfo part))
      {
        return;
      }

      this.partBeingColoured = part;

      if (this.partColorSwatches.ItemsSource == null)
      {
        var palette = new System.Collections.Generic.List<PaletteSwatch>();
        for (int i = 0; i < PartColors.PaletteLength; i++)
        {
          var (r, g, b) = PartColors.PaletteAt(i);
          var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
          brush.Freeze();
          palette.Add(new PaletteSwatch { Rgb = PartColors.ToRgb((r, g, b)), Label = string.Format(Strings.Main_PaletteSwatchFormat, i + 1), Brush = brush });
        }

        this.partColorSwatches.ItemsSource = palette;
      }

      // Start the canvas on the colour the part has now, without that assignment counting as a change.
      var (cr, cg, cb) = PartColors.FromRgb(Math.Max(0, part.ColorRgb >= 0 ? part.ColorRgb : this.EffectiveColorOf(part)));
      this.partColorPopup.PlacementTarget = swatch;
      this.suppressPartColorCanvas = true;
      this.partColorCanvas.SelectedColor = System.Windows.Media.Color.FromRgb(cr, cg, cb);
      this.suppressPartColorCanvas = false;
      this.partColorPopup.IsOpen = true;
    }

    private void OnPartColorSwatchClick(object sender, RoutedEventArgs e)
    {
      if (sender is FrameworkElement swatch && swatch.Tag is int rgb)
      {
        this.ApplyChosenPartColour(rgb);
        this.partColorPopup.IsOpen = false;
      }
    }

    private void OnPartColorCanvasChanged(object sender, RoutedPropertyChangedEventArgs<System.Windows.Media.Color?> e)
    {
      if (!this.suppressPartColorCanvas && e.NewValue.HasValue)
      {
        this.ApplyChosenPartColour(PartColors.ToRgb((e.NewValue.Value.R, e.NewValue.Value.G, e.NewValue.Value.B)));
      }
    }

    /// <summary>Remembers the colour on the part, then repaints everything that draws it — the row's
    /// swatch, its thumbnail and the sheet.</summary>
    private void ApplyChosenPartColour(int rgb)
    {
      var part = this.partBeingColoured;
      if (part == null || part.ColorRgb == rgb)
      {
        return;
      }

      part.ColorRgb = rgb;
      var doc = ViewModel.ActiveDocument as NestProjectViewModel;
      this.ApplyPartColours(doc?.ProjectInfo?.DetailLoadInfos);
      this.dxfViewer?.RefreshRender();
    }

    private int EffectiveColorOf(DeepNestLib.NestProject.IDetailLoadInfo part)
      => PartColors.ToRgb(PartColors.For(this.dxfViewer?.PartColours, part.Path));

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

      // A new part changes the set of colours, and the thumbnails have to follow.
      this.ApplyPartColours(doc.ProjectInfo.DetailLoadInfos);

      // Insert flow: adding a single part opens Edit Part right away. Skipped for 3D
      // imports (autoEditSingle=false) — their unfold is slow, so the part loads async in the grid.
      //
      // A drawing that held several parts adds several rows, so this deliberately does not fire: there
      // is no single part to edit, and picking one of them would be a guess. The operator sees the rows
      // and opens whichever they meant.
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

        // The flat's path changed, and the path is what names the colour.
        this.ApplyPartColours((ViewModel.ActiveDocument as NestProjectViewModel)?.ProjectInfo?.DetailLoadInfos);
        this.partsListView.Items.Refresh();
      }
      catch (DeepNestLib.IO.StepUnfoldException ex)
      {
        part.KFactor = oldK; // revert so Edit Part shows the value that actually produced the current flat
        part.KFactorStandard = oldStd;
        ViewModel.MessageService.DisplayMessageBox(ex.Message, Strings.Msg_ReunfoldTitle, DeepNestLib.MessageBoxIcon.Stop);
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
      // SheetNest has ONE engine: sparrow, run as a bundled native subprocess.
      var project = (ViewModel.ActiveDocument as NestProjectViewModel)?.ProjectInfo;
      if (project == null || project.DetailLoadInfos.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox(Strings.Msg_AddPartsFirst, Strings.Msg_NestTitle, DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      // One result at a time: its sheets are already deducted from the stock, so nesting again
      // would consume more sheets on top of it. Clear Result gives them back and unlocks NEST.
      if (!ViewModel.NestMonitorViewModel.TopNestResults.IsEmpty)
      {
        ViewModel.MessageService.DisplayMessageBox(
          Strings.Msg_ResultExists,
          Strings.Msg_NestTitle,
          DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      // Read the UI-bound project data on the UI thread, then do the heavy nest off-thread so the app
      // stays responsive (it was freezing because the whole nest ran on the UI thread).

      var parts = project.DetailLoadInfos
        .Where(o => o.IsIncluded && !string.IsNullOrWhiteSpace(o.Path))
        .SelectMany(o =>
        {
          var cc = o.EffectiveCommonCutting;
          string commonCuttingDomain = CommonCuttingDomainOf(o);
          bool directCommonCutting = RequiresMergedLinesForDirectCommonCutting(o);
          var populations = new List<RasterPartInfo>
          {
            new RasterPartInfo
            {
              Path = o.Path,
              Quantity = o.Quantity + o.Extra,               // required + spares
              Rotations = o.Rotations,                       // -1 = engine default
              Priority = o.Priority,                         // higher nests first
              Spacing = o.Spacing,                           // kept to whoever it cannot share a cut with; -1 = job default
              Cc = cc,
              CommonCuttingDomain = commonCuttingDomain,
              RequiresMergedLines = directCommonCutting,
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
              Spacing = o.Spacing,
              Cc = cc,
              CommonCuttingDomain = commonCuttingDomain,
              RequiresMergedLines = directCommonCutting,
              Mirrored = true,
            });
          }

          return populations;
        })
        .ToList();
      // EVERY sheet entry participates. For each sheet the engine packs the pending work onto every size
      // still in stock and cuts whichever filled most of its own sheet, so list order decides nothing; a
      // size marked Unlimited never runs down, and its Quantity is carried along only to be ignored.
      var sheetStock = project.SheetLoadInfos
        .Where(s => s.Width > 0 && s.Height > 0 && (s.Unlimited || s.Quantity > 0))
        .Select(s => (s.Width, s.Height, s.Quantity, s.Unlimited))
        .ToList();
      if (sheetStock.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox(Strings.Msg_AddSheetFirst, Strings.Msg_NestTitle, DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      // Total sheets the job may use — only meaningful when every size is counted, which is why the
      // warning that quotes it is skipped once a size is unlimited.
      int sheetQty = sheetStock.Sum(s => s.Quantity);
      var config = ViewModel.SvgNestConfigViewModel.SvgNestConfig;
      int rotations = config.Rotations;
      double spacing = config.Spacing;          // part spacing (drawing units = inches)
      double margin = config.SheetSpacing;      // sheet edge margin

      // The nester is the external sparrow engine (a bundled native binary). It must be present.
      string sparrowExe = ResolveSparrowExe();
      if (sparrowExe == null)
      {
        ViewModel.MessageService.DisplayMessageBox(
          Strings.Msg_EngineMissing,
          Strings.Msg_NestTitle,
          DeepNestLib.MessageBoxIcon.Warning);
        return;
      }

      var button = sender as Button;
      if (button != null)
      {
        button.IsEnabled = false;
      }

      nestCts = new System.Threading.CancellationTokenSource();
      this.cancelNestButton.Content = Strings.Main_Cancel;
      this.cancelNestButton.IsEnabled = true;
      this.cancelNestButton.Visibility = Visibility.Visible;
      this.nestPhaseText.Text = string.Format(Strings.Main_NestingFormat, "0:00");
      this.nestPhaseText.Visibility = Visibility.Visible;

      // A stopwatch + timer keep the elapsed clock ticking between engine reports (the engine only reports
      // on sheet/density events, which can be seconds apart).
      this.nestStopwatch = System.Diagnostics.Stopwatch.StartNew();
      this.nestTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
      this.nestTimer.Tick += (_, __) => this.RenderNestProgress();
      this.nestTimer.Start();

      // The status just shows the elapsed clock (the DispatcherTimer keeps it ticking); the engine's per-sheet
      // reports aren't needed for that, but we still refresh on each so the clock updates promptly.
      var nestProgress = new System.Progress<(int Placed, int Total, int Sheet, double Density)>(_ =>
      {
        this.RenderNestProgress();
      });

      bool nestSucceeded = false;
      bool nestCancelled = false;
      try
      {
        // Hidden end-to-end test hook for the crash reporter (set SHEETNEST_CRASH_TEST=1).
        if (System.Environment.GetEnvironmentVariable("SHEETNEST_CRASH_TEST") == "1")
        {
          throw new System.InvalidOperationException("Synthetic crash for testing the problem-report dialog.");
        }

        var token = nestCts.Token;
        var (result, error) = await Task.Run(() =>
        {
          // The sparrow engine: dense collision-separation nesting, per-sheet, corner-compacted, with
          // pattern replication across identical sheets. Works in real drawing units (no raster grid).
          // perSheetBudgetSec no longer bounds the search: how hard a sheet is searched is the operator's
          // effort setting, in iterations, so two machines agree on the nest and only the clock differs. What
          // survives here is the scale for the watchdog that kills a hung engine process.
          // Common cutting rides along on each part's own mode; there is no job-wide flag to keep in step.
          double unitScale = this.unitsMm ? CommonLineDefaults.InchesToMillimeters : 1.0;
          var r = SparrowNestService.Nest(
            parts,
            sheetStock,
            rotations,
            spacing,
            margin,
            5,
            sparrowExe,
            out string err,
            token,
            nestProgress,
            geometryTolerance: CommonLineDefaults.DefaultGeometryToleranceInches * unitScale,
            commonLineGap: config.CommonLineGapInches * unitScale,
            effort: this.nestEffort,
            commonLineMaxClusterParts: config.CommonLineMaxClusterParts);
          return (r, err);
        });

        if (result == null)
        {
          ViewModel.MessageService.DisplayMessageBox(error ?? Strings.Msg_NoResult, Strings.Msg_NestTitle, DeepNestLib.MessageBoxIcon.Information);
          return;
        }

        nestSucceeded = true;

        // Hand the per-part spacings to the viewer BEFORE showing the result, so manual nesting
        // (drag/rotate/nudge) enforces the same clearances the nester used — common-line parts may
        // touch, spaced parts keep (spacingA + spacingB) / 2.
        this.ApplyPartColours(project.DetailLoadInfos);

        if (this.dxfViewer != null)
        {
          this.dxfViewer.DefaultPartSpacing = System.Math.Max(0, spacing);

          // The same edge margin the engine packed to, so hand-editing keeps the parts off the sheet edge.
          this.dxfViewer.SheetEdgeMargin = System.Math.Max(0, margin);
          this.dxfViewer.OffcutOptions = this.CurrentOffcutOptions();

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

        lastNestConsumed = ConsumeStock(project.SheetLoadInfos, used);
        this.sheetsListView.Items.Refresh();
        this.UpdateNestedInfo(result);

        // Not enough sheets for the whole order? Say so clearly — don't let a partial nest pass as done.
        // With an unlimited size in the stock the job never ran OUT of sheets, so telling the operator to
        // raise a quantity would send them to change a number that is already ignored: the only way a part
        // is left over then is that no sheet size is big enough to hold it.
        int unplacedCount = result.UnplacedParts?.Count ?? 0;
        if (unplacedCount > 0)
        {
          bool anyUnlimited = project.SheetLoadInfos.Any(s => s.Unlimited);
          ViewModel.MessageService.DisplayMessageBox(
            anyUnlimited
              ? Languages.Count(unplacedCount, Strings.Msg_PartsDoNotFitAnySize_One, Strings.Msg_PartsDoNotFitAnySize_Other)
              : Languages.Count(
                  unplacedCount,
                  Strings.Msg_PartsDidNotFit_One,
                  Strings.Msg_PartsDidNotFit_Other,
                  Languages.Count(sheetQty, Strings.Msg_AvailableSheets_One, Strings.Msg_AvailableSheets_Other)),
            anyUnlimited ? Strings.Msg_PartsDoNotFitTitle : Strings.Msg_NotEnoughSheetsTitle,
            DeepNestLib.MessageBoxIcon.Warning);
        }
      }
      catch (System.Exception cancelEx) when (IsCancellation(cancelEx))
      {
        // User pressed Cancel: not an error — discard everything and return to idle. Cancellations
        // can also arrive WRAPPED: a Parallel.For without the token in its options bundles the
        // workers' OperationCanceledExceptions into an AggregateException (user-reported crash
        // dialog on Cancel during the tail sweep).
        this.nestPhaseText.Text = Strings.Main_NestCancelled;
        nestCancelled = true;
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
        string elapsed = NestElapsed();
        this.nestTimer?.Stop();
        this.nestTimer = null;
        this.nestStopwatch?.Stop();
        this.nestStopwatch = null;

        this.cancelNestButton.Visibility = Visibility.Collapsed;

        // Keep the final time on screen after a successful nest; keep "Nest cancelled" if cancelled;
        // otherwise (error) clear the status text.
        if (nestSucceeded)
        {
          CommonLineFallbackNotice[] commonLineFallbacks;
          lock (SparrowNestService.DiagCommonLineFallbacks)
          {
            commonLineFallbacks = SparrowNestService.DiagCommonLineFallbacks.ToArray();
          }

          // Say what common cutting actually managed. Giving a group up used to be completely silent,
          // so a job could come out with no shared cuts on it and look exactly like a job that never
          // had any to make.
          string seams = SparrowNestService.DiagSeamsSnapped > 0
              || SparrowNestService.DiagSeamsGivenUp > 0
              || commonLineFallbacks.Length > 0
            ? Languages.Count(SparrowNestService.DiagSeamsSnapped, Strings.Main_SharedCuts_One, Strings.Main_SharedCuts_Other)
              + (SparrowNestService.DiagSeamsGivenUp > 0
                ? string.Format(Strings.Main_GivenUpFormat, SparrowNestService.DiagSeamsGivenUp)
                : string.Empty)
              + (commonLineFallbacks.Length > 0
                ? Languages.Count(
                    commonLineFallbacks.Select(notice => notice.Drawing).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Strings.Main_DrawingsKept_One,
                    Strings.Main_DrawingsKept_Other)
                : string.Empty)
            : string.Empty;

          this.nestPhaseText.Text = string.Format(Strings.Main_NestedInFormat, elapsed, seams);

          if (commonLineFallbacks.Length > 0)
          {
            ViewModel.MessageService.DisplayMessageBox(
              CommonLineFallbackMessage(commonLineFallbacks),
              Strings.Msg_CommonLineTitle,
              DeepNestLib.MessageBoxIcon.Warning);
          }

        }
        else if (!nestCancelled)
        {
          this.nestPhaseText.Visibility = Visibility.Collapsed;
        }

        nestCts.Dispose();
        nestCts = null;
        if (button != null)
        {
          button.IsEnabled = true;
        }
      }
    }

    /// <summary>Renders the live nest status: just the elapsed clock (no progress bar — a global % can't be
    /// known accurately). The final time is kept on screen when the nest ends.</summary>
    private void RenderNestProgress()
    {
      this.nestPhaseText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Strings.Main_NestingFormat, NestElapsed());
    }

    /// <summary>Elapsed nest time as m:ss (0:00 if the stopwatch isn't running).</summary>
    private string NestElapsed()
    {
      return this.nestStopwatch is { } sw
        ? FormattableString.Invariant($"{(int)sw.Elapsed.TotalMinutes}:{sw.Elapsed.Seconds:00}")
        : "0:00";
    }

    /// <summary>Locates the external quality engine: SHEETNEST_SPARROW env var, else sparrow.exe beside the app.</summary>
    private static string ResolveSparrowExe()
    {
      var env = System.Environment.GetEnvironmentVariable("SHEETNEST_SPARROW");
      if (!string.IsNullOrWhiteSpace(env) && System.IO.File.Exists(env))
      {
        return env;
      }

      var beside = System.IO.Path.Combine(System.AppContext.BaseDirectory, "sparrow.exe");
      return System.IO.File.Exists(beside) ? beside : null;
    }

    private void OnCancelNest(object sender, RoutedEventArgs e)
    {
      // Cooperative: the engine checks the token at its phase boundaries (sub-second on real jobs).
      this.cancelNestButton.Content = Strings.Main_Cancelling;
      this.cancelNestButton.IsEnabled = false;
      nestCts?.Cancel();
    }

    private static bool IsCancellation(System.Exception ex)
    {
      return ex is System.OperationCanceledException
        || (ex is System.AggregateException agg
            && agg.Flatten().InnerExceptions.Count > 0
            && agg.Flatten().InnerExceptions.All(e => e is System.OperationCanceledException));
    }

    /// <summary>
    /// Manual editing may leave a part on top of a neighbour or off the sheet — that is what makes
    /// rearranging possible — so the check that used to live in the editor lives here instead: nothing
    /// leaves the app while any layout is still wrong. Checks EVERY layout, not the one on screen.
    /// </summary>
    private bool RefuseWhileAnythingOverlaps(string title)
    {
      var unfit = this.dxfViewer?.FindUnfitLayouts();
      if (unfit == null || unfit.Count == 0)
      {
        return false;
      }

      // Say WHICH fault, per layout. The two are not the same job: an overlap is moved apart by hand, while
      // a part inside the edge margin usually means the margin wants lowering and the nest re-running.
      int parts = unfit.Sum(u => u.Parts);
      var where = string.Join("\n", unfit.Select(u => $"    {u.Layout}: {Languages.Count(u.Parts, Strings.Main_UnfitParts_One, Strings.Main_UnfitParts_Other)}{Why(u)}"));
      ViewModel.MessageService.DisplayMessageBox(
        Languages.Count(parts, Strings.Msg_NotReadyToCut_One, Strings.Msg_NotReadyToCut_Other, where),
        title,
        DeepNestLib.MessageBoxIcon.Stop);
      return true;

      static string Why((string Layout, int Parts, int Overlapping, int InMargin) u)
      {
        if (u.Overlapping > 0 && u.InMargin > 0)
        {
          return string.Format(Strings.Main_UnfitBothFormat, u.Overlapping, u.InMargin);
        }

        return u.InMargin > 0 ? Strings.Main_UnfitInMargin : Strings.Main_UnfitOverlapping;
      }
    }

    private void OnNestReportPdf(object sender, RoutedEventArgs e)
    {
      var selected = ViewModel.NestMonitorViewModel?.SelectedItem;
      if (selected == null || selected.UsedSheets == null || selected.UsedSheets.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox(
          Strings.Msg_SelectResultForReport,
          Strings.Msg_NestReportTitle,
          DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      if (this.RefuseWhileAnythingOverlaps(Strings.Msg_NestReportTitle))
      {
        return;
      }

      // Report exactly what the viewer shows: one entry per distinct layout with its cut count.
      var plan = this.dxfViewer.GetProductionPlan();
      if (plan == null || plan.Count == 0)
      {
        plan = selected.UsedSheets.Select(s => ((DeepNestLib.Placement.ISheetPlacement)s, 1, Strings.Main_ReportSheetName)).ToList();
      }

      var dialog = new Microsoft.Win32.SaveFileDialog
      {
        Filter = Strings.Main_PdfFilter,
        FileName = Strings.Main_ReportFileName,
        Title = Strings.Main_SaveReportTitle,
      };
      if (dialog.ShowDialog(this) != true)
      {
        return;
      }

      try
      {
        Reports.NestReportPdf.Write(
          dialog.FileName,
          plan,
          selected.UnplacedParts?.Count ?? 0,
          this.unitsMm ? "mm" : "in");
      }
      catch (System.Exception ex)
      {
        ViewModel.MessageService.DisplayMessageBox(
          string.Format(Strings.Msg_ReportWriteFailed, ex.Message),
          Strings.Msg_NestReportTitle,
          DeepNestLib.MessageBoxIcon.Error);
      }
    }

    private async void OnExportDxf(object sender, RoutedEventArgs e)
    {
      var selected = ViewModel.NestMonitorViewModel?.SelectedItem;
      if (selected == null || selected.UsedSheets == null || selected.UsedSheets.Count == 0)
      {
        ViewModel.MessageService.DisplayMessageBox(
          Strings.Msg_SelectResultForExport,
          Strings.Msg_ExportTitle,
          DeepNestLib.MessageBoxIcon.Information);
        return;
      }

      if (this.RefuseWhileAnythingOverlaps(Strings.Msg_ExportTitle))
      {
        return;
      }

      // Export one DXF per DISTINCT layout (the production plan tells how many of each to cut), so two
      // identical sheets don't each produce a duplicate DXF. Fall back to every sheet if no grouping.
      var layouts = this.dxfViewer.GetDistinctLayoutSheets();
      if (layouts != null && layouts.Count > 0)
      {
        var offcutSheet = this.dxfViewer.OffcutSheet;
        for (int li = 0; li < layouts.Count; li++)
        {
          // The offcut separation cut(s) travel only with the genuine remainder sheet the engine
          // shaped (same rule as the viewer overlay) — never a replicated pattern or a synthetic
          // production-plan representative — so the cut matches the packed remainder exactly.
          var offcut = ReferenceEquals(layouts[li], offcutSheet) ? OffcutGeometry.BuildLines(layouts[li], this.CurrentOffcutOptions()) : null;
          await ViewModel.ExportSheetPlacementAsync(layouts[li], offcut);
        }
      }
      else
      {
        var used = selected.UsedSheets.ToList();
        for (int si = 0; si < used.Count; si++)
        {
          var offcut = si == used.Count - 1 ? OffcutGeometry.BuildLines(used[si], this.CurrentOffcutOptions()) : null;
          await ViewModel.ExportSheetPlacementAsync(used[si], offcut);
        }
      }
    }

    /// <summary>
    /// The contract a part shares a cut under. Only parts naming the same non-empty contract may share
    /// one, so a part that has not confirmed direct common cutting names none and shares with nobody.
    /// </summary>
    internal static string CommonCuttingDomainOf(IDetailLoadInfo part)
    {
      if (part == null || part.EffectiveCommonCutting == CommonCuttingMode.None)
      {
        return string.Empty;
      }

      return part.DirectCommonCuttingEnabled ? "direct" : string.Empty;
    }

    /// <summary>
    /// A direct common-cutting source makes its layout authoritative for DXF line merging. Unsupported
    /// formats must never force an exporter that cannot represent the shared edge once.
    /// </summary>
    internal static bool RequiresMergedLinesForDirectCommonCutting(IDetailLoadInfo part)
      => part != null
        && part.SupportsCommonCutting
        && part.DirectCommonCuttingEnabled
        && part.EffectiveCommonCutting != CommonCuttingMode.None;

    /// <summary>
    /// Charge a finished nest to the stock: counted sizes lose what it used, and the record of what each
    /// size gave up comes back for the Available badge and for <see cref="ReturnStock"/> to undo.
    /// <para>An unlimited size is REPORTED and not charged. Those are two different jobs and conflating
    /// them is what put "0 used" on the badge: leaving the size out of the record to stop Clear Result
    /// crediting it also took away the only number the badge had to show.</para>
    /// <para>Lives here, out of the click handler, so it can be tested at all. The whole reason it exists
    /// as a method is that it failed twice in one afternoon inside <c>OnNest</c>, where the suite cannot
    /// reach it without instantiating a WPF Window.</para>
    /// </summary>
    /// <param name="rows">The stock rows, whose Quantity this mutates.</param>
    /// <param name="usedBySize">How many sheets of each size the nest consumed. Not modified.</param>
    internal static Dictionary<(int W, int H), int> ConsumeStock(
      IEnumerable<ISheetLoadInfo> rows,
      IReadOnlyDictionary<(int W, int H), int> usedBySize)
    {
      var consumed = new Dictionary<(int W, int H), int>();
      if (rows == null || usedBySize == null)
      {
        return consumed;
      }

      // A copy, because the remaining count is walked down as rows claim it — two rows of the same size
      // must share what was used rather than both taking all of it. Doing that to the caller's dictionary
      // would leave it emptied behind their back.
      var remaining = usedBySize.ToDictionary(kv => kv.Key, kv => kv.Value);

      foreach (var row in rows)
      {
        var key = (row.Width, row.Height);
        if (!remaining.TryGetValue(key, out int n) || n <= 0)
        {
          continue;
        }

        int take = row.Unlimited ? n : Math.Min(row.Quantity, n);
        if (!row.Unlimited)
        {
          row.Quantity -= take;
        }

        remaining[key] = n - take;
        if (take > 0)
        {
          consumed[key] = consumed.TryGetValue(key, out int c) ? c + take : take;
        }
      }

      return consumed;
    }

    /// <summary>
    /// Undo <see cref="ConsumeStock"/>: the sheets a discarded result was holding go back on the rack.
    /// <para>An unlimited size gets nothing, because nothing was taken from it. That guard has to live
    /// here rather than at the deduction: reopening a saved project rebuilds the consumed record from the
    /// result itself, so this is the only place BOTH routes into a give-back pass through.</para>
    /// </summary>
    internal static void ReturnStock(
      System.Collections.Generic.IList<ISheetLoadInfo> rows,
      IReadOnlyDictionary<(int W, int H), int> consumed)
    {
      if (rows == null || consumed == null)
      {
        return;
      }

      foreach (var kv in consumed)
      {
        var row = rows.FirstOrDefault(s => s.Width == kv.Key.W && s.Height == kv.Key.H);
        if (row == null)
        {
          // The user removed the row after nesting — bring the size back so no stock is lost.
          rows.Add(new SheetLoadInfo(kv.Key.W, kv.Key.H, kv.Value));
        }
        else if (!row.Unlimited)
        {
          row.Quantity += kv.Value;
        }
      }
    }

    /// <summary>Formats the per-drawing reason a requested direct common line retained normal spacing.</summary>
    internal static string CommonLineFallbackMessage(IEnumerable<CommonLineFallbackNotice> notices)
    {
      var rows = (notices ?? Enumerable.Empty<CommonLineFallbackNotice>())
        .Where(notice => notice != null)
        .GroupBy(notice => notice.Drawing, StringComparer.OrdinalIgnoreCase)
        .Select(group => string.Format(
          Strings.Main_FallbackRowFormat,
          group.Key,
          string.Join("; ", group.Select(notice => notice.DisplayReason).Distinct(StringComparer.Ordinal))))
        .ToArray();
      if (rows.Length == 0)
      {
        return string.Empty;
      }

      return string.Format(Strings.Msg_CommonLineSkippedFormat, string.Join("\n", rows));
    }

    /// <summary>
    /// The size window and edge overlap in force: what was saved, or the recommended figure for the
    /// drawing's units. Resolved HERE, where the geometry is built, and handed to the dialog already
    /// resolved — a default that lived only in the window would be a lie, showing 12 on screen while the
    /// cut was still computed the automatic way.
    /// <para>-1 means never saved and takes the default; an explicit 0 is the user asking for automatic
    /// (the 5% rule on a minimum, no limit on a maximum) and is left alone.</para>
    /// </summary>
    private (OffcutLimits Limits, double Spacing, double EdgeOverlap) EffectiveOffcutLimits()
    {
      var (d, defaultSpacing, defaultOverlap) = OffcutOptions.Defaults(this.unitsMm);
      return (
        new OffcutLimits(
          this.offcutMinX >= 0 ? this.offcutMinX : d.MinX,
          this.offcutMinY >= 0 ? this.offcutMinY : d.MinY,
          this.offcutMaxX >= 0 ? this.offcutMaxX : d.MaxX,
          this.offcutMaxY >= 0 ? this.offcutMaxY : d.MaxY),
        this.offcutSpacing >= 0 ? this.offcutSpacing : defaultSpacing,
        this.offcutEdgeOverlap >= 0 ? this.offcutEdgeOverlap : defaultOverlap);
    }

    /// <summary>The active offcut settings, or null when the option is off.</summary>
    private OffcutOptions CurrentOffcutOptions()
    {
      if (!this.preferRectOffcut)
      {
        return null;
      }

      var (limits, spacing, edgeOverlap) = this.EffectiveOffcutLimits();
      return new OffcutOptions
      {
        Direction = (OffcutDirection)System.Math.Max(0, System.Math.Min(3, this.offcutDirection)),
        Spacing = spacing,
        MinX = limits.MinX > 0 ? limits.MinX : -1,
        MinY = limits.MinY > 0 ? limits.MinY : -1,
        MaxX = limits.MaxX > 0 ? limits.MaxX : -1,
        MaxY = limits.MaxY > 0 ? limits.MaxY : -1,
        EdgeOverlap = System.Math.Max(0, edgeOverlap),
      };
    }
  }
}
