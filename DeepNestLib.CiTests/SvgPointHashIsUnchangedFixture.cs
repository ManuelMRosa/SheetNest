namespace DeepNestLib.CiTests
{
  using DeepNestLib;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The tripwire for the obvious "tidier" version of curve provenance: putting the flag on SvgPoint.
  /// </summary>
  /// <remarks>
  /// It cannot go there. SvgPoint.Equals is literally <c>GetHashCode() == GetHashCode()</c> and the hash
  /// mixes in Exact and Marked, so any new field in it changes the key the Minkowski NFP cache is stored
  /// under: either identical geometry caches twice, or two different shapes collide. On top of that,
  /// Rotate, MirrorX and CloneTop all build <c>new SvgPoint(x, y)</c> without copying the extra fields,
  /// so a per-point flag would be lost on the first rotation, which is exactly the step the live path
  /// takes. Hence the parallel array on the polygon.
  /// </remarks>
  public class SvgPointHashIsUnchangedFixture
  {
    [Fact]
    public void TheHashStillMixesExactlyWhatItAlwaysDid()
    {
      var point = new SvgPoint(1.23456, -7.6543);

      point.GetHashCode().Should().Be(
        System.HashCode.Combine(point.Exact, point.Marked, System.Math.Round(point.X, 4), System.Math.Round(point.Y, 4)),
        "adding a field to SvgPoint changes the Minkowski cache key");
    }

    [Fact]
    public void PointsThatDifferOnlyInTheirFlagsAreStillDifferent()
    {
      var a = new SvgPoint(1, 2) { Exact = true };
      var b = new SvgPoint(1, 2) { Exact = false };

      a.Equals(b).Should().BeFalse("Exact is part of identity here, whether or not that is wise");
    }

    /// <summary>Curve provenance lives on the polygon, not the point, and this says so out loud.</summary>
    [Fact]
    public void TheCurveFlagIsNotOnThePoint()
    {
      typeof(SvgPoint).GetProperty("Curved").Should().BeNull();
      typeof(SvgPoint).GetProperty("CurvedSegments").Should().BeNull();
    }
  }
}
