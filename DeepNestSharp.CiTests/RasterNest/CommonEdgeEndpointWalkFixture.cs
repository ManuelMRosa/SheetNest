namespace DeepNestSharp.CiTests.RasterNest
{
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  public class CommonEdgeEndpointWalkFixture
  {
    private const double CommonGap = 0.01;
    private const double NormalGap = 0.30;
    private const double Tolerance = 0.0001;

    [Fact]
    public void EndpointExceptionWalksEveryChordUntilRoundedBranchesRecoverNormalGap()
    {
      IPartPlacement a = Place(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(9.98, 10.20),
        new SvgPoint(9.92, 10.40),
        new SvgPoint(9.82, 10.60),
        new SvgPoint(9.65, 10.80),
        new SvgPoint(9.40, 10.95),
        new SvgPoint(9, 11),
        new SvgPoint(0, 11),
      }, 0);
      IPartPlacement b = Place(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 11),
        new SvgPoint(1, 11),
        new SvgPoint(0.60, 10.95),
        new SvgPoint(0.35, 10.80),
        new SvgPoint(0.18, 10.60),
        new SvgPoint(0.08, 10.40),
        new SvgPoint(0.02, 10.20),
        new SvgPoint(0, 10),
      }, 10 + CommonGap);

      CommonEdgeClearance.TooClose(
        a,
        b,
        NormalGap,
        PlacementCollision.PlacementNoise,
        new[] { new CommonEdgeClearance.EdgeReference(a, 1, b, 9) },
        CommonGap,
        Tolerance).Should().BeFalse(
          "the connected endpoint cap extends across several tessellation chords");
    }

    [Fact]
    public void LaterLobeIsNotIncludedAfterTheEndpointBranchesRecoverNormalGap()
    {
      IPartPlacement a = Place(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(9.95, 10),
        new SvgPoint(9.85, 10),
        new SvgPoint(9.60, 10),
        new SvgPoint(8, 10),
        new SvgPoint(8, 12),
        new SvgPoint(9.90, 12),
        new SvgPoint(9.90, 14),
        new SvgPoint(0, 14),
      }, 0);
      IPartPlacement b = Place(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 14),
        new SvgPoint(0.10, 14),
        new SvgPoint(0.10, 12),
        new SvgPoint(2, 12),
        new SvgPoint(2, 10),
        new SvgPoint(0.40, 10),
        new SvgPoint(0.15, 10),
        new SvgPoint(0.05, 10),
        new SvgPoint(0, 10),
      }, 10 + CommonGap);

      CommonEdgeClearance.TooClose(
        a,
        b,
        NormalGap,
        PlacementCollision.PlacementNoise,
        new[] { new CommonEdgeClearance.EdgeReference(a, 1, b, 10) },
        CommonGap,
        Tolerance).Should().BeTrue(
          "the separate 0.21-gap lobe is not connected to the intentional endpoint cap");
    }

    [Fact]
    public void LongBranchesThatNeverRecoverNormalGapAreRejectedOutsideTheJunctionCap()
    {
      IPartPlacement a = Place(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(9.90, 20),
        new SvgPoint(0, 20),
      }, 0);
      IPartPlacement b = Place(new[]
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 20),
        new SvgPoint(0.10, 20),
        new SvgPoint(0, 10),
      }, 10 + CommonGap);

      CommonEdgeClearance.TooClose(
        a,
        b,
        NormalGap,
        PlacementCollision.PlacementNoise,
        new[] { new CommonEdgeClearance.EdgeReference(a, 1, b, 4) },
        CommonGap,
        Tolerance).Should().BeTrue(
          "only a physically local endpoint cap may use less than the normal part gap");
    }

    private static IPartPlacement Place(SvgPoint[] points, double x)
      => new PartPlacement(new NoFitPolygon(points))
      {
        Id = x > 0 ? 2 : 1,
        Source = x > 0 ? 1 : 0,
        X = x,
        Y = 1,
      };
  }
}
