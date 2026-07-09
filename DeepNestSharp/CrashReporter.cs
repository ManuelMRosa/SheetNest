namespace DeepNestSharp
{
  using System;
  using System.IO;
  using System.Text;
  using DeepNestSharp.Ui.Views;

  /// <summary>
  /// Consent-based crash reporting: every unhandled error is written to a local log under
  /// %LOCALAPPDATA%\SheetNest, and the user is OFFERED a pre-filled GitHub issue in the browser —
  /// nothing ever leaves the machine unless they submit it there themselves.
  /// </summary>
  public static class CrashReporter
  {
    private const string IssuesUrl = "https://github.com/ManuelMRosa/SheetNest/issues/new";

    /// <summary>App version, 3 fields (matches the window title).</summary>
    public static string Version =>
      typeof(CrashReporter).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>The full human-readable report (also what "Copy details" copies).</summary>
    public static string BuildReport(Exception ex, string context)
    {
      var sb = new StringBuilder();
      sb.AppendLine($"SheetNest {Version} crash report");
      sb.AppendLine($"When:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
      sb.AppendLine($"Where:   {context}");
      sb.AppendLine($"OS:      {Environment.OSVersion}");
      sb.AppendLine($".NET:    {Environment.Version}");
      sb.AppendLine();
      sb.AppendLine(ex?.ToString() ?? "(no exception details)");
      return sb.ToString();
    }

    /// <summary>
    /// Writes the report to a timestamped log file; returns its path (null if even that failed —
    /// this method must never throw, it runs while the app is already in trouble).
    /// </summary>
    public static string Save(Exception ex, string context)
    {
      try
      {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SheetNest");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(path, BuildReport(ex, context));
        return path;
      }
      catch
      {
        return null;
      }
    }

    /// <summary>
    /// GitHub new-issue URL pre-filled with the crash summary. Kept comfortably under browser/
    /// server URL limits by truncating the stack — the dialog's "Copy details" has the full text.
    /// </summary>
    public static string BuildIssueUrl(Exception ex, string context)
    {
      string title = $"Crash: {ex?.GetType().Name ?? "Unknown"} during {context} (v{Version})";
      string stack = ex?.ToString() ?? "(no details)";
      if (stack.Length > 1500)
      {
        stack = stack.Substring(0, 1500) + "\n... (truncated - full log attached below if possible)";
      }

      string body =
        $"**Version:** {Version}\n**OS:** {Environment.OSVersion}\n**Where:** {context}\n\n" +
        "**What I was doing:**\n(please describe)\n\n```\n" + stack + "\n```";
      return $"{IssuesUrl}?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>
    /// GitHub new-issue URL pre-filled as a FEEDBACK template (Help → Send Feedback): the user
    /// describes the idea/problem in their own words; version and OS ride along automatically.
    /// </summary>
    public static string BuildFeedbackUrl()
    {
      string body =
        "**What happened / what would you like:**\n\n\n" +
        "**What did you expect:**\n\n\n---\n" +
        $"Version: {Version} · OS: {Environment.OSVersion}";
      return $"{IssuesUrl}?labels=feedback&title=&body={Uri.EscapeDataString(body)}";
    }

    /// <summary>Saves the log and shows the consent dialog. Never throws.</summary>
    public static void Show(Exception ex, string context, System.Windows.Window owner)
    {
      try
      {
        string path = Save(ex, context);
        var dialog = new CrashReportWindow(BuildReport(ex, context), path, BuildIssueUrl(ex, context));
        if (owner != null && owner.IsLoaded)
        {
          dialog.Owner = owner;
        }

        dialog.ShowDialog();
      }
      catch
      {
        // Reporting must never make things worse.
      }
    }
  }
}
