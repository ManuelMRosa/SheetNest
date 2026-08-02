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
  /// A seam no longer has to be horizontal or vertical. Two faces make one when they LOOK AT EACH
  /// OTHER, at any angle, which is one test instead of "parallel" plus "material on facing sides".
  /// <para>
  /// Every case here passes its tolerances in explicitly. A test that leaned on the shipped defaults
  /// would start failing the day someone calibrates them against a real machine, which is the one thing
  /// those defaults exist to allow.
  /// </para>
  /// </summary>
  public class ObliqueCommonCutFixture
  {
    private const double Kerf = 0.3;

    private static CommonCuttingTolerances Tol(double angleDeg = 0.25) => new CommonCuttingTolerances
    {
      AngleToleranceDeg = angleDeg,
      MinEdgeLengthKerfs = 2,
      GapMinKerfs = 0.4,
      GapMaxKerfs = 1.6,
      MinOverlapKerfs = 2,
    };

    /// <summary>The point of the whole commit: a seam at 30 degrees closes to exactly one kerf.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(-30)]
    [InlineData(45)]
    [InlineData(122.5)]
    public void ASeamClosesToOneKerfAtAnyAngle(double deg)
    {
      var (a, b, n) = Pair(deg, deg, gap: 1.17 * Kerf, aLo: 0, aHi: 12, bLo: 0, bHi: 12);

      Snap(a, b, Tol());

      PerpendicularGap(a, b, n).Should().BeApproximately(Kerf, 1e-6);
    }

    /// <summary>The angular tolerance is about the PAIR, so it is the angle BETWEEN the two faces.</summary>
    [Theory]
    [InlineData(0.24, true)]
    [InlineData(0.26, false)]
    [InlineData(0.9, false)]     // the figure from the specification
    public void FacesMustLookAtEachOtherWithinTheTolerance(double misalignDeg, bool expectSnap)
    {
      var (a, b, _) = Pair(30, 30 + misalignDeg, gap: 1.17 * Kerf, aLo: 0, aHi: 12, bLo: 0, bHi: 12);

      Snap(a, b, Tol(0.25));

      Moved(b).Should().Be(expectSnap);
    }

    /// <summary>
    /// Half the error on each side still adds up. Two faces 0.45 degrees off in opposite directions are
    /// 0.9 degrees apart from each other, and it is that figure the tolerance judges.
    /// </summary>
    [Fact]
    public void HalfTheErrorOnEachSideStillCounts()
    {
      var (a, b, _) = Pair(-0.45, 0.45, gap: 1.17 * Kerf, aLo: 0, aHi: 12, bLo: 0, bHi: 12);

      Snap(a, b, Tol(0.25));

      Moved(b).Should().BeFalse("0.9 degrees between them, whoever is carrying it");
    }

    [Theory]
    [InlineData(0.39, false)]
    [InlineData(0.41, true)]
    [InlineData(1.59, true)]
    [InlineData(1.61, false)]
    public void TheGapHasToBeInTheWindow(double kerfs, bool expectSnap)
    {
      var (a, b, _) = Pair(30, 30, gap: kerfs * Kerf, aLo: 0, aHi: 12, bLo: 0, bHi: 12);

      Snap(a, b, Tol());

      Moved(b).Should().Be(expectSnap);
    }

    /// <summary>
    /// The case that matters physically: the engine left the pair too TIGHT, and the snap opens it back
    /// out to a full kerf rather than leaving the cut to eat into both parts.
    /// </summary>
    [Fact]
    public void APairLeftTooTightIsOpenedBackOutToAFullKerf()
    {
      var (a, b, n) = Pair(30, 30, gap: 0.7 * Kerf, aLo: 0, aHi: 12, bLo: 0, bHi: 12);

      PerpendicularGap(a, b, n).Should().BeApproximately(0.7 * Kerf, 1e-9, "the fixture starts too tight");

      Snap(a, b, Tol());

      PerpendicularGap(a, b, n).Should().BeApproximately(Kerf, 1e-6);
    }

    [Theory]
    [InlineData(1.99, false)]
    [InlineData(2.01, true)]
    public void TheFacesHaveToRunAlongsideEachOther(double overlapKerfs, bool expectSnap)
    {
      // A runs 0..12; B starts where A ends, less the overlap asked for.
      double overlap = overlapKerfs * Kerf;
      var (a, b, _) = Pair(30, 30, gap: 1.17 * Kerf, aLo: 0, aHi: 12, bLo: 12 - overlap, bHi: 24 - overlap);

      Snap(a, b, Tol());

      Moved(b).Should().Be(expectSnap);
    }

    /// <summary>Two parts meeting at a single vertex overlap by nothing, so there is no seam.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    public void ACornerTouchIsNotASeam(double deg)
    {
      var (a, b, _) = Pair(deg, deg, gap: 1.17 * Kerf, aLo: 0, aHi: 12, bLo: 12, bHi: 24);

      Snap(a, b, Tol());

      Moved(b).Should().BeFalse();
    }

    /// <summary>Travel can be capped, which nothing bounded before.</summary>
    [Fact]
    public void TravelIsBounded()
    {
      var tight = Tol();
      tight.MaxSnapTravelKerfs = 0.5;

      // Lining the ends up would ask for a 6 long slide along the face.
      var (a, b, _) = Pair(30, 30, gap: 1.17 * Kerf, aLo: 0, aHi: 12, bLo: 6, bHi: 18);

      Snap(a, b, tight);

      Moved(b).Should().BeFalse("that seam would cost more travel than allowed");
    }

    /// <summary>
    /// The case that used to lose BOTH seams. A part near two faces of the same neighbour closed a
    /// cycle, the group read as inconsistent, and nothing moved. Best-first through a spanning forest
    /// keeps one of them.
    /// </summary>
    [Fact]
    public void APartFacingTwoWaysGetsOneSeamRatherThanNone()
    {
      // A bracket with a notch out of its bottom right, and a bar in the notch near BOTH the vertical
      // face at x = 4 and the floor at y = 3.
      var bracket = new[]
      {
        new SvgPoint(0, 0), new SvgPoint(4, 0), new SvgPoint(4, 3),
        new SvgPoint(20, 3), new SvgPoint(20, 20), new SvgPoint(0, 20),
      };
      var bar = new[]
      {
        new SvgPoint(4.35, 0.4), new SvgPoint(6.35, 0.4), new SvgPoint(6.35, 2.6), new SvgPoint(4.35, 2.6),
      };

      var a = new PartPlacement(new NoFitPolygon(bracket)) { X = 0, Y = 0, Source = 0 };
      var b = new PartPlacement(new NoFitPolygon(bar)) { X = 0, Y = 0, Source = 1 };

      Snap(a, b, Tol(), bracket, bar);

      bool onTheSide = Math.Abs((b.PlacedPart.MinX - 4) - Kerf) < 1e-6;
      bool onTheFloor = Math.Abs((3 - b.PlacedPart.MaxY) - Kerf) < 1e-6;
      (onTheSide || onTheFloor).Should().BeTrue("one of the two faces wins rather than both cancelling out");
    }

    private static bool Moved(IPartPlacement p) => Math.Abs(p.X) > 1e-9 || Math.Abs(p.Y) > 1e-9;

    /// <summary>Distance between the two faces measured along A's outward normal.</summary>
    private static double PerpendicularGap(IPartPlacement a, IPartPlacement b, (double X, double Y) n)
    {
      double best = double.MaxValue;
      foreach (var pa in a.PlacedPart.Points)
      {
        foreach (var pb in b.PlacedPart.Points)
        {
          double d = ((pb.X - pa.X) * n.X) + ((pb.Y - pa.Y) * n.Y);
          if (d > 1e-9 && d < best)
          {
            best = d;
          }
        }
      }

      return best;
    }

    /// <summary>
    /// Two blocks facing each other across <paramref name="gap"/>. A's face runs at
    /// <paramref name="aDeg"/> with its material behind it; B's runs at <paramref name="bDeg"/> with its
    /// material behind ITS face, so the two look at each other.
    /// </summary>
    private static (IPartPlacement A, IPartPlacement B, (double X, double Y) Normal) Pair(
      double aDeg, double bDeg, double gap, double aLo, double aHi, double bLo, double bHi)
    {
      const double Depth = 8;
      var (au, an) = Frame(aDeg);
      var (bu, bn) = Frame(bDeg);

      SvgPoint P(double x, double y) => new SvgPoint(x, y);

      var a1 = P(au.X * aLo, au.Y * aLo);
      var a2 = P(au.X * aHi, au.Y * aHi);
      var aPts = new[]
      {
        a1,
        P(a1.X - (Depth * an.X), a1.Y - (Depth * an.Y)),
        P(a2.X - (Depth * an.X), a2.Y - (Depth * an.Y)),
        a2,
      };

      // B's face sits `gap` out along A's normal, then runs along its own direction.
      var origin = P(gap * an.X, gap * an.Y);
      var b1 = P(origin.X + (bu.X * bLo), origin.Y + (bu.Y * bLo));
      var b2 = P(origin.X + (bu.X * bHi), origin.Y + (bu.Y * bHi));
      var bPts = new[]
      {
        b1,
        b2,
        P(b2.X + (Depth * bn.X), b2.Y + (Depth * bn.Y)),
        P(b1.X + (Depth * bn.X), b1.Y + (Depth * bn.Y)),
      };

      var a = new PartPlacement(new NoFitPolygon(aPts)) { X = 0, Y = 0, Source = 0 };
      var b = new PartPlacement(new NoFitPolygon(bPts)) { X = 0, Y = 0, Source = 1 };
      return (a, b, (an.X, an.Y));
    }

    private static ((double X, double Y) U, (double X, double Y) N) Frame(double deg)
    {
      double r = deg * Math.PI / 180.0;
      return ((Math.Cos(r), Math.Sin(r)), (-Math.Sin(r), Math.Cos(r)));
    }

    private static void Snap(IPartPlacement a, IPartPlacement b, CommonCuttingTolerances tol, SvgPoint[] aPts = null, SvgPoint[] bPts = null)
    {
      aPts ??= a.PlacedPart.Points;
      bPts ??= b.PlacedPart.Points;

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
    }
  }
}
