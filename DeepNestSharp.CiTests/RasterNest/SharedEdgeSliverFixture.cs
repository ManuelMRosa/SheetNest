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
    /// A compact bite is measured by how deep it goes, not by half of it. Judging by 2 x area / perimeter
    /// reads a square of side s as s/2, so a job whose cut is six thou waved through a ten thou bite: twice
    /// the kerf, and the parts really do come off notched. The long-edge cases above are what that formula
    /// gets right, and they must keep working, which is why the two live in one fixture.
    /// </summary>
    [Fact]
    public void ACompactBiteIsNotHalfAsDeepAsItLooks()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.01, Length - 0.01); // a 0.01 x 0.01 corner, ten thou into a six thou job

      PlacementCollision.TooClose(a, b, 0, Kerf).Should().BeTrue("ten thou of material with six thou of cut to take it out");
    }

    /// <summary>
    /// Where the cut is known, that is what it forgives. Where it is not, only the noise in the numbers is:
    /// placements are stored rounded to four decimals, so anything finer than that cannot even survive a
    /// save, while a thousandth of an inch of real overlap has to be reported.
    /// <para>This is the part that used to be wrong. A plain DXF got a stand-in kerf of five thou and the
    /// forgiveness was handed to every pair, whether or not one cut was ever going to run between them. Two
    /// parts nested to touch on a plain drawing could bite four thou into each other and pass, and the
    /// export wrote both outlines in full.</para>
    /// </summary>
    [Fact]
    public void OnlyAKnownCutForgivesAnything()
    {
      PlacementCollision.SliverFor(Kerf, 0).Should().Be(Kerf, "the job said how wide its cut is");
      PlacementCollision.SliverFor(0.002, Kerf).Should().Be(Kerf, "the wider of the pair is the one that runs");
      PlacementCollision.SliverFor(0, 0).Should().Be(PlacementCollision.PlacementNoise);

      PlacementCollision.PlacementNoise.Should().BeLessThan(
        0.001, "a thousandth of an inch of overlap is real material, not a rounding artefact");
    }

    /// <summary>The same tolerance in a metric drawing, because it is a property of how the file stores a
    /// number and not of how big the part is. The old stand-in kerf had to be converted between systems and
    /// that is exactly where this project has slipped before.</summary>
    [Fact]
    public void TheNoiseFloorDoesNotDependOnTheUnits()
    {
      PlacementCollision.PlacementNoise.Should().BeGreaterThan(
        0, "coincident edges land a hair either side of zero and must not be called an overlap");
    }

    /// <summary>What that buys on a plain drawing: four thou into a neighbour used to sit inside the
    /// stand-in kerf and pass in silence. Nothing is going to cut it away, so it is an overlap.</summary>
    [Fact]
    public void OnAPlainDrawingFourThouIsAnOverlap()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.004, 0);

      PlacementCollision.TooClose(a, b, 0, PlacementCollision.SliverFor(0, 0)).Should().BeTrue();
    }

    /// <summary>And the other side of it, which is what the noise floor is for: edges welded coincident by
    /// common line are not an overlap on a plain drawing either.</summary>
    [Fact]
    public void WeldedEdgesStillPassWithoutAKnownCut()
    {
      var a = Bar(0, 0);
      var b = Bar(Width - 0.000001, 0);

      PlacementCollision.TooClose(a, b, 0, PlacementCollision.SliverFor(0, 0)).Should().BeFalse();
    }

    /// <summary>
    /// A cut can only ever forgive MORE than the noise in the numbers, never less. Reported from the
    /// shop: a per-part kerf of 1.38777878078145E-17 mm, which is 2^-56 and pure floating point residue
    /// off the spinner, was taken as a real cut width. Being above zero it replaced the noise floor
    /// instead of raising it, and the editor's tolerance collapsed by fifteen orders of magnitude.
    /// </summary>
    [Fact]
    public void SliverForNeverGoesBelowTheNoiseFloor()
    {
      const double SpinnerResidueMm = 1.38777878078145E-17;
      double residueInches = SpinnerResidueMm / 25.4;

      PlacementCollision.SliverFor(residueInches, 0).Should().Be(
        PlacementCollision.PlacementNoise,
        "a cut finer than the precision positions are stored at cannot make the check stricter");

      PlacementCollision.SliverFor(Kerf, 0).Should().Be(Kerf, "a real cut still forgives its own width");
    }

    /// <summary>
    /// THE PHOTO. Four Same-part copies came up red on a nest nobody had touched, and this is why: the
    /// engine places them TOUCHING on purpose so they can share a cut, so they are the only parts that
    /// depend on the noise floor, and that residue kerf had taken it away.
    /// </summary>
    [Fact]
    public void TouchingSamePartCopiesAreNotAnOverlapBecauseOfSpinnerResidue()
    {
      const double SpinnerResidueMm = 1.38777878078145E-17;
      double residueInches = SpinnerResidueMm / 25.4;

      // Welded to a shared cut, which lands a hair to one side of exact contact rather than on it: a
      // hundred-thousandth, far under the ten-thousandth the positions are even stored to. With the
      // noise floor in place that is nothing; with the residue standing in for it, it is an overlap.
      var a = Bar(0, 0);
      var b = Bar(Width - 0.00001, 0);

      PlacementCollision.TooClose(a, b, 0, PlacementCollision.SliverFor(residueInches, residueInches))
        .Should().BeFalse("nothing moved and nothing real overlaps; only the tolerance changed");
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
