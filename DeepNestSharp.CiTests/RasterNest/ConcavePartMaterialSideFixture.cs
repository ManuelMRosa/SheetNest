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
  /// Which side of an edge the material sits on was decided by comparing the edge against the ARITHMETIC
  /// MEAN OF THE VERTICES. That is inside the part only while the part is convex, so every concave face
  /// got the answer backwards and could never form a seam.
  /// </summary>
  public class ConcavePartMaterialSideFixture
  {
    private const double NormalGap = 0.3;
    private const double CommonLineGap = 0.01;

    /// <summary>
    /// A bracket with a notch cut out of its bottom right, wound counter-clockwise:
    /// (0,0) (4,0) (4,3) (20,3) (20,20) (0,20). The empty notch is x &gt; 4, y &lt; 3.
    /// <para>
    /// The notch's vertical face runs (4,0) to (4,3) and has material to its LEFT, so it is a right-hand
    /// face. The vertex mean is x = 8, which is on the far side of it, so the old test
    /// <c>mean &lt; edgeX</c> answered false and called it a left-hand face. A bar parked in the notch
    /// therefore never paired with it: both faces claimed to be looking the same way.
    /// </para>
    /// </summary>
    [Fact]
    public void ABarInAConcaveNotchStillFindsItsSeam()
    {
      var bracket = new[]
      {
        new SvgPoint(0, 0), new SvgPoint(4, 0), new SvgPoint(4, 3),
        new SvgPoint(20, 3), new SvgPoint(20, 20), new SvgPoint(0, 20),
      };

      // Premise: the ring really is counter-clockwise and the vertex mean really is on the wrong side.
      SignedArea(bracket).Should().BeGreaterThan(0, "the fixture assumes a counter-clockwise ring");
      MeanX(bracket).Should().BeGreaterThan(4, "if the mean fell left of the notch face there would be no bug to catch");

      // A bar in the notch. Its top is kept clear of the notch floor at y = 3 on purpose, so the bar is
      // not trying to seam on two faces at once; seaming a part on two sides is a separate question.
      var bar = new[]
      {
        new SvgPoint(4.35, 0.4), new SvgPoint(6.35, 0.4), new SvgPoint(6.35, 2.0), new SvgPoint(4.35, 2.0),
      };

      IPartPlacement bracketPlacement = Place(bracket, 0);
      IPartPlacement barPlacement = Place(bar, 1);

      SparrowNestService.DirectCommonLineSnapResult result = Plan(bracketPlacement, barPlacement);

      result.Edges.Should().ContainSingle("a concave face is still a face and can still share its cut");
      (barPlacement.PlacedPart.MinX - 4).Should().BeApproximately(CommonLineGap, 1e-6,
        "the notch face and the bar's face share one cut, so they close to the configured gap");
    }

    /// <summary>
    /// The control. For a convex part the vertex mean IS inside, so the old rule and the new one agree,
    /// and this pair has to keep behaving exactly as it always did.
    /// </summary>
    [Fact]
    public void TwoConvexBarsStillPairTheSameWay()
    {
      var left = new[]
      {
        new SvgPoint(1, 1), new SvgPoint(3, 1), new SvgPoint(3, 11), new SvgPoint(1, 11),
      };
      var right = new[]
      {
        new SvgPoint(3.35, 1), new SvgPoint(5.35, 1), new SvgPoint(5.35, 11), new SvgPoint(3.35, 11),
      };

      IPartPlacement a = Place(left, 0);
      IPartPlacement b = Place(right, 1);

      SparrowNestService.DirectCommonLineSnapResult result = Plan(a, b);

      result.Edges.Should().ContainSingle();
      (b.PlacedPart.MinX - 3).Should().BeApproximately(CommonLineGap, 1e-6);
    }

    private static IPartPlacement Place(SvgPoint[] pts, int source)
      => new PartPlacement(new NoFitPolygon(pts)) { X = 0, Y = 0, Rotation = 0, Source = source };

    private static SparrowNestService.DirectCommonLineSnapResult Plan(IPartPlacement first, IPartPlacement second)
      => SparrowNestService.SnapDirectCommonLineEdges(
        new List<IPartPlacement> { first, second },
        new Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)>
        {
          { 0, (CommonCuttingMode.Unrestricted, 0, "direct", true, NormalGap) },
          { 1, (CommonCuttingMode.Unrestricted, 1, "direct", true, NormalGap) },
        },
        sheetW: 40D,
        sheetH: 40D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: 0.0001D);

    private static double SignedArea(SvgPoint[] pts)
    {
      double sum = 0;
      for (int i = 0; i < pts.Length; i++)
      {
        var p = pts[i];
        var q = pts[(i + 1) % pts.Length];
        sum += (p.X * q.Y) - (q.X * p.Y);
      }

      return sum / 2.0;
    }

    private static double MeanX(SvgPoint[] pts)
    {
      double sum = 0;
      foreach (var p in pts)
      {
        sum += p.X;
      }

      return sum / pts.Length;
    }
  }
}
