namespace DeepNestSharp.Ui.Views
{
  using System.Windows;
  using SheetNest.Localization;

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
      this.logPathRun.Text = logPath ?? Strings.Crash_LogNotWritten;
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
      try
      {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(this.issueUrl) { UseShellExecute = true });
      }
      catch
      {
        MessageBox.Show(this, Strings.Crash_MsgBrowserFailed, "SheetNest", MessageBoxButton.OK, MessageBoxImage.Information);
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
