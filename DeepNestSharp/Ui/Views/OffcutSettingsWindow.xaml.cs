namespace DeepNestSharp.Ui.Views
{
  using System.Windows;
  using DeepNestSharp.RasterNest;

  /// <summary>
  /// Dedicated offcut settings dialog: the remnant position is chosen from preview cards drawn in
  /// the overlay's visual language, plus the cut-line spacing, the size a remnant has to come out at
  /// to be worth cutting off, and how far the cut runs past the sheet edge. Results are exposed as
  /// read-only properties; the caller (MainWindow) persists them in SessionState.
  /// </summary>
  public partial class OffcutSettingsWindow : Window
  {
    // Kept so "Reset to defaults" can rebuild the numbers from the same source the window opened with.
    private readonly bool unitsMm;

    internal OffcutSettingsWindow(bool enabled, int direction, double spacing, OffcutLimits limits, double edgeOverlap, bool unitsMm)
    {
      InitializeComponent();

      this.unitsMm = unitsMm;

      string u = unitsMm ? "mm" : "in";
      this.spacingLabel.Text = $"Offcut spacing ({u}):";
      this.minXLabel.Text = $"Minimum X size ({u}):";
      this.minYLabel.Text = $"Minimum Y size ({u}):";
      this.maxXLabel.Text = $"Maximum X size ({u}):";
      this.maxYLabel.Text = $"Maximum Y size ({u}):";
      this.edgeOverlapLabel.Text = $"Edge overlap ({u}):";

      this.enableCheck.IsChecked = enabled;
      switch (direction)
      {
        case 1:
          this.sideRadio.IsChecked = true;
          break;
        case 2:
          this.bothRadio.IsChecked = true;
          break;
        case 3:
          this.autoRadio.IsChecked = true;
          break;
        default:
          this.endRadio.IsChecked = true;
          break;
      }

      // Every number arrives already resolved by MainWindow - what was saved, or the recommended figure for
      // these units - so the window can never show one thing while the cut is computed from another.
      this.spacingUpDown.Value = System.Math.Max(0, spacing);
      this.minXUpDown.Value = System.Math.Max(0, limits.MinX);
      this.minYUpDown.Value = System.Math.Max(0, limits.MinY);
      this.maxXUpDown.Value = System.Math.Max(0, limits.MaxX);
      this.maxYUpDown.Value = System.Math.Max(0, limits.MaxY);
      this.edgeOverlapUpDown.Value = System.Math.Max(0, edgeOverlap);
      this.OnEnableToggled(this, null);
    }

    /// <summary>Pack the last sheet toward one end for a rectangular offcut — persisted by the caller.</summary>
    public bool OffcutEnabled => this.enableCheck.IsChecked == true;

    /// <summary>Offcut direction (0 = end, 1 = side, 2 = both, 3 = auto) — persisted by the caller.</summary>
    public int Direction =>
      this.sideRadio.IsChecked == true ? 1 :
      this.bothRadio.IsChecked == true ? 2 :
      this.autoRadio.IsChecked == true ? 3 : 0;

    /// <summary>Gap between the packed parts and the offcut cut line — persisted by the caller.</summary>
    public double Spacing => System.Math.Max(0, this.spacingUpDown.Value ?? 0);

    /// <summary>The size window a remnant must fall inside; 0 on any bound = not set.</summary>
    internal OffcutLimits Limits => new OffcutLimits(
      System.Math.Max(0, this.minXUpDown.Value ?? 0),
      System.Math.Max(0, this.minYUpDown.Value ?? 0),
      System.Math.Max(0, this.maxXUpDown.Value ?? 0),
      System.Math.Max(0, this.maxYUpDown.Value ?? 0));

    /// <summary>How far the cut runs past the sheet edge at each end — persisted by the caller.</summary>
    public double EdgeOverlap => System.Math.Max(0, this.edgeOverlapUpDown.Value ?? 0);

    /// <summary>Puts the six numbers back to the recommended figures, from the SAME source MainWindow uses
    /// to fill them in the first place — so the button can never drift from what the window opens with. The
    /// remnant position and the enable tick are deliberately left alone: this is about values.</summary>
    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
      var (limits, spacing, edgeOverlap) = OffcutOptions.Defaults(this.unitsMm);

      this.spacingUpDown.Value = spacing;
      this.minXUpDown.Value = limits.MinX;
      this.minYUpDown.Value = limits.MinY;
      this.maxXUpDown.Value = limits.MaxX;
      this.maxYUpDown.Value = limits.MaxY;
      this.edgeOverlapUpDown.Value = edgeOverlap;
    }

    private void OnEnableToggled(object sender, RoutedEventArgs e)
    {
      if (this.detailsPanel != null)
      {
        this.detailsPanel.IsEnabled = this.enableCheck.IsChecked == true;
      }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
      // Xceed up/downs commit typed text on focus loss; Enter (OK is IsDefault) would keep stale values.
      this.spacingUpDown.CommitInput();
      this.minXUpDown.CommitInput();
      this.minYUpDown.CommitInput();
      this.maxXUpDown.CommitInput();
      this.maxYUpDown.CommitInput();
      this.edgeOverlapUpDown.CommitInput();
      this.DialogResult = true;
    }
  }
}
