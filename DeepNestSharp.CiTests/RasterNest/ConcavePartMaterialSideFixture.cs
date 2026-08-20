namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
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
    private const double Kerf = 0.3;

    /// <summary>
    /// A bracket with a notch cut out of its bottom right, wound counter-clockwise:
    /// (0,0) (4,0) (4,3) (20,3) (20,20) (0,20). The empty notch is x &gt; 4, y &lt; 3.
    /// <para>
    /// The notch's vertical face runs (4,0) to (4,3) and has material to its LEFT, so it is a right-hand
    /// face. The vertex mean is x = 8, which is on the far side of it, so the old test
    /// <c>mean &lt; edgeX</c> answered false and called it a left-hand face. A bar parked in the notch
    /// one kerf away therefore never paired with it: both faces claimed to be looking the same way.
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

      // A bar in the notch, its left face 0.35 to the right of the notch face (1.17 kerfs, inside the
      // window). Its top is kept well clear of the notch floor at y = 3 on purpose: at 2.6 it would be
      // 0.4 below, which is ALSO inside the window, and the bar would be trying to seam on two faces at
      // once. That makes a cycle, the group reads as inconsistent and nothing moves at all. Seaming a
      // part on two sides is a real limitation and it belongs to the spanning-forest work, not here.
      var bar = new[]
      {
        new SvgPoint(4.35, 0.4), new SvgPoint(6.35, 0.4), new SvgPoint(6.35, 2.0), new SvgPoint(4.35, 2.0),
      };

      var a = new PartPlacement(new NoFitPolygon(bracket)) { X = 0, Y = 0, Rotation = 0, Source = 0 };
      var b = new PartPlacement(new NoFitPolygon(bar)) { X = 0, Y = 0, Rotation = 0, Source = 1 };

      SparrowNestService.SnapCommonLineEdges(
        new List<IPartPlacement> { a, b },
        new Dictionary<int, double> { { 0, Kerf }, { 1, Kerf } },
        new Dictionary<int, INfp>
        {
          { 0, SparrowNestService.ToolingFootprint(new NoFitPolygon(bracket), null, Kerf, 0) },
          { 1, SparrowNestService.ToolingFootprint(new NoFitPolygon(bar), null, Kerf, 0) },
        },
        new Dictionary<int, (CommonCuttingMode Cc, int ShareKey)>
        {
          { 0, (CommonCuttingMode.Unrestricted, 0) },
          { 1, (CommonCuttingMode.Unrestricted, 1) },
        });

      (b.PlacedPart.MinX - 4).Should().BeApproximately(Kerf, 1e-6,
        "the notch face and the bar's face share one cut, so they end up exactly one kerf apart");
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

      var a = new PartPlacement(new NoFitPolygon(left)) { X = 0, Y = 0, Rotation = 0, Source = 0 };
      var b = new PartPlacement(new NoFitPolygon(right)) { X = 0, Y = 0, Rotation = 0, Source = 1 };

      SparrowNestService.SnapCommonLineEdges(
        new List<IPartPlacement> { a, b },
        new Dictionary<int, double> { { 0, Kerf }, { 1, Kerf } },
        new Dictionary<int, INfp>
        {
          { 0, SparrowNestService.ToolingFootprint(new NoFitPolygon(left), null, Kerf, 0) },
          { 1, SparrowNestService.ToolingFootprint(new NoFitPolygon(right), null, Kerf, 0) },
        },
        new Dictionary<int, (CommonCuttingMode Cc, int ShareKey)>
        {
          { 0, (CommonCuttingMode.Unrestricted, 0) },
          { 1, (CommonCuttingMode.Unrestricted, 1) },
        });

      (b.PlacedPart.MinX - 3).Should().BeApproximately(Kerf, 1e-6);
    }

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
