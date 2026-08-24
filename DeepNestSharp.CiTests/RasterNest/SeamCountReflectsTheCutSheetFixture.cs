namespace DeepNestSharp.CiTests.RasterNest
{
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib.NestProject;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// <see cref="SparrowNestService.DiagSeamsSnapped"/> is what <c>MainWindow</c> shows the operator as
  /// "N shared cuts" once a nest finishes. Found on 2026-08-23 measuring a common-line job: it reported
  /// 75 shared cuts on a 26-part sheet, where the true physical maximum for 26 pieces (all of them one
  /// group, a spanning forest) is 25. The counter was accumulating EVERY candidate the search raced and
  /// then discarded — every seed in every wave, every stock size tried for a sheet, both attempts of the
  /// last-chance retry — not just the one layout that was actually kept and would be cut.
  /// <para>
  /// The fix threads the seam count through the same "which candidate did we keep" decisions the code
  /// already makes for placements themselves, instead of mutating the counter from inside the code that
  /// evaluates every candidate. This is the invariant that failed before the fix and holds after it: for
  /// N pieces the search finally KEEPS, snapped seams can never exceed N - 1 (a spanning forest over N
  /// nodes has at most N - 1 edges).
  /// </para>
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class SeamCountReflectsTheCutSheetFixture
  {
    private readonly ITestOutputHelper output;

    public SeamCountReflectsTheCutSheetFixture(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void SnappedSeamsNeverExceedTheSpanningForestBoundOnTheKeptSheet()
    {
      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("SEAM: SPARROW_EXE not set - skipping.");
        return;
      }

      string dxfDir = FindDxfDir();
      string part = dxfDir == null ? null : Path.Combine(dxfDir, "10x10.dxf");
      if (part == null || !File.Exists(part))
      {
        this.output.WriteLine("SEAM: 10x10.dxf not found - skipping.");
        return;
      }

      // Small and cheap on purpose: this only needs several waves of racing to happen (WaveSize is under
      // the app's own tries-per-effort, so any real common-line nest already qualifies), not a big pack.
      var parts = new List<RasterPartInfo>
      {
        new RasterPartInfo
        {
          Path = part,
          Quantity = 24,
          Rotations = 4,
          Spacing = 0.2,
          Cc = CommonCuttingMode.Unrestricted,
          Kerf = 0.2,
        },
      };

      var result = SparrowNestService.Nest(parts, 60, 60, 4, 0.2, 0.2, 15, exe, out string err);
      result.Should().NotBeNull($"nest must return a result (err={err})");

      int placed = result.UsedSheets.Sum(s => s.PartPlacements.Count);
      placed.Should().BeGreaterThan(1, "need at least two placed parts for a seam to be possible at all");

      int snapped = SparrowNestService.DiagSeamsSnapped;
      this.output.WriteLine($"SEAM: placed={placed} snapped={snapped} (bound={placed - 1})");

      snapped.Should().BeLessOrEqualTo(
        placed - 1,
        "snapped seams form a spanning forest over the placed parts, so they can never outnumber the "
        + "parts minus one — a bigger count means the search's own discarded candidates leaked into the "
        + "total instead of only the layout that was actually kept");
    }

    private static string FindDxfDir()
    {
      var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
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
