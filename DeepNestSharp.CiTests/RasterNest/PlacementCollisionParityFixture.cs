namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// The clearance test in C++ (sheetnest_core.dll) against the C# it was translated from: the same verdict on
  /// every pair. The pairs are invented, built from a fixed seed, and most of them are placed right at the edge of
  /// the clearance, where the round shells and the depth of a bite decide, since a pair far apart or deep inside
  /// each other would agree whatever the translation got wrong.
  /// </summary>
  public class PlacementCollisionParityFixture
  {
    private readonly ITestOutputHelper output;

    public PlacementCollisionParityFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Theory]
    [InlineData("spaced-inches", 1.0, 0.25, 1e-4)]
    [InlineData("half-inch", 1.0, 0.5, 1e-4)]
    [InlineData("shared-cut", 1.0, 0.0, 1e-4)]
    [InlineData("kerf", 1.0, 0.25, 0.006)]
    [InlineData("spaced-mm", 25.4, 6.35, 1e-4)]
    [InlineData("kerf-mm", 25.4, 0.0, 0.2)]
    public void TheNativeTestGivesTheCSharpVerdictOnEveryPair(string name, double unit, double clearance, double sliver)
    {
      var random = new Random(name.Aggregate(23, (h, c) => (h * 31) + c));
      int tooClose = 0;
      const int Pairs = 1500;
      for (int k = 0; k < Pairs; k++)
      {
        var (a, b) = Pair(random, unit, clearance, sliver);

        bool managed = PlacementCollision.TooCloseManaged(a, b, clearance, sliver);
        bool ran = NativeCollision.TryTooCloseNative(a, b, clearance, sliver, out bool native);

        ran.Should().BeTrue("sheetnest_core.dll ships next to the tests and must accept this pair");
        native.Should().Be(managed, $"pair {k} of {name}");
        tooClose += managed ? 1 : 0;
      }

      this.output.WriteLine($"{name}: {Pairs} pairs, {tooClose} too close");
      tooClose.Should().BeInRange(Pairs / 10, Pairs - (Pairs / 10), "the pairs have to sit on both sides of the line or they test nothing");
    }

    /// <summary>A part may arrive without holes, with a null hole or with a degenerate contour; the C# code skips
    /// those, and so must the native one.</summary>
    [Fact]
    public void DegenerateContoursAreSkippedTheSameWay()
    {
      var a = Rectangle(10, 10);
      a.Children.Add(Polygon((1, 1), (2, 1))); // two points: not a contour
      a.Children.Add(null);
      var b = Rectangle(10, 10).Shift(10.1, 0);

      foreach (double clearance in new[] { 0.0, 0.05, 0.2 })
      {
        bool managed = PlacementCollision.TooCloseManaged(a, b, clearance, 1e-4);
        NativeCollision.TryTooCloseNative(a, b, clearance, 1e-4, out bool native).Should().BeTrue();
        native.Should().Be(managed, $"clearance {clearance}");
      }
    }

    /// <summary>
    /// Where the rounding rule decides. A's right edge is at 1 + 1/128, exact in binary and exactly 1007812.5
    /// integer units, and B starts at 1007812. Rounded half to even, as .NET does, the two only touch; rounded
    /// half away from zero A would reach 1007813 and bite one unit into B, past a sliver of 1e-7.
    /// </summary>
    [Fact]
    public void ATieIsRoundedToEvenAsInCSharp()
    {
      var a = Rectangle(1 + (1.0 / 128), 2);
      var b = Rectangle(2, 2).Shift(1.007812, 0);

      bool managed = PlacementCollision.TooCloseManaged(a, b, 0, 1e-7);
      NativeCollision.TryTooCloseNative(a, b, 0, 1e-7, out bool native).Should().BeTrue();

      managed.Should().BeFalse("rounded to even the edges meet and nothing overlaps");
      native.Should().Be(managed);
    }

    /// <summary>Two parts, the second beside, above or below the first at a gap close to the clearance: a little
    /// inside, a little outside, or overlapping by about the sliver.</summary>
    private static (INfp A, INfp B) Pair(Random random, double unit, double clearance, double sliver)
    {
      INfp a = Shape(random, unit);
      INfp b = Shape(random, unit);
      double gap = random.Next(4) switch
      {
        0 => clearance * (1 + ((random.NextDouble() - 0.5) * 0.02)), // within 1 % of the line
        1 => clearance + ((random.NextDouble() - 0.5) * 4 * Math.Max(sliver, 1e-4)), // within a few slivers of it
        2 => -sliver * random.NextDouble() * 3, // overlapping by about the sliver
        _ => (random.NextDouble() * 1.5 * Math.Max(clearance, 0.1 * unit)) - (0.2 * unit), // anywhere near
      };

      bool sideways = random.Next(2) == 0;
      double slide = (random.NextDouble() - 0.5) * 4 * unit;
      INfp placedB = sideways
        ? b.Shift(a.MaxX - b.MinX + gap, a.MinY - b.MinY + slide)
        : b.Shift(a.MinX - b.MinX + slide, a.MaxY - b.MinY + gap);
      return (a, placedB);
    }

    private static INfp Shape(Random random, double unit)
    {
      double w = (3 + (random.NextDouble() * 6)) * unit;
      double h = (2 + (random.NextDouble() * 5)) * unit;
      INfp shape = random.Next(5) switch
      {
        0 => Rectangle(w, h),
        1 => Polygon((0, 0), (w, 0), (w, h / 3), (w / 3, h / 3), (w / 3, h), (0, h)),
        2 => Polygon((0, 0), (w, 0), (w, h), (w * 0.7, h), (w * 0.7, h * 0.35), (w * 0.3, h * 0.35), (w * 0.3, h), (0, h)),
        3 => Ring(Math.Min(w, h) / 2),
        _ => Polygon((0, 0), (w, 0), (0, h)),
      };
      double angle = random.Next(8) * 45.0 + (random.NextDouble() < 0.3 ? random.NextDouble() * 30 : 0);
      return angle == 0 ? shape : shape.Rotate(angle);
    }

    private static NoFitPolygon Polygon(params (double X, double Y)[] points)
      => new NoFitPolygon(points.Select(p => new SvgPoint(p.X, p.Y)).ToArray());

    private static INfp Rectangle(double w, double h) => Polygon((0, 0), (w, 0), (w, h), (0, h));

    private static INfp Ring(double r)
    {
      var disc = Polygon(Enumerable.Range(0, 40).Select(k => (r + (r * Math.Cos(k * Math.PI / 20)), r + (r * Math.Sin(k * Math.PI / 20)))).ToArray());
      disc.Children.Add(Polygon(Enumerable.Range(0, 24).Select(k => (r + (r / 2 * Math.Cos(k * Math.PI / 12)), r + (r / 2 * Math.Sin(k * Math.PI / 12)))).ToArray()));
      return disc;
    }
  }
}
