namespace DeepNestSharp.CiTests.RasterNest
{
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// What counts as two parts sitting on top of each other, when they were MEANT to share an edge.
  /// <para>Common-line cutting welds a pair's edges until they are coincident, and exact coincidence in
  /// floating point always lands a hair to one side of zero or the other. The verdict used to come from an
  /// absolute area (1e-6 sq in): along a 36in shared edge that is a bite 2.8e-8in deep, so the pair whose
  /// residue happened to fall on the crossing side was reported as overlapping. On a real 26-part sheet
  /// exactly one pair out of 54 did, and the export refused a nest nobody had touched. Depth is the
  /// measure that means something here; area is not.</para>
  /// </summary>
  public class SharedEdgeSliverFixture
  {
    private const double Length = 36;
    private const double Width = 7;

    /// <summary>
    /// The bug, reduced to two rectangles. One Clipper unit of crossing (1e-6in) over a 36in edge is
    /// 3.6e-5 sq in, THIRTY SIX TIMES the old area threshold, and it is nothing at all: a thousandth of a
    /// thousandth of the kerf that is about to vaporise it.
    /// </summary>
    [Fact]
    public void EdgesWeldedTogetherAreNotOnTopOfEachOther()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.000001, 0); // crossing by one Clipper unit

      PlacementCollision.TooClose(a, b, 0).Should().BeFalse();
    }

    /// <summary>The other side of it, and the reason the fix is a depth and not a bigger area: a bite a
    /// thousandth of an inch deep along that same long edge IS parts on top of each other, and it must
    /// still be caught however little area it adds up to.</summary>
    [Fact]
    public void ARealBiteAlongTheSameLongEdgeIsStillCaught()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.01, 0);

      PlacementCollision.TooClose(a, b, 0).Should().BeTrue();
    }

    /// <summary>And a plain overlap of two parts, which is what the check is for.</summary>
    [Fact]
    public void PartsProperlyOnTopOfEachOtherAreCaught()
    {
      var a = Bar(0, 0);
      var b = Bar(1, 1);

      PlacementCollision.TooClose(a, b, 0).Should().BeTrue();
    }

    /// <summary>Touching exactly is what common line asks for, so it cannot be a fault either.</summary>
    [Fact]
    public void EdgesExactlyTouchingAreNotAnOverlap()
    {
      var a = Bar(0, 0);
      var b = Bar(Width, 0);

      PlacementCollision.TooClose(a, b, 0).Should().BeFalse();
    }

    /// <summary>A small square bite is not a sliver: it is 0.02 wide and 0.02 deep, so depth catches it
    /// where "it is only 0.0004 sq in" would have waved it through.</summary>
    [Fact]
    public void ASmallSquareBiteIsNotASliver()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.02, Length - 0.02);

      PlacementCollision.TooClose(a, b, 0).Should().BeTrue();
    }

    /// <summary>Asking for clearance still works: apart by less than the gap the job wants is too close,
    /// and the sliver tolerance is far too small to blur a real clearance.</summary>
    [Fact]
    public void AskedForClearanceIsStillEnforced()
    {
      var a = Bar(0, 0);
      var b = Bar(Width + 0.1, 0);

      PlacementCollision.TooClose(a, b, 0.25).Should().BeTrue("0.1 apart when 0.25 was asked for");
      PlacementCollision.TooClose(a, b, 0.05).Should().BeFalse("0.1 apart is clear when 0.05 was asked for");
    }

    private static INfp Bar(double x, double y)
    {
      return new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(x, y),
        new SvgPoint(x + Width, y),
        new SvgPoint(x + Width, y + Length),
        new SvgPoint(x, y + Length),
      });
    }
  }
}
