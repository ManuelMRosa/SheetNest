namespace DeepNestSharp.Ui.Services
{
  using System;
  using System.Diagnostics;
  using System.IO;
  using System.Windows;

  /// <summary>
  /// Wires SheetCam's Auto Nesting to SheetNest. SheetCam launches a fixed path,
  /// <c>plugins\AutoNesting\NestingPrototype.exe</c>, and reloads the .nest when it exits — so integrating
  /// backs that exe up and drops the SheetNest shim in its place, which forwards the job to SheetNest and
  /// waits. Writing into Program Files needs elevation, so the file work runs in one elevated shim call.
  /// </summary>
  internal static class SheetCamIntegration
  {
    /// <summary>True when the shim is installed (the target file + the vendor backup are both present).</summary>
    internal static bool IsIntegrated()
    {
      var dir = FindAutoNestingDir();
      return dir != null
        && File.Exists(Path.Combine(dir, "sheetnest-target.txt"))
        && File.Exists(Path.Combine(dir, "NestingPrototype.vendor.exe"));
    }

    /// <summary>Backs up SheetCam's nester and installs the shim. Shows its own messages. Owner may be null.</summary>
    internal static void Integrate(Window owner)
    {
      var pluginDir = ResolveAutoNestingDir(owner);
      if (pluginDir == null)
      {
        return;
      }

      var shim = Path.Combine(AppContext.BaseDirectory, "NestingPrototype.exe");
      var sheetNestExe = Path.Combine(AppContext.BaseDirectory, "SheetNest.exe");
      if (!File.Exists(shim) || !File.Exists(sheetNestExe))
      {
        Warn(owner, "Integrate with SheetCam",
          "This SheetNest install is missing files it needs to integrate (NestingPrototype.exe / SheetNest.exe next to the app). Reinstall and try again.");
        return;
      }

      // The shim does the elevated file work itself (File.Copy with real errors + a log). Then re-read the
      // plugin folder to confirm the swap actually took.
      if (!RunShimElevated(owner, shim, $"--install \"{pluginDir}\" \"{sheetNestExe}\"", "Integrate with SheetCam"))
      {
        return;
      }

      var installedExe = Path.Combine(pluginDir, "NestingPrototype.exe");
      var targetFile = Path.Combine(pluginDir, "sheetnest-target.txt");
      bool swapped = File.Exists(targetFile) && new FileInfo(installedExe).Length == new FileInfo(shim).Length;
      if (!swapped)
      {
        Warn(owner, "Integrate with SheetCam",
          "The integration did not complete. Close SheetCam if it is open and try again.\n\n" +
          $"Details were logged to {Path.Combine(Path.GetTempPath(), "sheetnest-integrate.log")}.");
        return;
      }

      Info(owner, "Integrate with SheetCam",
        "Done. SheetCam's nesting now runs through SheetNest.\n\n" +
        "Don't open SheetNest yourself for this. In SheetCam use \"Export to Auto Nesting\" and " +
        "SheetNest will open with the job. Nest it, review, then press \"Send to SheetCam\".\n\n" +
        "The original nester was backed up; a SheetCam update may undo this, so just run Integrate again if so.");
    }

    /// <summary>Restores SheetCam's own nester and removes the shim files. Shows its own messages.</summary>
    internal static void Restore(Window owner)
    {
      var pluginDir = ResolveAutoNestingDir(owner);
      if (pluginDir == null)
      {
        return;
      }

      var vendorBackup = Path.Combine(pluginDir, "NestingPrototype.vendor.exe");
      if (!File.Exists(vendorBackup))
      {
        Info(owner, "Restore SheetCam's Nester",
          "SheetNest is not integrated with SheetCam (no backup of the original nester was found).");
        return;
      }

      var shim = Path.Combine(AppContext.BaseDirectory, "NestingPrototype.exe");
      if (!RunShimElevated(owner, shim, $"--uninstall \"{pluginDir}\"", "Restore SheetCam's Nester"))
      {
        return;
      }

      if (File.Exists(vendorBackup))
      {
        Warn(owner, "Restore SheetCam's Nester", "The restore did not complete. Close SheetCam if it is open and try again.");
        return;
      }

      Info(owner, "Restore SheetCam's Nester", "Restored. SheetCam's Auto Nesting will use its own nester again.");
    }

    /// <summary>The AutoNesting plugin dir if it exists, else null (no message).</summary>
    private static string FindAutoNestingDir()
    {
      foreach (var root in new[]
      {
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
      })
      {
        if (string.IsNullOrEmpty(root))
        {
          continue;
        }

        var candidate = Path.Combine(root, "SheetCam TNG Integration", "plugins", "AutoNesting");
        if (File.Exists(Path.Combine(candidate, "NestingPrototype.exe")))
        {
          return candidate;
        }
      }

      return null;
    }

    /// <summary>Like <see cref="FindAutoNestingDir"/> but warns the user when SheetCam is not found.</summary>
    private static string ResolveAutoNestingDir(Window owner)
    {
      var dir = FindAutoNestingDir();
      if (dir == null)
      {
        Warn(owner, "SheetCam not found",
          "Could not find SheetCam's AutoNesting plugin. Make sure SheetCam TNG is installed.\n\n" +
          "Expected: <Program Files (x86)>\\SheetCam TNG Integration\\plugins\\AutoNesting\\NestingPrototype.exe");
      }

      return dir;
    }

    /// <summary>Runs the shim elevated (one UAC). False — with a message — on cancel or failure.</summary>
    private static bool RunShimElevated(Window owner, string shimExe, string arguments, string caption)
    {
      try
      {
        var psi = new ProcessStartInfo(shimExe, arguments)
        {
          UseShellExecute = true, // required for the runas verb (UAC elevation)
          Verb = "runas",
          WindowStyle = ProcessWindowStyle.Hidden,
        };
        using var proc = Process.Start(psi);
        proc.WaitForExit();
        return proc.ExitCode == 0;
      }
      catch (System.ComponentModel.Win32Exception)
      {
        // The user dismissed the UAC prompt (or elevation is blocked). Say so — silence reads as "nothing
        // happened".
        Info(owner, caption,
          "Windows did not grant administrator permission, so nothing was changed. " +
          "Integrating needs it to write into SheetCam's program folder.");
        return false;
      }
    }

    private static void Info(Window owner, string caption, string text)
      => Show(owner, caption, text, MessageBoxImage.Information);

    private static void Warn(Window owner, string caption, string text)
      => Show(owner, caption, text, MessageBoxImage.Warning);

    private static void Show(Window owner, string caption, string text, MessageBoxImage icon)
    {
      if (owner != null)
      {
        MessageBox.Show(owner, text, caption, MessageBoxButton.OK, icon);
      }
      else
      {
        MessageBox.Show(text, caption, MessageBoxButton.OK, icon);
      }
    }
  }
}
