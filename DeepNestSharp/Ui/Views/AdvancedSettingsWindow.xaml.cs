namespace DeepNestSharp.Ui.Views
{
  using System.Windows;
  using System.Windows.Controls;
  using DeepNestLib;

  /// <summary>
  /// Curated settings dialog (replaces the old auto-generated property grid): only the options the
  /// live raster engine, the DXF export and the app itself consume. OK applies and persists;
  /// Cancel changes nothing.
  /// </summary>
  public partial class AdvancedSettingsWindow : Window
  {
    private readonly ISvgNestConfig config;
    private double commonLineGapInches;
    private int commonLineMaxClusterParts;
    private bool displayedUnitsMm;
    private bool suppressUnitsChanged = true;

    internal AdvancedSettingsWindow(
      ISvgNestConfig config,
      bool autosaveEnabled,
      int autosaveMinutes,
      bool unitsMm = false,
      double commonLineGapInches = DeepNestLib.Placement.CommonLineDefaults.DefaultGapInches,
      DeepNestSharp.RasterNest.NestEffort effort = DeepNestSharp.RasterNest.NestEffortDefaults.Default,
      int commonLineMaxClusterParts = DeepNestLib.Placement.CommonLineDefaults.DefaultMaxClusterParts)
    {
      this.config = config;
      this.commonLineGapInches = NormalizeCommonLineGapInches(commonLineGapInches);
      this.commonLineMaxClusterParts = NormalizeCommonLineMaxClusterParts(commonLineMaxClusterParts);
      InitializeComponent();

      this.unitsCombo.SelectedIndex = unitsMm ? 1 : 0;
      this.displayedUnitsMm = unitsMm;
      this.suppressUnitsChanged = false;
      this.UpdateUnitPresentation();

      SelectRotations(config.Rotations);
      this.effortCombo.SelectedIndex = (int)effort;
      this.spacingUpDown.Value = System.Math.Max(0, config.Spacing);
      this.marginUpDown.Value = System.Math.Max(0, config.SheetSpacing);
      this.mergeLinesCheck.IsChecked = config.MergeLines;
      this.differentiateCheck.IsChecked = config.DifferentiateChildren;

      bool automaticClusterLimit = this.commonLineMaxClusterParts
        == DeepNestLib.Placement.CommonLineDefaults.AutoMaxClusterParts;
      this.commonLineMaxClusterPartsUpDown.Value = automaticClusterLimit
        ? DeepNestLib.Placement.CommonLineDefaults.MinimumMaxClusterParts
        : this.commonLineMaxClusterParts;
      this.commonLineClusterModeCombo.SelectedIndex = automaticClusterLimit ? 0 : 1;
      this.UpdateCommonLineClusterLimitPresentation();

      this.autosaveCheck.IsChecked = autosaveEnabled;
      this.minutesUpDown.Value = autosaveMinutes;
      this.minutesUpDown.IsEnabled = autosaveEnabled;

      RefreshIntegrationStatus();
    }

    public bool AutosaveEnabled => this.autosaveCheck.IsChecked == true;

    /// <summary>Drawing units are millimeters — persisted by the caller (SessionState).</summary>
    public bool UnitsMm => this.unitsCombo.SelectedIndex == 1;

    /// <summary>The physical common-line gap, in canonical inches, for the caller to persist.</summary>
    public double CommonLineGapInches => this.commonLineGapInches;

    /// <summary>Zero selects automatic common-line cluster sizing; manual limits are 2..500.</summary>
    public int CommonLineMaxClusterParts => this.commonLineMaxClusterParts;

    public int AutosaveMinutes => this.minutesUpDown.Value ?? 5;

    /// <summary>Converts canonical inches to the units selected for display.</summary>
    internal static double CommonLineGapForDisplay(double inches, bool unitsMm)
      => inches * (unitsMm ? DeepNestLib.Placement.CommonLineDefaults.InchesToMillimeters : 1.0);

    /// <summary>Converts an edited display value back to canonical inches.</summary>
    internal static double CommonLineGapFromDisplay(double value, bool unitsMm)
      => value / (unitsMm ? DeepNestLib.Placement.CommonLineDefaults.InchesToMillimeters : 1.0);

    /// <summary>How hard the nester searches each sheet. Persisted by the caller (SessionState), per user
    /// and not per project: it is a preference about this operator's time, so opening somebody else's job
    /// must not change it.</summary>
    internal DeepNestSharp.RasterNest.NestEffort Effort => this.effortCombo.SelectedItem is ComboBoxItem e
      ? (DeepNestSharp.RasterNest.NestEffort)int.Parse((string)e.Tag, System.Globalization.CultureInfo.InvariantCulture)
      : DeepNestSharp.RasterNest.NestEffortDefaults.Default;

    /// <summary>The chosen global rotation code (1/2/4/8/36) — persisted by the caller.</summary>
    public int Rotations => this.rotationsCombo.SelectedItem is ComboBoxItem item
      ? int.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture)
      : 4;

    private void SelectRotations(int code)
    {
      foreach (ComboBoxItem item in this.rotationsCombo.Items)
      {
        if (int.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture) == code)
        {
          this.rotationsCombo.SelectedItem = item;
          return;
        }
      }

      this.rotationsCombo.SelectedIndex = 2; // 90° steps — the engine default
    }

    private void OnAutosaveToggled(object sender, RoutedEventArgs e)
    {
      if (this.minutesUpDown != null)
      {
        this.minutesUpDown.IsEnabled = this.autosaveCheck.IsChecked == true;
      }
    }

    private void OnUnitsChanged(object sender, SelectionChangedEventArgs e)
    {
      if (this.suppressUnitsChanged || this.commonLineGapUpDown == null)
      {
        return;
      }

      // SelectionChanged runs after the ComboBox has moved to its new item. Commit using the units that
      // were on screen BEFORE the change, then redraw the same physical distance in the new units.
      this.CommitCommonLineGap(this.displayedUnitsMm);
      this.displayedUnitsMm = this.UnitsMm;
      this.UpdateUnitPresentation();
    }

    private void OnResetCommonLineGap(object sender, RoutedEventArgs e)
    {
      this.commonLineGapInches = DeepNestLib.Placement.CommonLineDefaults.DefaultGapInches;
      this.ShowCommonLineGap();
    }

    private void OnCommonLineClusterModeChanged(object sender, SelectionChangedEventArgs e)
    {
      this.UpdateCommonLineClusterLimitPresentation();
    }

    private void OnResetCommonLineMaxClusterParts(object sender, RoutedEventArgs e)
    {
      this.commonLineMaxClusterParts = DeepNestLib.Placement.CommonLineDefaults.DefaultMaxClusterParts;
      this.commonLineClusterModeCombo.SelectedIndex = 0;
      this.commonLineMaxClusterPartsUpDown.Value =
        DeepNestLib.Placement.CommonLineDefaults.MinimumMaxClusterParts;
      this.UpdateCommonLineClusterLimitPresentation();
    }

    private void UpdateCommonLineClusterLimitPresentation()
    {
      if (this.commonLineMaxClusterPartsUpDown != null)
      {
        this.commonLineMaxClusterPartsUpDown.IsEnabled = this.commonLineClusterModeCombo?.SelectedIndex == 1;
      }
    }

    private void UpdateUnitPresentation()
    {
      string unit = this.displayedUnitsMm ? "mm" : "in";
      this.spacingLabel.Text = $"Part spacing ({unit}):";
      this.commonLineGapLabel.Text = $"Common Line Gap ({unit}):";
      this.marginLabel.Text = $"Sheet edge margin ({unit}):";
      this.ShowCommonLineGap();
    }

    private void ShowCommonLineGap()
    {
      double unitScale = this.displayedUnitsMm
        ? DeepNestLib.Placement.CommonLineDefaults.InchesToMillimeters
        : 1.0;
      this.commonLineGapUpDown.FormatString = this.displayedUnitsMm ? "0.#####" : "0.000##";
      this.commonLineGapUpDown.Minimum =
        2 * DeepNestLib.Placement.CommonLineDefaults.DefaultGeometryToleranceInches * unitScale;
      this.commonLineGapUpDown.Increment = 0.001 * unitScale;
      this.commonLineGapUpDown.Value = CommonLineGapForDisplay(this.commonLineGapInches, this.displayedUnitsMm);
    }

    private void CommitCommonLineGap(bool unitsMm)
    {
      this.commonLineGapUpDown.CommitInput();
      double fallback = CommonLineGapForDisplay(this.commonLineGapInches, unitsMm);
      double displayed = System.Math.Max(0, this.commonLineGapUpDown.Value ?? fallback);
      this.commonLineGapInches = NormalizeCommonLineGapInches(CommonLineGapFromDisplay(displayed, unitsMm));
    }

    private static double NormalizeCommonLineGapInches(double value)
      => value > DeepNestLib.Placement.CommonLineDefaults.DefaultGeometryToleranceInches
        && !double.IsNaN(value)
        && !double.IsInfinity(value)
        ? value
        : DeepNestLib.Placement.CommonLineDefaults.DefaultGapInches;

    internal static int NormalizeCommonLineMaxClusterParts(int value)
      => DeepNestLib.Placement.CommonLineDefaults.NormalizeMaxClusterParts(value);

    private void CommitCommonLineMaxClusterParts()
    {
      if (this.commonLineClusterModeCombo.SelectedIndex != 1)
      {
        this.commonLineMaxClusterParts = DeepNestLib.Placement.CommonLineDefaults.AutoMaxClusterParts;
        return;
      }

      this.commonLineMaxClusterPartsUpDown.CommitInput();
      this.commonLineMaxClusterParts = NormalizeCommonLineMaxClusterParts(
        this.commonLineMaxClusterPartsUpDown.Value
          ?? DeepNestLib.Placement.CommonLineDefaults.MinimumMaxClusterParts);
    }

    private void OnIntegrate(object sender, RoutedEventArgs e)
    {
      DeepNestSharp.Ui.Services.SheetCamIntegration.Integrate(this);
      RefreshIntegrationStatus();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
      DeepNestSharp.Ui.Services.SheetCamIntegration.Restore(this);
      RefreshIntegrationStatus();
    }

    private void RefreshIntegrationStatus()
    {
      bool integrated = DeepNestSharp.Ui.Services.SheetCamIntegration.IsIntegrated();
      this.integrationStatus.Text = integrated ? "Integrated" : "Not integrated";
      this.integrateButton.IsEnabled = !integrated;
      this.restoreButton.IsEnabled = integrated;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
      // Xceed up/downs commit typed text on focus loss; Enter (OK is IsDefault) would keep stale values.
      this.spacingUpDown.CommitInput();
      this.marginUpDown.CommitInput();
      this.minutesUpDown.CommitInput();
      this.CommitCommonLineGap(this.displayedUnitsMm);
      this.CommitCommonLineMaxClusterParts();

      // Settings-backed properties persist in their setters; Rotations and the autosave options are
      // persisted by the caller (SessionState).
      this.config.Rotations = Rotations;
      this.config.Spacing = System.Math.Max(0, this.spacingUpDown.Value ?? this.config.Spacing);
      this.config.SheetSpacing = System.Math.Max(0, this.marginUpDown.Value ?? this.config.SheetSpacing);
      this.config.MergeLines = this.mergeLinesCheck.IsChecked == true;
      this.config.DifferentiateChildren = this.differentiateCheck.IsChecked == true;

      this.DialogResult = true;
    }
  }
}
