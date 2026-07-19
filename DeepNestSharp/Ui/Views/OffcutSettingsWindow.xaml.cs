namespace DeepNestSharp.Ui.Views
{
  using System.Windows;

  /// <summary>
  /// Dedicated offcut settings dialog: the remnant position is chosen from preview cards drawn in
  /// the overlay's visual language, plus the cut-line spacing and the minimum worthwhile remnant
  /// width. Results are exposed as read-only properties; the caller (MainWindow) persists them in
  /// SessionState.
  /// </summary>
  public partial class OffcutSettingsWindow : Window
  {
    public OffcutSettingsWindow(bool enabled, int direction, double spacing, double minWidth, bool unitsMm, double defaultSpacing)
    {
      InitializeComponent();

      string u = unitsMm ? "mm" : "in";
      this.spacingLabel.Text = $"Offcut spacing ({u}):";
      this.minWidthLabel.Text = $"Minimum remnant width ({u}):";

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

      // -1 = never saved: default the cut-line gap to the part spacing (the remnant is a part too).
      this.spacingUpDown.Value = spacing >= 0 ? spacing : System.Math.Max(0, defaultSpacing);

      // -1 = never saved: shown as 0, which means the automatic 5% rule.
      this.minWidthUpDown.Value = System.Math.Max(0, minWidth);
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

    /// <summary>Narrowest strip worth separating; 0 = automatic (5% rule) — persisted by the caller.</summary>
    public double MinWidth => System.Math.Max(0, this.minWidthUpDown.Value ?? 0);

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
      this.minWidthUpDown.CommitInput();
      this.DialogResult = true;
    }
  }
}
