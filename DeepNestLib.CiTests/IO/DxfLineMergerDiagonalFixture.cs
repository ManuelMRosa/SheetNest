namespace DeepNestLib.CiTests.IO
{
  using System.Linq;
  using DeepNestLib.IO;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// Common-line triangle pairs share their DIAGONAL hypotenuse — the merger must fuse coincident
  /// diagonal lines exactly like vertical/horizontal ones (one cut instead of two).
  /// </summary>
  public class DxfLineMergerDiagonalFixture
  {
    [Fact]
    public void MergesCoincidentDiagonals()
    {
      // The same hypotenuse drawn by two touching triangle contours (opposite directions).
      var a = new DxfLine(new DxfPoint(2.25, 5.0, 0), new DxfPoint(9.84, 15.32, 0));
      var b = new DxfLine(new DxfPoint(9.84, 15.32, 0), new DxfPoint(2.25, 5.0, 0));

      var merged = new DxfLineMerger().MergeLines(new DxfEntity[] { a, b }).ToList();

      merged.Should().HaveCount(1, "a shared diagonal edge must export as ONE cut line");
    }

    [Fact]
    public void MergesCoincidentDiagonalsWithFloatNoise()
    {
      // Same edge from two independently transformed placements: endpoints differ by ~1e-9.
      var a = new DxfLine(new DxfPoint(2.25, 5.0, 0), new DxfPoint(9.84, 15.32, 0));
      var b = new DxfLine(new DxfPoint(9.840000001, 15.320000001, 0), new DxfPoint(2.250000001, 5.000000001, 0));

      var merged = new DxfLineMerger().MergeLines(new DxfEntity[] { a, b }).ToList();

      merged.Should().HaveCount(1);
    }

    [Fact]
    public void MergesOverlappingCollinearDiagonals()
    {
      // Partial overlap on the same infinite line: one longer cut.
      var a = new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(6, 8, 0));
      var b = new DxfLine(new DxfPoint(3, 4, 0), new DxfPoint(9, 12, 0));

      var merged = new DxfLineMerger().MergeLines(new DxfEntity[] { a, b }).ToList();

      merged.Should().HaveCount(1);
    }

    [Fact]
    public void DoesNotMergeParallelButOffsetDiagonals()
    {
      var a = new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(6, 8, 0));
      var b = new DxfLine(new DxfPoint(1, 0, 0), new DxfPoint(7, 8, 0)); // parallel, 1" to the right

      var merged = new DxfLineMerger().MergeLines(new DxfEntity[] { a, b }).ToList();

      merged.Should().HaveCount(2, "distinct parallel cuts must both survive");
    }
  }
}
