namespace DeepNestSharp.Ui.Views
{
  using System.Windows;

  /// <summary>
  /// Application Settings: the SigmaNEST-style autosave options — an enable toggle ("Auto Save WS")
  /// and "Auto Save Time (min)".
  /// </summary>
  public partial class AppSettingsWindow : Window
  {
    public AppSettingsWindow(bool autosaveEnabled, int autosaveMinutes)
    {
      InitializeComponent();
      this.autosaveCheck.IsChecked = autosaveEnabled;
      this.minutesUpDown.Value = autosaveMinutes;
      this.minutesUpDown.IsEnabled = autosaveEnabled;
    }

    public bool AutosaveEnabled => this.autosaveCheck.IsChecked == true;

    public int AutosaveMinutes => this.minutesUpDown.Value ?? 5;

    private void OnAutosaveToggled(object sender, RoutedEventArgs e)
    {
      if (this.minutesUpDown != null)
      {
        this.minutesUpDown.IsEnabled = this.autosaveCheck.IsChecked == true;
      }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
      this.minutesUpDown.CommitInput(); // Xceed commits typed text on focus loss; Enter would keep the stale value
      this.DialogResult = true;
    }
  }
}
