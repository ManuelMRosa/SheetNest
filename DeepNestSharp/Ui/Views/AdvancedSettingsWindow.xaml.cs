namespace DeepNestSharp.Ui.Views
{
  using System.Linq;
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
    private readonly System.Collections.Generic.List<KerfPreset> kerfPresets;

    public AdvancedSettingsWindow(
      ISvgNestConfig config,
      bool autosaveEnabled,
      int autosaveMinutes,
      bool unitsMm = false,
      double jobKerfMm = -1,
      System.Collections.Generic.List<KerfPreset> kerfPresets = null,
      DeepNestLib.NestProject.CommonCuttingTolerances commonCutting = null)
    {
      this.config = config;
      this.kerfPresets = kerfPresets ?? new System.Collections.Generic.List<KerfPreset>();
      InitializeComponent();

      this.kerfUpDown.Value = jobKerfMm > 0 ? jobKerfMm : 0;
      this.RefreshKerfPresets();

      var cc = commonCutting ?? DeepNestLib.NestProject.CommonCuttingTolerances.Default;
      this.ccAngleUpDown.Value = cc.AngleToleranceDeg;
      this.ccMinEdgeUpDown.Value = cc.MinEdgeLengthKerfs;
      this.ccGapMinUpDown.Value = cc.GapMinKerfs;
      this.ccGapMaxUpDown.Value = cc.GapMaxKerfs;
      this.ccOverlapUpDown.Value = cc.MinOverlapKerfs;

      this.unitsCombo.SelectedIndex = unitsMm ? 1 : 0;
      string u = unitsMm ? "mm" : "in";
      this.spacingLabel.Text = $"Part spacing ({u}):";
      this.marginLabel.Text = $"Sheet edge margin ({u}):";

      SelectRotations(config.Rotations);
      this.spacingUpDown.Value = System.Math.Max(0, config.Spacing);
      this.marginUpDown.Value = System.Math.Max(0, config.SheetSpacing);
      this.mergeLinesCheck.IsChecked = config.MergeLines;
      this.differentiateCheck.IsChecked = config.DifferentiateChildren;

      this.autosaveCheck.IsChecked = autosaveEnabled;
      this.minutesUpDown.Value = autosaveMinutes;
      this.minutesUpDown.IsEnabled = autosaveEnabled;

      RefreshIntegrationStatus();
    }

    public bool AutosaveEnabled => this.autosaveCheck.IsChecked == true;

    /// <summary>Drawing units are millimeters — persisted by the caller (SessionState).</summary>
    public bool UnitsMm => this.unitsCombo.SelectedIndex == 1;

    public int AutosaveMinutes => this.minutesUpDown.Value ?? 5;

    /// <summary>The job's cut width in millimetres; -1 when the box is empty or zero, meaning unset.</summary>
    public double JobKerfMm => (this.kerfUpDown.Value ?? 0) > 0 ? this.kerfUpDown.Value.Value : -1;

    /// <summary>The saved kerfs after any adds or deletes; persisted by the caller (SessionState).</summary>
    public System.Collections.Generic.List<KerfPreset> KerfPresets => this.kerfPresets;

    /// <summary>The common cutting tolerances as edited; persisted by the caller (SessionState).</summary>
    public DeepNestLib.NestProject.CommonCuttingTolerances CommonCutting
    {
      get
      {
        var defaults = DeepNestLib.NestProject.CommonCuttingTolerances.Default;
        return new DeepNestLib.NestProject.CommonCuttingTolerances
        {
          AngleToleranceDeg = this.ccAngleUpDown.Value ?? defaults.AngleToleranceDeg,
          MinEdgeLengthKerfs = this.ccMinEdgeUpDown.Value ?? defaults.MinEdgeLengthKerfs,
          GapMinKerfs = this.ccGapMinUpDown.Value ?? defaults.GapMinKerfs,
          GapMaxKerfs = this.ccGapMaxUpDown.Value ?? defaults.GapMaxKerfs,
          MinOverlapKerfs = this.ccOverlapUpDown.Value ?? defaults.MinOverlapKerfs,
        };
      }
    }

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

    private void RefreshKerfPresets()
    {
      string keep = this.kerfPresetCombo.Text;
      this.kerfPresetCombo.ItemsSource = null;
      this.kerfPresetCombo.ItemsSource = this.kerfPresets
        .Select(p => p.Name)
        .ToList();
      this.kerfPresetCombo.Text = keep;
    }

    private void OnKerfPresetPicked(object sender, SelectionChangedEventArgs e)
    {
      if (this.kerfPresetCombo.SelectedItem is string name)
      {
        var hit = this.kerfPresets.FirstOrDefault(p => string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));
        if (hit != null)
        {
          this.kerfUpDown.Value = hit.KerfMm;
        }
      }
    }

    /// <summary>Remembers whatever is in the kerf box under the name typed into the combo.</summary>
    private void OnSaveKerfPreset(object sender, RoutedEventArgs e)
    {
      this.kerfUpDown.CommitInput();
      string name = (this.kerfPresetCombo.Text ?? string.Empty).Trim();
      double value = this.kerfUpDown.Value ?? 0;
      if (name.Length == 0 || value <= 0)
      {
        return; // nothing to name, or nothing worth naming
      }

      var hit = this.kerfPresets.FirstOrDefault(p => string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));
      if (hit != null)
      {
        hit.KerfMm = value;
      }
      else
      {
        this.kerfPresets.Add(new KerfPreset { Name = name, KerfMm = value });
      }

      this.RefreshKerfPresets();
    }

    private void OnDeleteKerfPreset(object sender, RoutedEventArgs e)
    {
      string name = (this.kerfPresetCombo.Text ?? string.Empty).Trim();
      this.kerfPresets.RemoveAll(p => string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));
      this.kerfPresetCombo.Text = string.Empty;
      this.RefreshKerfPresets();
    }

    private void OnAutosaveToggled(object sender, RoutedEventArgs e)
    {
      if (this.minutesUpDown != null)
      {
        this.minutesUpDown.IsEnabled = this.autosaveCheck.IsChecked == true;
      }
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
      this.kerfUpDown.CommitInput();
      this.ccAngleUpDown.CommitInput();
      this.ccMinEdgeUpDown.CommitInput();
      this.ccGapMinUpDown.CommitInput();
      this.ccGapMaxUpDown.CommitInput();
      this.ccOverlapUpDown.CommitInput();

      // A window that cannot describe a seam would just turn the feature off without saying so.
      try
      {
        this.CommonCutting.Validate();
      }
      catch (System.ArgumentOutOfRangeException)
      {
        MessageBox.Show(
          this,
          "Those common cutting tolerances cannot describe a seam. The gap window needs a smaller minimum than maximum, and the angle has to be above zero.",
          "Advanced Settings",
          MessageBoxButton.OK,
          MessageBoxImage.Warning);
        return;
      }

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
