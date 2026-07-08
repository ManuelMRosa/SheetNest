namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text;

  /// <summary>
  /// Raised when a 3D model cannot be unfolded to a flat pattern. The message is user-facing.
  /// </summary>
  public class StepUnfoldException : Exception
  {
    public StepUnfoldException(string message)
        : base(message)
    {
    }

    public StepUnfoldException(string message, Exception inner)
        : base(message, inner)
    {
    }
  }

  /// <summary>
  /// Turns a 3D sheet-metal model (STEP/IGES) into a flat 2D DXF by driving a bundled, headless
  /// FreeCAD + SheetMetal workbench (see assets/freecad/unfold_macro.py), then feeds that DXF into
  /// the existing DXF import path. This keeps the nesting engine strictly 2D — a STEP part becomes
  /// an <see cref="IRawDetail"/> exactly like a native DXF.
  /// </summary>
  public static class StepUnfoldService
  {
    private static readonly string[] StepExtensions = { ".step", ".stp", ".iges", ".igs" };

    /// <summary>Open-file-dialog filter listing only the 3D formats (STEP/IGES).</summary>
    public const string FileDialogFilter3D =
      "STEP / IGES 3D (*.step;*.stp;*.iges;*.igs)|*.step;*.stp;*.iges;*.igs|All files (*.*)|*.*";

    /// <summary>K-factor used for the bend allowance (developed length). Persisted by the host.</summary>
    public static double KFactor { get; set; } = 0.40;

    /// <summary>K-factor standard: "ansi" or "din". Persisted by the host.</summary>
    public static string KFactorStandard { get; set; } = "ansi";

    /// <summary>Optional override to a freecadcmd.exe (debug / dev); empty = use the bundled copy.</summary>
    public static string FreeCadCmdPathOverride { get; set; } = string.Empty;

    /// <summary>Max time to wait for FreeCAD to unfold a single part.</summary>
    public static int TimeoutMs { get; set; } = 120000;

    /// <summary>
    /// If true, the unfolded flat is converted from millimeters (STEP/IGES native) to inches so it
    /// matches inch drawings/sheets — the nest engine is inch-based. Uncheck when nesting in mm.
    /// </summary>
    public static bool UnfoldUnitInch { get; set; } = true;

    /// <summary>Scale applied to the mm flat before export: 1/25.4 for inches, 1 for millimeters.</summary>
    private static double ScaleFor(bool unitInch) => unitInch ? 1.0 / 25.4 : 1.0;

    // Cache of unfolded flat DXFs so FreeCAD runs once per (file + params). A single STEP can yield
    // MANY flats (an assembly = one part per solid); reused by the geometry load and the preview.
    private static readonly object CacheLock = new object();
    private static readonly Dictionary<string, (string Key, List<string> Paths, int Total)> PartsCache =
      new Dictionary<string, (string, List<string>, int)>(StringComparer.OrdinalIgnoreCase);

    public static bool IsStepFile(string path)
      => !string.IsNullOrEmpty(path) && StepExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Unfolds EVERY sheet-metal solid in <paramref name="stepPath"/> to flat DXF files (an assembly
    /// yields one per part) and returns their paths. Runs FreeCAD only once per (file + K-factor + unit);
    /// cached for the session. Throws <see cref="StepUnfoldException"/> (user-facing) if nothing unfolds.
    /// </summary>
    public static (IReadOnlyList<string> Paths, int Skipped) GetUnfoldedParts(string stepPath)
      => GetUnfoldedParts(stepPath, KFactor, KFactorStandard, UnfoldUnitInch);

    /// <summary>
    /// Explicit-params overload used for PER-PART re-unfolds (e.g. the user changed a part's K-factor):
    /// does NOT read/mutate the process-global statics, so it is safe to call off the UI thread and from
    /// concurrent loads. Each distinct (file, K, standard, unit) is cached in its own temp dir, so
    /// re-unfolding one part never clobbers another's flats.
    /// </summary>
    public static (IReadOnlyList<string> Paths, int Skipped) GetUnfoldedParts(
      string stepPath, double kFactor, string kFactorStandard, bool unitInch)
    {
      double scale = ScaleFor(unitInch);
      var key = CacheKey(stepPath, kFactor, kFactorStandard, scale);
      var cacheKey = stepPath + "|" + key;
      lock (CacheLock)
      {
        if (PartsCache.TryGetValue(cacheKey, out var entry) && entry.Key == key && entry.Paths.All(File.Exists))
        {
          return (entry.Paths, System.Math.Max(0, entry.Total - entry.Paths.Count));
        }

        var dir = CacheDir(stepPath, key);
        try
        {
          if (Directory.Exists(dir))
          {
            Directory.Delete(dir, true); // stale file (mtime changed) — drop the old flats
          }
        }
        catch
        {
          // ignore
        }

        Directory.CreateDirectory(dir);
        var dstBase = Path.Combine(dir, Path.GetFileNameWithoutExtension(stepPath) + ".dxf");
        var (paths, total) = RunUnfold(stepPath, dstBase, kFactor, kFactorStandard, scale);

        // FreeCAD exports exploded LINE/ARC soup; join each flat into closed LWPOLYLINEs so the
        // nested DXF the laser receives has real closed contours. A join failure keeps the
        // exploded (still geometrically closed) file rather than failing the import.
        foreach (var p in paths)
        {
          try
          {
            FlatDxfJoiner.JoinToClosedPolylines(p);
          }
          catch
          {
            // keep the exploded flat
          }
        }

        PartsCache[cacheKey] = (key, paths, total);
        return (paths, System.Math.Max(0, total - paths.Count));
      }
    }

    /// <summary>The first (or only) unfolded flat as an <see cref="IRawDetail"/> — for opening a .step directly.</summary>
    public static IRawDetail LoadAsRawDetail(string stepPath)
    {
      var result = GetUnfoldedParts(stepPath);
      return DxfParser.LoadDxfFile(result.Paths[0]).Result;
    }

    /// <summary>True (with the first cached flat DXF) if the part was already unfolded; never runs FreeCAD.</summary>
    public static bool TryGetCachedDxf(string stepPath, out string dxfPath)
    {
      double scale = ScaleFor(UnfoldUnitInch);
      var key = CacheKey(stepPath, KFactor, KFactorStandard, scale);
      var cacheKey = stepPath + "|" + key;
      lock (CacheLock)
      {
        if (PartsCache.TryGetValue(cacheKey, out var entry) && entry.Key == key
            && entry.Paths.Count > 0 && File.Exists(entry.Paths[0]))
        {
          dxfPath = entry.Paths[0];
          return true;
        }
      }

      dxfPath = null;
      return false;
    }

    // Stable per-(file+params) temp dir. NOTE: string.GetHashCode is randomized per process in .NET 6, so
    // it cannot be used here — a SHA256 of the (path + cache key) keeps the dir stable across runs AND
    // distinct per K-factor, letting different-K flats of the same file coexist.
    private static string CacheDir(string stepPath, string key)
    {
      var bytes = Encoding.UTF8.GetBytes(stepPath + "|" + key);
      var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).Substring(0, 16);
      return Path.Combine(Path.GetTempPath(), "sheetnest_unfold", hash);
    }

    private static string CacheKey(string stepPath, double kFactor, string kFactorStandard, double scale)
    {
      long ticks = 0;
      try
      {
        ticks = File.GetLastWriteTimeUtc(stepPath).Ticks;
      }
      catch
      {
        // missing file -> ticks 0; the unfold will surface the real error
      }

      // "joined1" versions the cache format: bumping it regenerates stale exploded-entity flats
      // produced before FlatDxfJoiner existed.
      return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|joined1", ticks, kFactor,
        string.IsNullOrWhiteSpace(kFactorStandard) ? "ansi" : kFactorStandard, scale);
    }

    /// <summary>
    /// Runs the bundled FreeCAD headlessly against the unfold macro in the given mode ("unfold" or
    /// "probe") and returns the single RESULT line the macro wrote (via a sidecar file).
    /// </summary>
    private static string RunMacro(string stepPath, string dstBase, string mode, double kFactor, string kFactorStandard, double scale)
    {
      var freecad = ResolveFreeCadCmd();
      var macro = ResolveMacro();

      var psi = new ProcessStartInfo
      {
        FileName = freecad,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
      };
      psi.ArgumentList.Add(macro);
      psi.ArgumentList.Add(stepPath);
      psi.ArgumentList.Add(dstBase);
      psi.ArgumentList.Add(kFactor.ToString(CultureInfo.InvariantCulture));
      psi.ArgumentList.Add(string.IsNullOrWhiteSpace(kFactorStandard) ? "ansi" : kFactorStandard);
      psi.ArgumentList.Add(scale.ToString(CultureInfo.InvariantCulture));
      psi.ArgumentList.Add(mode);

      var stdout = new StringBuilder();
      var stderr = new StringBuilder();
      using (var proc = new Process { StartInfo = psi })
      {
        proc.OutputDataReceived += (s, e) => { if (e.Data != null) { stdout.AppendLine(e.Data); } };
        proc.ErrorDataReceived += (s, e) => { if (e.Data != null) { stderr.AppendLine(e.Data); } };

        try
        {
          proc.Start();
        }
        catch (Exception ex)
        {
          throw new StepUnfoldException($"Could not start the unfold engine (FreeCAD):\n{freecad}\n\n{ex.Message}", ex);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit(TimeoutMs))
        {
          try
          {
            proc.Kill(true);
          }
          catch
          {
            // ignore
          }

          throw new StepUnfoldException($"FreeCAD timed out ({TimeoutMs / 1000} s).");
        }

        proc.WaitForExit(); // ensure async output buffers are flushed
      }

      // FreeCAD does not send its stdout through .NET's redirected pipe, so the macro also writes the
      // result to a sidecar file next to the base path. Prefer that; fall back to stdout.
      var resultFile = dstBase + ".result";
      string line = null;
      try
      {
        if (File.Exists(resultFile))
        {
          line = File.ReadAllText(resultFile).Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Contains("_RESULT="));
        }
      }
      catch
      {
        // fall through to stdout
      }
      finally
      {
        TryDelete(resultFile);
      }

      if (line == null)
      {
        line = stdout.ToString().Split('\n').Select(l => l.Trim())
          .FirstOrDefault(l => l.Contains("_RESULT="));
      }

      if (line == null)
      {
        throw new StepUnfoldException("FreeCAD returned no result.\n\n" + Tail(stderr.ToString()));
      }

      return line;
    }

    /// <summary>Unfolds all solids. Returns (flat DXF paths, total solids in the file).</summary>
    private static (List<string> Paths, int Total) RunUnfold(string stepPath, string dstBase, double kFactor, string kFactorStandard, double scale)
    {
      // UNFOLD_RESULT=OK:<produced>:<total>:<p1>|<p2>|...   |   UNFOLD_RESULT=FAIL:<code>:<msg>
      var line = RunMacro(stepPath, dstBase, "unfold", kFactor, kFactorStandard, scale);
      var payload = line.Substring(line.IndexOf('=') + 1);
      if (payload.StartsWith("OK:", StringComparison.Ordinal))
      {
        var seg = payload.Split(new[] { ':' }, 4); // OK, produced, total, "<p1>|..." (drive colons kept)
        if (seg.Length >= 4)
        {
          int.TryParse(seg[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total);
          var paths = seg[3].Split('|').Select(p => p.Trim()).Where(p => p.Length > 0 && File.Exists(p)).ToList();
          if (paths.Count == 0)
          {
            throw new StepUnfoldException("The unfold reported success but produced no flat pattern.");
          }

          return (paths, total < paths.Count ? paths.Count : total);
        }
      }

      var fail = payload.Split(new[] { ':' }, 3);
      var reason = fail.Length >= 3 ? fail[2] : payload;
      throw new StepUnfoldException("Could not unfold the 3D part.\n\n" + reason);
    }

    /// <summary>Analyzes a 3D file WITHOUT unfolding: returns the solid count and per-solid thickness (mm, 0 if unknown).</summary>
    public static (int SolidCount, double[] ThicknessMm) ProbeThickness(string stepPath)
    {
      double scale = ScaleFor(UnfoldUnitInch);
      var key = CacheKey(stepPath, KFactor, KFactorStandard, scale);
      var dir = CacheDir(stepPath, key);
      Directory.CreateDirectory(dir);
      var dstBase = Path.Combine(dir, "probe.dxf");

      // PROBE_RESULT=OK:<count>:<t1>|<t2>|...
      var line = RunMacro(stepPath, dstBase, "probe", KFactor, KFactorStandard, scale);
      var payload = line.Substring(line.IndexOf('=') + 1);
      if (!payload.StartsWith("OK:", StringComparison.Ordinal))
      {
        var seg0 = payload.Split(new[] { ':' }, 3);
        throw new StepUnfoldException("Could not analyze the 3D model.\n\n" + (seg0.Length >= 3 ? seg0[2] : payload));
      }

      var seg = payload.Split(new[] { ':' }, 3); // OK, count, "t1|t2|..."
      int.TryParse(seg.Length > 1 ? seg[1] : "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int count);
      var ths = seg.Length > 2
        ? seg[2].Split('|').Select(s =>
          {
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double t);
            return t;
          }).ToArray()
        : System.Array.Empty<double>();
      return (count, ths);
    }

    private static string detectedFreeCad; // cached auto-detection result

    private static string ResolveFreeCadCmd()
    {
      if (!string.IsNullOrWhiteSpace(FreeCadCmdPathOverride) && File.Exists(FreeCadCmdPathOverride))
      {
        return FreeCadCmdPathOverride;
      }

      var bundled = Path.Combine(AppContext.BaseDirectory, "freecad", "bin", "freecadcmd.exe");
      if (File.Exists(bundled))
      {
        return bundled;
      }

      // Fallback: a FreeCAD installed on this machine (dev/Debug builds have no bundled copy).
      if (!string.IsNullOrEmpty(detectedFreeCad) && File.Exists(detectedFreeCad))
      {
        return detectedFreeCad;
      }

      detectedFreeCad = DetectInstalledFreeCad();
      if (!string.IsNullOrEmpty(detectedFreeCad))
      {
        return detectedFreeCad;
      }

      throw new StepUnfoldException(
        "Unfold engine (FreeCAD) not found.\n" +
        "Reinstall SheetNest (it bundles FreeCAD) or install FreeCAD from freecad.org.");
    }

    private static string DetectInstalledFreeCad()
    {
      var roots = new[]
      {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
      };

      foreach (var root in roots)
      {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
          continue;
        }

        try
        {
          foreach (var dir in Directory.GetDirectories(root, "FreeCAD*"))
          {
            var exe = Path.Combine(dir, "bin", "freecadcmd.exe");
            if (File.Exists(exe))
            {
              return exe;
            }
          }
        }
        catch
        {
          // permission / IO — skip this root
        }
      }

      return null;
    }

    private static string ResolveMacro()
    {
      var macro = Path.Combine(AppContext.BaseDirectory, "unfold_macro.py");
      if (File.Exists(macro))
      {
        return macro;
      }

      var alt = Path.Combine(AppContext.BaseDirectory, "freecad", "Mod", "SheetMetal", "unfold_macro.py");
      if (File.Exists(alt))
      {
        return alt;
      }

      throw new StepUnfoldException("unfold_macro.py not found next to SheetNest.");
    }

    private static void TryDelete(string path)
    {
      try
      {
        if (File.Exists(path))
        {
          File.Delete(path);
        }
      }
      catch
      {
        // ignore
      }
    }

    private static string Tail(string s, int max = 500)
      => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(s.Length - max));
  }
}
