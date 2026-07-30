namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The number the editor puts on screen for "how far is this part from its neighbour", found by asking
  /// "are these closer than d" and bisecting.
  /// <para>It asks with NO allowance for the cut, because a distance is a measurement and not a verdict.
  /// The verdict does allow for the cut, since an overlap the kerf removes is not one. Wiring the measure
  /// to the verdict looked like the way to keep the number and the red from telling different stories, and
  /// it made the number wrong instead: bisection converges where the answer flips, so every reading came
  /// back a whole kerf too generous and parts in contact reported the kerf instead of nothing.</para>
  /// <para>This fixture used to hand NearestGap a stand-in predicate with no cut allowance in it, so it
  /// went on passing while the shipped combination was out by a kerf. It uses the real one now, and the
  /// pair of tests at the bottom is what would have caught it.</para>
  /// </summary>
  public class NearestGapFixture
  {
    private const double Cap = 1.0;
    private const double Kerf = 0.006;

    /// <summary>The measuring question: pure distance, no allowance for what the cut takes out.</summary>
    private static readonly Func<IPartPlacement, IPartPlacement, double, bool> TooClose = (a, b, d) =>
      DeepNestSharp.RasterNest.PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, d, 0);

    /// <summary>The judging question, for contrast: the same test with a kerf's worth of forgiveness.</summary>
    private static readonly Func<IPartPlacement, IPartPlacement, double, bool> TooCloseAllowingTheCut = (a, b, d) =>
      DeepNestSharp.RasterNest.PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, d, Kerf);

    [Fact]
    public void ItFindsTheDistanceToTheNeighbour()
    {
      var parts = Parts(Bar(0, 0), Bar(10.4, 0));

      Gap(parts).Should().BeApproximately(0.4, 0.001);
    }

    /// <summary>The NEAREST one, not the first one looked at.</summary>
    [Fact]
    public void TheNearestNeighbourIsTheOneReported()
    {
      var parts = Parts(Bar(0, 0), Bar(10.7, 0), Bar(0, 10.2), Bar(10.5, 10.5));

      Gap(parts).Should().BeApproximately(0.2, 0.001);
    }

    /// <summary>Common line puts parts in contact by design, and that reads as zero rather than as a
    /// number too small to mean anything.</summary>
    [Fact]
    public void PartsInContactReadZero()
    {
      var parts = Parts(Bar(0, 0), Bar(10, 0));

      Gap(parts).Should().Be(0);
    }

    [Fact]
    public void OverlappingPartsReadZero()
    {
      var parts = Parts(Bar(0, 0), Bar(9, 0));

      Gap(parts).Should().Be(0);
    }

    /// <summary>Past the cap there is nothing worth putting a number on, and the cap is what keeps this
    /// cheap on a full sheet: only what is already near enough gets searched.</summary>
    [Fact]
    public void NothingWithinReachReportsTheCap()
    {
      var parts = Parts(Bar(0, 0), Bar(40, 0));

      Gap(parts).Should().Be(Cap);
    }

    [Fact]
    public void APartAloneOnTheSheetReportsTheCap()
    {
      var parts = Parts(Bar(0, 0));

      Gap(parts).Should().Be(Cap);
    }

    /// <summary>A part is not its own neighbour.</summary>
    [Fact]
    public void ThePartItselfIsNotMeasuredAgainst()
    {
      var parts = Parts(Bar(0, 0), Bar(10.4, 0));

      DxfViewer.NearestGap(parts[0], parts, Cap, (a, b, d) =>
      {
        a.Should().NotBeSameAs(b, "measuring a part against itself would always answer zero");
        return TooClose(a, b, d);
      }).Should().BeApproximately(0.4, 0.001);
    }

    /// <summary>
    /// The bug this fixture missed. Asked with the cut allowed for, bisection settles where the verdict
    /// flips, which is a kerf short of contact, so two parts touching report a kerf of gap and every other
    /// reading is a kerf too generous. On a common-line pair, which is welded edge to edge on purpose, the
    /// operator reads six thou of daylight that is not there.
    /// </summary>
    [Fact]
    public void MeasuringMustNotAllowForTheCut()
    {
      var parts = Parts(Bar(0, 0), Bar(10, 0));

      DxfViewer.NearestGap(parts[0], parts, Cap, TooCloseAllowingTheCut)
        .Should().BeApproximately(Kerf, 0.0005, "this is what the wrong question answers");

      DxfViewer.NearestGap(parts[0], parts, Cap, TooClose)
        .Should().Be(0, "and this is the right one");
    }

    /// <summary>The same a kerf out at any distance, not only in contact: it is an offset, not a floor.</summary>
    [Fact]
    public void TheOffsetIsThereAtEveryDistance()
    {
      var parts = Parts(Bar(0, 0), Bar(10.4, 0));

      DxfViewer.NearestGap(parts[0], parts, Cap, TooCloseAllowingTheCut)
        .Should().BeApproximately(0.4 + Kerf, 0.0005);

      DxfViewer.NearestGap(parts[0], parts, Cap, TooClose)
        .Should().BeApproximately(0.4, 0.001);
    }

    private static double Gap(List<IPartPlacement> parts) => DxfViewer.NearestGap(parts[0], parts, Cap, TooClose);

    private static List<IPartPlacement> Parts(params IPartPlacement[] parts) => new List<IPartPlacement>(parts);

    /// <summary>A 10 x 10 square at (x, y).</summary>
    private static IPartPlacement Bar(double x, double y)
    {
      var poly = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(0, 10),
      });

      return new PartPlacement(poly) { X = x, Y = y };
    }
  }
}
