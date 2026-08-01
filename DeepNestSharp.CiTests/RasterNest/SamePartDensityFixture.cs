namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// What Same part actually costs. The engine takes ONE polygon per part and cannot be told "tight
  /// against your own kind, spaced from the others", so a job whose parts cannot all share with each other
  /// is handed over already spaced and only compaction can win the seams back. This measures whether that
  /// is a real loss or a rounding error, on the same parts, same quantities, same sheet.
  /// </summary>
  public class SamePartDensityFixture
  {
    private readonly ITestOutputHelper output;

    public SamePartDensityFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    /// <summary>
    /// One drawing in Same part mode is the common case in a shop (a sheet of one bracket), and it must
    /// not cost anything: every part may share with every other, so the tight footprint still applies.
    /// </summary>
    [Fact]
    public void OneDrawingInSamePartModePacksAsTightlyAsUnrestricted()
    {
      string exe = Sparrow();
      if (exe == null)
      {
        this.output.WriteLine("DENSITY: SPARROW_EXE not set — skipping.");
        return;
      }

      int unrestricted = PlacedOnOneSheet(exe, 1, CommonCuttingMode.Unrestricted);
      int samePart = PlacedOnOneSheet(exe, 1, CommonCuttingMode.SamePart);

      this.output.WriteLine($"DENSITY 1 drawing: unrestricted={unrestricted} samePart={samePart}");
      samePart.Should().Be(unrestricted, "with a single drawing every part shares with every other either way");
    }

    /// <summary>
    /// MEASUREMENT, not a promise. Two drawings in Same part mode cannot all share, so each is handed to
    /// the engine carrying its full spacing. This prints what that costs against the two bounds: everything
    /// sharing (Unrestricted) and nothing sharing (None). The assertion is only that it lands between them,
    /// which is the honest claim; the NUMBER is the point, and it is what decides whether a batching pass
    /// is worth building.
    /// </summary>
    [Fact]
    public void MeasureWhatSamePartCostsWithTwoDrawings()
    {
      string exe = Sparrow();
      if (exe == null)
      {
        this.output.WriteLine("DENSITY: SPARROW_EXE not set — skipping.");
        return;
      }

      int unrestricted = PlacedOnOneSheet(exe, 2, CommonCuttingMode.Unrestricted);
      int samePart = PlacedOnOneSheet(exe, 2, CommonCuttingMode.SamePart);
      int none = PlacedOnOneSheet(exe, 2, CommonCuttingMode.None);

      this.output.WriteLine($"DENSITY 2 drawings: unrestricted={unrestricted} samePart={samePart} none={none}");
      this.output.WriteLine(FormattableString.Invariant(
        $"DENSITY 2 drawings: samePart recovers {(unrestricted == none ? 1.0 : (samePart - none) / (double)(unrestricted - none)):P0} of the common-cut gain"));

      samePart.Should().BeGreaterOrEqualTo(none, "sharing between copies of the same drawing cannot pack worse than sharing with nobody");
      samePart.Should().BeLessOrEqualTo(unrestricted, "and it cannot beat sharing with everybody");
    }

    private static string Sparrow()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      return !string.IsNullOrWhiteSpace(exe) && File.Exists(exe) ? exe : null;
    }

    /// <summary>Fills ONE sheet from a demand nobody can satisfy, so the count IS the density.</summary>
    private static int PlacedOnOneSheet(string exe, int drawings, CommonCuttingMode cc)
    {
      string dxfDir = FindDxfDir();
      dxfDir.Should().NotBeNull();

      var helper = new NestExecutionHelper();
      double maxExtent = 0;
      var parts = new List<RasterPartInfo>();
      for (int i = 1; i <= drawings; i++)
      {
        string path = Path.Combine(dxfDir, $"_{i}.dxf");
        if (File.Exists(path) && helper.LoadRawDetail(new FileInfo(path)) is { } det
            && det.TryConvertToNfp(0, out INfp nfp) && nfp.Points.Length > 2)
        {
          maxExtent = Math.Max(maxExtent, Math.Max(nfp.MaxX - nfp.MinX, nfp.MaxY - nfp.MinY));
          parts.Add(new RasterPartInfo { Path = path, Quantity = 40, Rotations = 4, Cc = cc });
        }
      }

      parts.Should().HaveCount(drawings);

      // Spacing has to be worth something next to the parts or every mode measures the same. A tenth of the
      // biggest part is the kind of gap that actually changes how many fit.
      double spacing = maxExtent / 10.0;
      foreach (var p in parts)
      {
        p.Spacing = spacing;
      }

      int side = (int)Math.Ceiling(maxExtent * 5);

      var result = SparrowNestService.Nest(parts, new List<(int, int, int)> { (side, side, 1) }, 4, spacing, spacing, 8, exe, out string err);
      result.Should().NotBeNull($"nest must return a result (err={err})");

      return result.UsedSheets.Sum(s => s.PartPlacements.Count);
    }

    private static string FindDxfDir()
    {
      var dir = new DirectoryInfo(AppContext.BaseDirectory);
      while (dir != null)
      {
        string candidate = Path.Combine(dir.FullName, "DeepNestPort", "dxfs");
        if (Directory.Exists(candidate))
        {
          return candidate;
        }

        dir = dir.Parent;
      }

      return null;
    }
  }
}
