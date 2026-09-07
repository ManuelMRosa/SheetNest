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
  /// evaluates every candidate. So the invariant is that the number shown equals the shared cuts
  /// RECORDED on the sheets that came back, exactly, with nothing from a candidate that lost.
  /// </para>
  /// <para>
  /// This used to be checked against a bound of parts minus one, on the reasoning that seams form a
  /// spanning forest. They do not: the direct planner deliberately records cycles, so a grid of 24
  /// squares legitimately shares 37 edges. That bound also let the test pass while common cutting was
  /// producing nothing at all, which is the worst way for a test to be green. Counting the records is
  /// both stricter and true of the route that actually runs.
  /// </para>
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class SeamCountReflectsTheCutSheetFixture
  {
    private readonly ITestOutputHelper output;

    public SeamCountReflectsTheCutSheetFixture(ITestOutputHelper output) => this.output = output;

    [Fact]
    public void SnappedSeamsCountOnlyTheSharedCutsOnTheSheetsThatCameBack()
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
          CommonCuttingDomain = "direct",
          RequiresMergedLines = true,
        },
      };

      var result = SparrowNestService.Nest(parts, 60, 60, 4, 0.2, 0.2, 15, exe, out string err);
      result.Should().NotBeNull($"nest must return a result (err={err})");

      int placed = result.UsedSheets.Sum(s => s.PartPlacements.Count);
      placed.Should().BeGreaterThan(1, "need at least two placed parts for a seam to be possible at all");

      int recorded = result.UsedSheets.Sum(s => s.CommonEdges?.Count ?? 0);
      int snapped = SparrowNestService.DiagSeamsSnapped;
      this.output.WriteLine($"SEAM: placed={placed} snapped={snapped} recorded={recorded}");

      recorded.Should().BeGreaterThan(
        0,
        "a sheet of one drawing set to share cuts is the job this counter exists to describe; with zero "
        + "shared cuts the count below would agree for the wrong reason");

      snapped.Should().Be(
        recorded,
        "the operator is shown the shared cuts on the sheets that came back, and nothing else — a "
        + "bigger count means the search's own discarded candidates leaked into the total");
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
