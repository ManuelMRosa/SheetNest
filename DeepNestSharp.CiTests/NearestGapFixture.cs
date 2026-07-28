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
  /// The number the editor puts on screen for "how far is this part from its neighbour". It is found by
  /// asking the collision rule "are these closer than d" and bisecting, so the number and the red can never
  /// tell different stories: the same rule decides both.
  /// </summary>
  public class NearestGapFixture
  {
    private const double Cap = 1.0;

    /// <summary>Stands in for PlacementCollision.TooClose over plain rectangles: true when the gap between
    /// the two boxes is under d.</summary>
    private static readonly Func<IPartPlacement, IPartPlacement, double, bool> TooClose = (a, b, d) =>
    {
      var pa = a.PlacedPart;
      var pb = b.PlacedPart;
      double gapX = Math.Max(pa.MinX - pb.MaxX, pb.MinX - pa.MaxX);
      double gapY = Math.Max(pa.MinY - pb.MaxY, pb.MinY - pa.MaxY);
      return Math.Max(gapX, gapY) < d;
    };

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
