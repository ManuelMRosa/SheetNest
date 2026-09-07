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
  /// One pair whose seam would drive a cut into a neighbour used to cost EVERY other pair on the sheet
  /// its shared cut, because the whole pass was rolled back together. The nest still came out correct,
  /// just with no common cutting anywhere and nothing saying why.
  /// </summary>
  public class GroupRollbackFixture
  {
    private const double NormalGap = 0.3;
    private const double CommonLineGap = 0.01;

    /// <summary>
    /// Two independent pairs plus a blocker.
    /// <para>
    /// A1/A2 are a plain seam over on the left with nothing near them. B1/B2 want the same seam, but B2's
    /// edges start 4 higher than B1's, so closing the seam slides B2 down onto a part parked below it and
    /// its cut would eat into that part. Only the B group may lose its seam.
    /// </para>
    /// </summary>
    [Fact]
    public void OneInvadingGroupDoesNotCostTheOthersTheirSeam()
    {
      var all = new List<IPartPlacement>
      {
        Place(Bar(1, 1, 2, 10), 0),
        Place(Bar(3.3, 1, 2, 10), 1),
        Place(Bar(20, 1, 2, 10), 2),
        Place(Bar(22.3, 5, 2, 10), 3),
        Place(Bar(22.45, 0, 1.7, 4.85), 4),
      };

      SparrowNestService.DirectCommonLineSnapResult result = Plan(all);

      result.Edges.Should().NotBeEmpty("the pair that was never in trouble keeps its shared cut");
      (all[1].PlacedPart.MinX - all[0].PlacedPart.MaxX).Should().BeApproximately(CommonLineGap, 1e-6,
        "the untroubled pair closes to the configured gap");
      all[3].Y.Should().Be(0, "the pair whose cut would have eaten the blocker is the only one put back");
    }

    /// <summary>
    /// The control that stops the optimisation opening a hole: the group-by-group retry only tests pairs
    /// involving the parts that moved, so a moved part invading a part that did NOT move still has to be
    /// caught. Here B2 runs into a blocker with no room to be walked clear of it.
    /// </summary>
    [Fact]
    public void AMovedPartInvadingAStationaryOneIsStillCaught()
    {
      var all = new List<IPartPlacement>
      {
        Place(Bar(20, 1, 2, 10), 0),
        Place(Bar(22.3, 5, 2, 10), 1),
        Place(Bar(22.45, 0, 1.7, 4.85), 2),
        Place(Bar(20.3, 0, 1.7, 4.85), 3),
      };

      SparrowNestService.DirectCommonLineSnapResult result = Plan(all);

      using (new FluentAssertions.Execution.AssertionScope())
      {
        result.Edges.Should().BeEmpty("no seam may be closed by cutting into something standing still");
        all[1].Y.Should().Be(0);
        all[1].X.Should().Be(0);
      }
    }

    private static SvgPoint[] Bar(double x, double y, double w, double h) => new[]
    {
      new SvgPoint(x, y), new SvgPoint(x + w, y), new SvgPoint(x + w, y + h), new SvgPoint(x, y + h),
    };

    private static IPartPlacement Place(SvgPoint[] pts, int source)
      => new PartPlacement(new NoFitPolygon(pts)) { X = 0, Y = 0, Rotation = 0, Source = source };

    /// <summary>Every part its own share key, so nothing shares by identity accident; the geometry alone
    /// decides, which is what these two tests are about.</summary>
    private static SparrowNestService.DirectCommonLineSnapResult Plan(List<IPartPlacement> all)
    {
      var rules = new Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)>();
      for (int i = 0; i < all.Count; i++)
      {
        rules[all[i].Source] = (CommonCuttingMode.Unrestricted, i, "direct", true, NormalGap);
      }

      return SparrowNestService.SnapDirectCommonLineEdges(
        all,
        rules,
        sheetW: 40D,
        sheetH: 20D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: 0.0001D);
    }
  }
}
