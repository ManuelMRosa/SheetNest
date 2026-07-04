namespace DeepNestSharp.Ui.Views
{
  using System.Windows;

  /// <summary>
  /// Consent dialog for crash reporting: shows the saved report and offers a pre-filled GitHub
  /// issue in the browser — the user reviews and submits it themselves, or just closes.
  /// </summary>
  public partial class CrashReportWindow : Window
  {
    private readonly string issueUrl;

    public CrashReportWindow(string report, string logPath, string issueUrl)
    {
      InitializeComponent();
      this.issueUrl = issueUrl;
      this.reportBox.Text = report;
      this.logPathRun.Text = logPath ?? "(the log could not be written)";
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
      try
      {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(this.issueUrl) { UseShellExecute = true });
      }
      catch
      {
        MessageBox.Show(this, "Could not open the browser. Use 'Copy details' and paste it into a new issue at github.com/ManuelMRosa/SheetNest/issues.", "SheetNest", MessageBoxButton.OK, MessageBoxImage.Information);
      }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
      try
      {
        Clipboard.SetText(this.reportBox.Text);
      }
      catch
      {
        // Clipboard can be locked by another app; the log file still has everything.
      }
    }
  }
}
