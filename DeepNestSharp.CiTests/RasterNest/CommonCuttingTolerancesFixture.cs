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
  /// The tolerances that decide a seam used to be literals inside the snap. Now they are settings, and
  /// these fix the conversion, because the conversion is the easy thing to get wrong.
  /// </summary>
  public class CommonCuttingTolerancesFixture
  {
    private const double Kerf = 0.3;

    /// <summary>
    /// The trap. The old test was <c>Math.Abs(dx) &lt; 1e-3 * len</c>, which is a SINE, so the angle it
    /// allowed is 0.0572958 degrees and not 0.001. Reading it as 0.001 degrees makes the tolerance
    /// fifty-seven times tighter and common cutting silently stops finding anything.
    /// <para>
    /// Both rows fail if someone makes that mistake: 0.05 degrees is inside the real tolerance and well
    /// outside the misread one.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.05, true)]   // inside 0.0572958
    [InlineData(0.07, false)]  // outside it
    public void TheDefaultAngleToleranceIsTheSineTheOldCodeUsed(double tiltDeg, bool expectSnap)
    {
      SnapsAtTilt(tiltDeg, CommonCuttingTolerances.Default).Should().Be(expectSnap);
    }

    /// <summary>The setting has to actually reach the edge test, not just exist.</summary>
    [Fact]
    public void AWiderAngleToleranceAcceptsAnEdgeTheDefaultRejects()
    {
      SnapsAtTilt(0.4, CommonCuttingTolerances.Default).Should().BeFalse("0.4 degrees is well outside the default");
      SnapsAtTilt(0.4, new CommonCuttingTolerances { AngleToleranceDeg = 0.5 }).Should().BeTrue();
    }

    /// <summary>
    /// The gap window, both edges of it. The pair below sits 0.35 apart on a 0.3 kerf, so 1.1667 kerfs:
    /// inside the shipped [0.4, 1.6] and outside a window that stops at 1.0.
    /// </summary>
    [Fact]
    public void TheGapWindowDecidesWhetherASeamIsRecognised()
    {
      SnapsAtTilt(0, CommonCuttingTolerances.Default).Should().BeTrue();
      SnapsAtTilt(0, new CommonCuttingTolerances { GapMaxKerfs = 1.0 }).Should().BeFalse("1.1667 kerfs is past the window");
      SnapsAtTilt(0, new CommonCuttingTolerances { GapMinKerfs = 1.3 }).Should().BeFalse("and short of it from the other side");
    }

    /// <summary>
    /// The edges below run alongside each other for 9.8, which on a 0.3 kerf is 32.67 kerfs. So 32 must
    /// still accept and 34 must reject: the pair straddles the setting.
    /// </summary>
    [Fact]
    public void TheOverlapMinimumDecidesWhetherTwoEdgesReallyRunAlongsideEachOther()
    {
      SnapsAtTilt(0, new CommonCuttingTolerances { MinOverlapKerfs = 32 })
        .Should().BeTrue("9.8 of overlap is 32.67 kerfs, so 32 is still met");
      SnapsAtTilt(0, new CommonCuttingTolerances { MinOverlapKerfs = 34 })
        .Should().BeFalse("34 kerfs would be 10.2, more than these edges share");
    }

    /// <summary>
    /// Aligning the low ends can ask for a long slide, and until this setting existed nothing bounded
    /// it. Here the seam needs 0.2 of travel, which is 0.667 kerfs.
    /// </summary>
    [Fact]
    public void TravelCanBeBounded()
    {
      SnapsAtTilt(0, new CommonCuttingTolerances { MaxSnapTravelKerfs = 0.1 })
        .Should().BeFalse("the part would have to move further than allowed");
      SnapsAtTilt(0, new CommonCuttingTolerances { MaxSnapTravelKerfs = 5 }).Should().BeTrue();
    }

    [Fact]
    public void ValidateRejectsAWindowThatCannotDescribeASeam()
    {
      new CommonCuttingTolerances { GapMinKerfs = 2, GapMaxKerfs = 1 }
        .Invoking(t => t.Validate()).Should().Throw<ArgumentOutOfRangeException>();
      new CommonCuttingTolerances { AngleToleranceDeg = 0 }
        .Invoking(t => t.Validate()).Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// A: an upright 2 x 10 bar whose right edge is exactly vertical at x = 3.
    /// B: the same bar tilted by <paramref name="tiltDeg"/>, its left edge starting 0.35 to the right and
    /// 0.2 higher, so the seam needs both a perpendicular nudge and a slide along the edge.
    /// Returns whether the snap moved B.
    /// </summary>
    private static bool SnapsAtTilt(double tiltDeg, CommonCuttingTolerances tol)
    {
      var aPts = new[]
      {
        new SvgPoint(1, 1), new SvgPoint(3, 1), new SvgPoint(3, 11), new SvgPoint(1, 11),
      };

      double t = tiltDeg * Math.PI / 180.0;
      double ux = Math.Sin(t), uy = Math.Cos(t);      // along B's left edge, tilted off vertical
      double rx = Math.Cos(t), ry = -Math.Sin(t);     // across it, to the right
      var bl = new SvgPoint(3.35, 1.2);
      var tl = new SvgPoint(bl.X + (10 * ux), bl.Y + (10 * uy));
      var bPts = new[]
      {
        bl,
        new SvgPoint(bl.X + (2 * rx), bl.Y + (2 * ry)),
        new SvgPoint(tl.X + (2 * rx), tl.Y + (2 * ry)),
        tl,
      };

      var a = new PartPlacement(new NoFitPolygon(aPts)) { X = 0, Y = 0, Rotation = 0, Source = 0 };
      var b = new PartPlacement(new NoFitPolygon(bPts)) { X = 0, Y = 0, Rotation = 0, Source = 1 };

      SparrowNestService.SnapCommonLineEdges(
        new List<IPartPlacement> { a, b },
        new Dictionary<int, double> { { 0, Kerf }, { 1, Kerf } },
        new Dictionary<int, INfp>
        {
          { 0, SparrowNestService.ToolingFootprint(new NoFitPolygon(aPts), null, Kerf, 0) },
          { 1, SparrowNestService.ToolingFootprint(new NoFitPolygon(bPts), null, Kerf, 0) },
        },
        new Dictionary<int, (CommonCuttingMode Cc, int ShareKey)>
        {
          { 0, (CommonCuttingMode.Unrestricted, 0) },
          { 1, (CommonCuttingMode.Unrestricted, 1) },
        },
        tol);

      return Math.Abs(b.X) > 1e-9 || Math.Abs(b.Y) > 1e-9;
    }
  }
}
