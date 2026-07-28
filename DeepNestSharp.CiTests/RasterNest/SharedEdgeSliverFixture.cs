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
    private const double Kerf = 0.006; // an ordinary laser kerf, the width the cut takes out

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

      PlacementCollision.TooClose(a, b, 0, Kerf).Should().BeFalse();
    }

    /// <summary>The other side of it, and the reason the fix is a depth and not a bigger area: a bite a
    /// thousandth of an inch deep along that same long edge IS parts on top of each other, and it must
    /// still be caught however little area it adds up to.</summary>
    [Fact]
    public void ARealBiteAlongTheSameLongEdgeIsStillCaught()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.01, 0);

      PlacementCollision.TooClose(a, b, 0, Kerf).Should().BeTrue();
    }

    /// <summary>And a plain overlap of two parts, which is what the check is for.</summary>
    [Fact]
    public void PartsProperlyOnTopOfEachOtherAreCaught()
    {
      var a = Bar(0, 0);
      var b = Bar(1, 1);

      PlacementCollision.TooClose(a, b, 0, Kerf).Should().BeTrue();
    }

    /// <summary>Touching exactly is what common line asks for, so it cannot be a fault either.</summary>
    [Fact]
    public void EdgesExactlyTouchingAreNotAnOverlap()
    {
      var a = Bar(0, 0);
      var b = Bar(Width, 0);

      PlacementCollision.TooClose(a, b, 0, Kerf).Should().BeFalse();
    }

    /// <summary>A small square bite is not a sliver: it is 0.02 wide and 0.02 deep, so depth catches it
    /// where "it is only 0.0004 sq in" would have waved it through.</summary>
    [Fact]
    public void ASmallSquareBiteIsNotASliver()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.02, Length - 0.02);

      PlacementCollision.TooClose(a, b, 0, Kerf).Should().BeTrue();
    }

    /// <summary>Asking for clearance still works: apart by less than the gap the job wants is too close,
    /// and the sliver tolerance is far too small to blur a real clearance.</summary>
    [Fact]
    public void AskedForClearanceIsStillEnforced()
    {
      var a = Bar(0, 0);
      var b = Bar(Width + 0.1, 0);

      PlacementCollision.TooClose(a, b, 0.25, Kerf).Should().BeTrue("0.1 apart when 0.25 was asked for");
      PlacementCollision.TooClose(a, b, 0.05, Kerf).Should().BeFalse("0.1 apart is clear when 0.05 was asked for");
    }

    /// <summary>
    /// How much bite the cut itself takes away. A common-line pair shares ONE cut, and that cut removes a
    /// kerf of material, so parts overlapping by less than the kerf come off the machine exactly as drawn:
    /// the overlap was never there to begin with, it was in the slot.
    /// <para>Reported from the shop floor as parts turning red when two radii met. What was really happening
    /// is below.</para>
    /// </summary>
    [Fact]
    public void AnOverlapTheCutWillRemoveIsNotAnOverlap()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.003, 0);

      PlacementCollision.TooClose(a, b, 0, Kerf).Should().BeFalse("three thou of a six thou kerf");
    }

    /// <summary>
    /// The measured case, pinned. Two parts placed by hand ended up a THOUSANDTH of an inch into each other
    /// along a 36in shared edge, and the threshold at the time was a thousandth of an inch: the same nest
    /// came out red or clean on the last decimal. A limit that sits exactly where real work lands decides
    /// nothing.
    /// </summary>
    [Fact]
    public void TheThousandthOfAnInchFromTheShopFloorIsNotAnOverlap()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.001, 0);

      PlacementCollision.TooClose(a, b, 0, Kerf).Should().BeFalse();
    }

    /// <summary>And what keeps it honest: past the kerf the cut cannot save it, so the parts really would
    /// eat into each other.</summary>
    [Fact]
    public void PastTheKerfItIsMaterialAgain()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.02, 0);

      PlacementCollision.TooClose(a, b, 0, Kerf).Should().BeTrue("twenty thou is three kerfs deep");
    }

    /// <summary>With a finer cut the same overlap is no longer forgiven: the tolerance follows the kerf
    /// rather than being a number of its own. The pair is what proves the kerf is really being used.</summary>
    [Fact]
    public void AFinerCutForgivesLess()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.004, 0);

      PlacementCollision.TooClose(a, b, 0, 0.006).Should().BeFalse("inside a six thou kerf");
      PlacementCollision.TooClose(a, b, 0, 0.002).Should().BeTrue("but not inside a two thou one");
    }

    /// <summary>
    /// On a plain DXF nobody has said how wide the cut is, so a figure stands in for one. The metric one is
    /// the imperial one converted and rounded for a metric drawing, NOT the same number reused: that is the
    /// mistake this project has already made, with a nudge step that moved a metric part a quarter of a
    /// millimetre. The second assertion is the guard that would have caught it.
    /// </summary>
    [Fact]
    public void TheFallbackIsTheSameDistanceInBothSystems()
    {
      double inches = PlacementCollision.DefaultSliver(unitsMm: false);
      double mm = PlacementCollision.DefaultSliver(unitsMm: true);

      (inches * 25.4).Should().BeApproximately(mm, 0.02);
      mm.Should().BeGreaterThan(10 * inches, "a metric job must not inherit the imperial number");
    }

    /// <summary>And it stands just under an ordinary kerf: forgiving enough to absorb placing by hand, far
    /// too small to wave through material the cut will not take away.</summary>
    [Fact]
    public void TheFallbackSitsJustUnderAKerf()
    {
      PlacementCollision.DefaultSliver(unitsMm: false).Should().BeLessThan(Kerf);
      PlacementCollision.DefaultSliver(unitsMm: false).Should().BeGreaterThan(0.001, "a thousandth is where hand placement lands");
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
