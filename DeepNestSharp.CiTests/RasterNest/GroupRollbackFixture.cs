namespace DeepNestSharp.CiTests.RasterNest
{
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// One pair whose snap would drive a cut into a neighbour used to cost EVERY other pair on the sheet
  /// its shared cut, because the whole pass was rolled back together. The nest still came out correct,
  /// just with no common cutting anywhere and nothing saying why.
  /// </summary>
  public class GroupRollbackFixture
  {
    private const double Kerf = 0.3;

    /// <summary>
    /// Two independent pairs plus a stationary blocker.
    /// <para>
    /// A1/A2 are a plain seam over on the left with nothing near them. B1/B2 want the same seam, but B2's
    /// edges start 4 higher than B1's, so closing the seam slides B2 down onto a part parked below it and
    /// its cut would eat into that part. Only the B group may lose its seam.
    /// </para>
    /// </summary>
    [Fact]
    public void OneInvadingGroupDoesNotCostTheOthersTheirSeam()
    {
      var a1 = Bar(1, 1, 2, 10);
      var a2 = Bar(3.35, 1, 2, 10);
      var b1 = Bar(20, 1, 2, 10);
      var b2 = Bar(22.35, 5, 2, 10);      // 4 higher, so the seam drags it down
      var blocker = Bar(22.6, 0, 1.5, 0.95);

      var all = new List<IPartPlacement>
      {
        Place(a1, 0), Place(a2, 1), Place(b1, 2), Place(b2, 3), Place(blocker, 4),
      };

      Snap(all, new[] { a1, a2, b1, b2, blocker });

      (all[1].PlacedPart.MinX - all[0].PlacedPart.MaxX).Should().BeApproximately(Kerf, 1e-6,
        "the pair that was never in trouble keeps its shared cut");
      all[3].Y.Should().Be(0, "the pair whose cut would have eaten the blocker is the only one put back");
    }

    /// <summary>
    /// The control that stops the optimisation opening a hole: the group-by-group retry only tests pairs
    /// involving the parts that moved, so a moved part invading a part that did NOT move still has to be
    /// caught. Here the blocker never moves and B2 runs into it.
    /// </summary>
    [Fact]
    public void AMovedPartInvadingAStationaryOneIsStillCaught()
    {
      var b1 = Bar(20, 1, 2, 10);
      var b2 = Bar(22.35, 5, 2, 10);
      var blocker = Bar(22.6, 0, 1.5, 0.95);

      var all = new List<IPartPlacement> { Place(b1, 0), Place(b2, 1), Place(blocker, 2) };

      Snap(all, new[] { b1, b2, blocker });

      all[1].Y.Should().Be(0, "the snap must not close a seam by cutting into something standing still");
      all[1].X.Should().Be(0);
    }

    private static SvgPoint[] Bar(double x, double y, double w, double h) => new[]
    {
      new SvgPoint(x, y), new SvgPoint(x + w, y), new SvgPoint(x + w, y + h), new SvgPoint(x, y + h),
    };

    private static IPartPlacement Place(SvgPoint[] pts, int source)
      => new PartPlacement(new NoFitPolygon(pts)) { X = 0, Y = 0, Rotation = 0, Source = source };

    private static void Snap(List<IPartPlacement> all, SvgPoint[][] outlines)
    {
      var kerf = new Dictionary<int, double>();
      var tooling = new Dictionary<int, INfp>();
      var cc = new Dictionary<int, (CommonCuttingMode Cc, int ShareKey)>();
      for (int i = 0; i < outlines.Length; i++)
      {
        kerf[i] = Kerf;
        tooling[i] = SparrowNestService.ToolingFootprint(new NoFitPolygon(outlines[i]), null, Kerf, 0);
        cc[i] = (CommonCuttingMode.Unrestricted, i);
      }

      SparrowNestService.SnapCommonLineEdges(all, kerf, tooling, cc);
    }
  }
}
