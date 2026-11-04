namespace DeepNestSharp.CiTests.RasterNest
{
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The clock can stop the compaction before a cluster the common-line planner moved has slid back to the
  /// corner. A nineteen-part job came out that way 2.14 in off the left border, with the gap on the left and
  /// the parts against the right. The sheet is moved to the corner whole, which keeps every seam.
  /// </summary>
  public class PackAnchorFixture
  {
    private const double Margin = 0.25D;
    private const double Gap = 0.010D;
    private const double Tolerance = 0.0001D;

    /// <summary>Two bars sharing a cut, 2.14 in off the left border and on the bottom one: they move left by
    /// that much together, stay on the bottom border, and their shared cut still makes a record.</summary>
    [Fact]
    public void APackOffTheCornerMovesToItWhole()
    {
      var parts = Pair(Margin + 2.14D, Margin);

      SparrowNestService.AnchorToCorner(parts, Margin, Tolerance);

      parts.Min(p => p.PlacedPart.MinX).Should().BeApproximately(Margin, 1e-9);
      parts.Min(p => p.PlacedPart.MinY).Should().BeApproximately(Margin, 1e-9);
      (parts[1].PlacedPart.MinX - parts[0].PlacedPart.MaxX).Should().BeApproximately(Gap, 1e-9, "the seam keeps its gap");
      CommonLineGeometryResolver.TryCreateCommonEdge(parts[0], 1, 0D, 1D, parts[1], 3, 0D, 1D, Gap, Tolerance, out _)
        .Should().BeTrue("the shared cut still lines up");
    }

    /// <summary>Off the corner by less than the tolerance is on it; a pack is never pulled away from the corner.</summary>
    [Theory]
    [InlineData(Margin + 0.00005D, Margin)]
    [InlineData(Margin - 0.1D, Margin + 0.00005D)]
    public void APackOnTheCornerOrPastItStays(double minX, double minY)
    {
      var parts = Pair(minX, minY);

      SparrowNestService.AnchorToCorner(parts, Margin, Tolerance);

      parts.Min(p => p.PlacedPart.MinX).Should().Be(minX);
      parts.Min(p => p.PlacedPart.MinY).Should().Be(minY);
    }

    private static List<IPartPlacement> Pair(double minX, double minY) => new List<IPartPlacement>
    {
      Place(Bar(minX, minY, 9D, 36D), 0),
      Place(Bar(minX + 9D + Gap, minY, 9D, 36D), 1),
    };

    private static SvgPoint[] Bar(double x, double y, double w, double h) => new[]
    {
      new SvgPoint(x, y), new SvgPoint(x + w, y), new SvgPoint(x + w, y + h), new SvgPoint(x, y + h),
    };

    private static IPartPlacement Place(SvgPoint[] pts, int id)
      => new PartPlacement(new NoFitPolygon(pts)) { X = 0, Y = 0, Rotation = 0, Source = 0, Id = id };
  }
}
