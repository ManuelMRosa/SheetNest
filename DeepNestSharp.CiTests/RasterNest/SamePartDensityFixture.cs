namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
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

    /// <summary>
    /// THE TEST THAT WAS MISSING, and the reason a Same part nest reached the operator with red parts on
    /// it while 214 tests were green: there were tests for what the engine packs and tests for what the
    /// editor accepts, and none that said THE TWO AGREE ABOUT THE SAME NEST. The bug lived in that seam.
    /// <para>
    /// So this nests for real and then judges the result with the VIEWER's own predicate, the one that
    /// decides whether a part is painted red and whether Export DXF is refused.
    /// </para>
    /// </summary>
    /// <param name="spacingDivisor">The part gap, as a fraction of the biggest part.</param>
    /// <param name="effortCode">Which <see cref="NestEffort"/> to nest at, as an int because the enum is
    /// internal. The Fast row is not redundant: this guard used to take whatever the default happened to be,
    /// and the defect it now catches showed up at one effort and not the other, purely because the layouts
    /// differ. Left to the default it would have gone quiet the moment the default moved, with nothing
    /// fixed.</param>
    [Theory]
    [InlineData(10, 1)]
    [InlineData(50, 1)]
    [InlineData(10, 0)]
    public void ASamePartNestDoesNotDisagreeWithTheEditorMoreThanAPlainOne(int spacingDivisor, int effortCode)
    {
      string exe = Sparrow();
      if (exe == null)
      {
        this.output.WriteLine("SEAM: SPARROW_EXE not set — skipping.");
        return;
      }

      var effort = (NestEffort)effortCode;
      int none = UnfitByTheEditorsRule(exe, CommonCuttingMode.None, spacingDivisor, effort);
      int samePart = UnfitByTheEditorsRule(exe, CommonCuttingMode.SamePart, spacingDivisor, effort);

      this.output.WriteLine($"SEAM spacing 1/{spacingDivisor} effort {effort}: none={none} samePart={samePart}");

      // This used to compare the two counts, because neither was ever zero: samePart was 9 at 1/10 and 8
      // at 1/50 against a none of 5 and 3, and after the weld/bounds fix 1 and 1 against 5 and 3. A
      // relative guard was all that could be asserted while the engine and the editor were still two
      // different judges.
      //
      // They are one judge now, and the engine is held to it, so the honest assertion is the invariant
      // itself: a nest nobody has touched is never red. That is STRICTER than what stood here, which
      // tolerated five disagreements on a plain job as long as common cutting was no worse.
      none.Should().Be(0, "a fresh nest must never hand the operator a layout the editor calls unfit");
      samePart.Should().Be(0, "and common cutting is no exception");
    }

    /// <summary>
    /// Nests for real, then judges the result with the VIEWER's own predicate — the one that decides
    /// whether a part is painted red and whether Export DXF is refused.
    /// </summary>
    /// <remarks>
    /// ⚠️ The absolute count is NOT zero even for a plain spaced job (3 to 5 pairs on this fixture), so
    /// this cannot assert zero. That residue is a PRE-EXISTING disagreement between what the engine packs
    /// and what the editor accepts, measured on `None` where common cutting plays no part at all. It is a
    /// separate defect and it is not this feature's to fix; what this guards is that common cutting does
    /// not ADD to it.
    /// </remarks>
    private int UnfitByTheEditorsRule(string exe, CommonCuttingMode mode, int spacingDivisor, NestEffort effort)
    {
      var (result, spacing) = NestTwoDrawings(exe, mode, spacingDivisor, effort);

      int unfit = 0;
      foreach (var sheet in result.UsedSheets)
      {
        var pls = sheet.PartPlacements.ToList();
        for (int i = 0; i < pls.Count; i++)
        {
          for (int j = i + 1; j < pls.Count; j++)
          {
            bool mayShare = DeepNestSharp.Ui.UserControls.DxfViewer.MayShare(
              mode, pls[i].Part.Name, pls[i].IsMirrored,
              mode, pls[j].Part.Name, pls[j].IsMirrored);

            double clearance = DeepNestSharp.Ui.UserControls.DxfViewer.ClearanceFor(spacing, spacing, mayShare);

            // No tooling on a plain DXF, so the viewer forgives only its own numeric noise.
            if (PlacementCollision.TooClose(pls[i].PlacedPart, pls[j].PlacedPart, clearance, PlacementCollision.SliverFor(0, 0)))
            {
              unfit++;
            }
          }
        }
      }

      return unfit;
    }

    private static (INestResult Result, double Spacing) NestTwoDrawings(string exe, CommonCuttingMode cc, int spacingDivisor, NestEffort effort)
    {
      string dxfDir = FindDxfDir();
      dxfDir.Should().NotBeNull();

      var helper = new NestExecutionHelper();
      double maxExtent = 0;
      var parts = new List<RasterPartInfo>();
      for (int i = 1; i <= 2; i++)
      {
        string path = Path.Combine(dxfDir, $"_{i}.dxf");
        if (File.Exists(path) && helper.LoadRawDetail(new FileInfo(path)) is { } det
            && det.TryConvertToNfp(0, out INfp nfp) && nfp.Points.Length > 2)
        {
          maxExtent = Math.Max(maxExtent, Math.Max(nfp.MaxX - nfp.MinX, nfp.MaxY - nfp.MinY));
          parts.Add(new RasterPartInfo { Path = path, Quantity = 40, Rotations = 4, Cc = cc });
        }
      }

      parts.Should().HaveCount(2);
      double spacing = maxExtent / spacingDivisor;
      foreach (var p in parts)
      {
        p.Spacing = spacing;
      }

      int side = (int)Math.Ceiling(maxExtent * 5);
      var result = SparrowNestService.Nest(
        parts, new List<(int, int, int)> { (side, side, 1) }, 4, spacing, spacing, 8, exe, out string err, default, null, null, effort);
      result.Should().NotBeNull($"nest must return a result (err={err})");
      return (result, spacing);
    }

    /// <summary>
    /// MEASUREMENT. A common-cut part has its rotations clipped, because the seam finder needs faces it
    /// can pair and free rotation gives it arbitrary angles. Now that seams work at any angle the clip
    /// could be loosened, and this is what says whether that is worth anything.
    /// </summary>
    /// <remarks>
    /// Loosening it can just as easily LOSE material: two copies at 37.1 and 142.9 degrees hardly ever
    /// present parallel faces to each other, so the seams stop happening AND the bias toward tidy
    /// aligned layouts goes with them. Hence a measurement and not a change.
    /// </remarks>
    [Fact]
    public void MeasureWhatRotationCostsACommonCutJob()
    {
      string exe = Sparrow();
      if (exe == null)
      {
        this.output.WriteLine("ROT: SPARROW_EXE not set — skipping.");
        return;
      }

      foreach (int rotations in new[] { 2, 4, 8, 36 })
      {
        int placed = PlacedOnOneSheet(exe, 1, CommonCuttingMode.Unrestricted, rotations);
        this.output.WriteLine($"ROT common-cut, rotations asked for {rotations}: placed {placed}");
      }
    }

    private static string Sparrow()
    {
      string exe = SparrowExe.Resolve();
      return !string.IsNullOrWhiteSpace(exe) && File.Exists(exe) ? exe : null;
    }

    /// <summary>Fills ONE sheet from a demand nobody can satisfy, so the count IS the density.</summary>
    private static int PlacedOnOneSheet(string exe, int drawings, CommonCuttingMode cc, int rotations = 4)
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
          parts.Add(new RasterPartInfo { Path = path, Quantity = 40, Rotations = rotations, Cc = cc });
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

      var result = SparrowNestService.Nest(parts, new List<(int, int, int)> { (side, side, 1) }, rotations, spacing, spacing, 8, exe, out string err);
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
