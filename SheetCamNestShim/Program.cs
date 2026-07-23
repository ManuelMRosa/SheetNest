using System;
using System.Diagnostics;
using System.IO;

namespace SheetCamNestShim
{
    /// <summary>
    /// Stand-in for SheetCam's <c>NestingPrototype.exe</c>. SheetCam's AutoNesting plugin launches the
    /// nester as <c>NestingPrototype.exe "&lt;job&gt;.nest"</c> and, because it launches it with a
    /// <c>wxProcess</c>, reloads the file once that process exits. This forwards the job to SheetNest and
    /// blocks until SheetNest closes, so the reload picks up the nested result.
    /// <para>
    /// SheetNest's path is written next to this exe as <c>sheetnest-target.txt</c> by the "Integrate with
    /// SheetCam" action. If anything is missing, it falls back to the backed-up factory nester
    /// (<c>NestingPrototype.vendor.exe</c>) so SheetCam is never left without a working nester.
    /// </para>
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // Elevated install/uninstall modes, run by SheetNest's "Integrate with SheetCam" via runas.
            if (args.Length >= 1 && args[0] == "--install")
            {
                return Install(args.Length > 1 ? args[1] : null, args.Length > 2 ? args[2] : null);
            }

            if (args.Length >= 1 && args[0] == "--uninstall")
            {
                return Uninstall(args.Length > 1 ? args[1] : null);
            }

            var here = AppDomain.CurrentDomain.BaseDirectory;
            var nestPath = args.Length > 0 ? args[0] : null;

            var sheetNest = ReadTarget(Path.Combine(here, "sheetnest-target.txt"));
            if (sheetNest != null && File.Exists(sheetNest))
            {
                try
                {
                    var psi = new ProcessStartInfo(sheetNest)
                    {
                        Arguments = nestPath != null ? "--sheetcam \"" + nestPath + "\"" : string.Empty,
                        UseShellExecute = false,
                    };
                    using (var proc = Process.Start(psi))
                    {
                        proc.WaitForExit(); // SheetCam waits on THIS process, so we wait on SheetNest
                        return proc.ExitCode;
                    }
                }
                catch
                {
                    // fall through to the factory nester
                }
            }

            return RunFactoryNester(here, args);
        }

        // Exit codes the app distinguishes; it also verifies the end state itself.
        private const int Ok = 0;
        private const int BadArgs = 2;
        private const int Failed = 3;

        /// <summary>
        /// Points SheetCam's nester at SheetNest: backs up the factory NestingPrototype.exe (once), copies
        /// this shim in its place, and records where SheetNest lives. Runs elevated. Logs each step to
        /// %TEMP%\sheetnest-integrate.log so a failure can be diagnosed.
        /// </summary>
        private static int Install(string pluginDir, string sheetNestExe)
        {
            var log = new System.Text.StringBuilder();
            try
            {
                if (string.IsNullOrEmpty(pluginDir) || string.IsNullOrEmpty(sheetNestExe))
                {
                    return BadArgs;
                }

                var target = Path.Combine(pluginDir, "NestingPrototype.exe");
                var vendor = Path.Combine(pluginDir, "NestingPrototype.vendor.exe");
                var targetFile = Path.Combine(pluginDir, "sheetnest-target.txt");
                var self = Process.GetCurrentProcess().MainModule.FileName;

                log.AppendLine("plugin dir: " + pluginDir);
                log.AppendLine("shim: " + self);

                if (!File.Exists(target))
                {
                    log.AppendLine("ERROR: no NestingPrototype.exe in the plugin dir");
                    WriteLog(log);
                    return Failed;
                }

                // Back up the factory nester once. If a backup already exists we are re-integrating over a
                // previous shim — don't overwrite the real backup with the shim.
                if (!File.Exists(vendor))
                {
                    File.Copy(target, vendor, false);
                    log.AppendLine("backed up factory nester -> NestingPrototype.vendor.exe");
                }
                else
                {
                    log.AppendLine("backup already present, keeping it");
                }

                File.Copy(self, target, true);
                File.WriteAllText(targetFile, sheetNestExe);
                log.AppendLine("installed shim + wrote sheetnest-target.txt");
                WriteLog(log);
                return Ok;
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION: " + ex);
                WriteLog(log);
                return Failed;
            }
        }

        /// <summary>Restores the factory nester and removes the shim's files. Runs elevated.</summary>
        private static int Uninstall(string pluginDir)
        {
            var log = new System.Text.StringBuilder();
            try
            {
                if (string.IsNullOrEmpty(pluginDir))
                {
                    return BadArgs;
                }

                var target = Path.Combine(pluginDir, "NestingPrototype.exe");
                var vendor = Path.Combine(pluginDir, "NestingPrototype.vendor.exe");
                var targetFile = Path.Combine(pluginDir, "sheetnest-target.txt");

                if (!File.Exists(vendor))
                {
                    log.AppendLine("no backup to restore");
                    WriteLog(log);
                    return Failed;
                }

                File.Copy(vendor, target, true);
                File.Delete(vendor);
                if (File.Exists(targetFile))
                {
                    File.Delete(targetFile);
                }

                log.AppendLine("restored factory nester and removed shim files");
                WriteLog(log);
                return Ok;
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION: " + ex);
                WriteLog(log);
                return Failed;
            }
        }

        private static void WriteLog(System.Text.StringBuilder log)
        {
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "sheetnest-integrate.log"), log.ToString());
            }
            catch
            {
                // best effort
            }
        }

        /// <summary>Reads the first non-empty line of the target file, or null.</summary>
        private static string ReadTarget(string file)
        {
            try
            {
                if (!File.Exists(file))
                {
                    return null;
                }

                foreach (var line in File.ReadAllLines(file))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0)
                    {
                        return trimmed;
                    }
                }
            }
            catch
            {
                // treated as "no target"
            }

            return null;
        }

        /// <summary>Hands the job back to SheetCam's own nester, backed up beside this exe.</summary>
        private static int RunFactoryNester(string here, string[] args)
        {
            var vendor = Path.Combine(here, "NestingPrototype.vendor.exe");
            if (!File.Exists(vendor))
            {
                return 0; // nothing to fall back to; exit quietly so SheetCam just reloads unchanged
            }

            try
            {
                var psi = new ProcessStartInfo(vendor) { UseShellExecute = false };
                if (args.Length > 0)
                {
                    psi.Arguments = "\"" + args[0] + "\"";
                }

                using (var proc = Process.Start(psi))
                {
                    proc.WaitForExit();
                    return proc.ExitCode;
                }
            }
            catch
            {
                return 0;
            }
        }
    }
}
