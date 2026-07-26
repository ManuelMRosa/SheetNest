namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Seating a part against its neighbours: "cuando está en rojo muy pegado de dos partes es muy difícil o
  /// imposible centrarlo en esas dos partes, digamos con common line".
  /// <para>Everything before this searched ONE axis at a time, and each direction had to reach a fully legal
  /// position on its own — so a part wedged between two neighbours and out by a little in Y matched nothing
  /// at all. These offsets are computed from the neighbours' edges instead of hunted for, which is what makes
  /// a common-line seat reachable: at a clearance of 0 the legal band has no width to sample.</para>
  /// </summary>
  public class FitToNeighboursFixture
  {
    private const double Reach = 0.5;

    /// <summary>A part spanning 0..10 with a neighbour at 12..20: seat it just before that neighbour, or just
    /// beyond it. At clearance 0 the edges land exactly on each other — that IS common line, and the exporter
    /// emits the shared edge as a single cut.</summary>
    [Fact]
    public void AtZeroClearanceTheEdgesLandExactlyOnEachOther()
    {
      var (before, after) = DxfViewer.EdgeAlignOffsets(0, 10, 12, 20, 0);

      before.Should().Be(2, "moving right by 2 puts the part's right edge on the neighbour's left one");
      after.Should().Be(20, "or its left edge on the neighbour's right one");
    }

    /// <summary>And with a real spacing the same two seats stand off by exactly that much, on both sides.</summary>
    [Fact]
    public void AClearanceStandsTheSeatOffByExactlyThatMuch()
    {
      var (before, after) = DxfViewer.EdgeAlignOffsets(0, 10, 12, 20, 0.25);

      before.Should().Be(1.75);
      after.Should().Be(20.25);
    }

    /// <summary>
    /// The reported case: a slot exactly as wide as the part, so ONE offset fits and everything either side
    /// of it overlaps. No sweep would land on it — the legal set has no width — but the neighbour's edge is
    /// plain arithmetic.
    /// </summary>
    [Fact]
    public void ASlotExactlyThePartsWidthIsStillReachable()
    {
      DxfViewer.TryFitToNeighbours(
        (x, y) => Exactly(x, 0.2) && Exactly(y, 0),
        new List<double> { 0, 0.2, -0.3 },
        new List<double> { 0, 0.1 },
        Reach,
        out double ox,
        out double oy).Should().BeTrue();

      ox.Should().Be(0.2);
      oy.Should().Be(0);
    }

    /// <summary>The correction a single axis can never make: the part has to move in X AND in Y before it
    /// fits. This is what left the arrows and the drop with nothing to snap to.</summary>
    [Fact]
    public void ASeatNeedingBothAxesAtOnceIsFound()
    {
      DxfViewer.TryFitToNeighbours(
        (x, y) => Exactly(x, 0.3) && Exactly(y, -0.2),
        new List<double> { 0, 0.3 },
        new List<double> { 0, -0.2 },
        Reach,
        out double ox,
        out double oy).Should().BeTrue();

      ox.Should().Be(0.3);
      oy.Should().Be(-0.2);
    }

    /// <summary>With more than one seat available the part takes the one it has to travel least to reach.</summary>
    [Fact]
    public void TheNearestSeatWins()
    {
      DxfViewer.TryFitToNeighbours(
        (x, y) => (Exactly(x, 0.4) && Exactly(y, 0)) || (Exactly(x, 0) && Exactly(y, 0.1)),
        new List<double> { 0, 0.4 },
        new List<double> { 0, 0.1 },
        Reach,
        out double ox,
        out double oy).Should().BeTrue();

      ox.Should().Be(0);
      oy.Should().Be(0.1);
    }

    /// <summary>A seat that would foul something else is simply not taken, and the part stays where it is —
    /// which is what keeps parking a part on top of its neighbour working.</summary>
    [Fact]
    public void NoLegalSeatLeavesThePartAlone()
    {
      DxfViewer.TryFitToNeighbours(
        (_, _) => false,
        new List<double> { 0, 0.2 },
        new List<double> { 0, 0.3 },
        Reach,
        out double ox,
        out double oy).Should().BeFalse();

      ox.Should().Be(0);
      oy.Should().Be(0);
    }

    /// <summary>A legal seat further off than the magnet reaches is not a seat: the part must not jump across
    /// the sheet to find one.</summary>
    [Fact]
    public void ASeatBeyondReachIsIgnored()
    {
      DxfViewer.TryFitToNeighbours(
        (x, _) => Exactly(x, 3),
        new List<double> { 0, 3 },
        new List<double> { 0 },
        Reach,
        out double ox,
        out double _).Should().BeFalse();

      ox.Should().Be(0);
    }

    /// <summary>Standing still is not a fit. Otherwise a part that is already fine would report a move of
    /// nothing and earn an undo entry for it.</summary>
    [Fact]
    public void StayingPutIsNotAFit()
    {
      DxfViewer.TryFitToNeighbours(
        (_, _) => true,
        new List<double> { 0 },
        new List<double> { 0 },
        Reach,
        out double _,
        out double _).Should().BeFalse();
    }

    /// <summary>The guard both magnets ask first: a part touching its neighbour is settled, and neither may
    /// drag it off to the next nearest thing.</summary>
    [Fact]
    public void APartTouchingItsNeighbourReadsAsSettled()
    {
      DxfViewer.RestsAgainstSomething((x, _) => x <= 0, hair: 1e-4).Should().BeTrue();
    }

    [Fact]
    public void APartInTheOpenIsNotSettled()
    {
      DxfViewer.RestsAgainstSomething((_, _) => true, hair: 1e-4).Should().BeFalse();
    }

    /// <summary>An overlapping part is not "settled" — it is wrong, and the magnets must be free to fix it.</summary>
    [Fact]
    public void AnOverlappingPartIsNotSettled()
    {
      DxfViewer.RestsAgainstSomething((_, _) => false, hair: 1e-4).Should().BeFalse();
    }

    private static bool Exactly(double value, double target) => Math.Abs(value - target) < 1e-9;
  }
}
