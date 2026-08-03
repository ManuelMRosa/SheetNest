namespace DeepNestLib.CiTests.NestProject
{
  using DeepNestLib.NestProject;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Which of the three possible kerfs wins. Common cutting is built on the kerf, so getting this
  /// wrong does not throw: it silently places parts at the wrong gap.
  /// </summary>
  public class KerfResolverFixture
  {
    /// <summary>Every combination of the three sources being present or absent.</summary>
    [Theory]
    [InlineData(0.5, 0.4, 0.3, 0.5)]    // all three -> the part
    [InlineData(0.5, 0.4, 0, 0.5)]
    [InlineData(0.5, -1, 0.3, 0.5)]
    [InlineData(0.5, -1, 0, 0.5)]
    [InlineData(-1, 0.4, 0.3, 0.4)]     // no part -> the job
    [InlineData(-1, 0.4, 0, 0.4)]
    [InlineData(-1, -1, 0.3, 0.3)]      // neither -> what the nest file measured
    [InlineData(-1, -1, 0, 0)]          // nobody said -> nothing
    public void TheMostSpecificSourceWins(double part, double job, double derived, double expected)
    {
      KerfResolver.ResolveMm(part, job, derived).Should().Be(expected);
    }

    /// <summary>
    /// A number far too small to be a cut width is not a cut width. Reported from the shop: a per-part
    /// kerf of 1.38777878078145E-17, which is 2^-56 and pure floating point residue from stepping a
    /// spinner up and back down. Taken as real it did not merely mean nothing, it went on to REPLACE the
    /// editor's noise floor and turned four correctly placed parts red.
    /// </summary>
    [Fact]
    public void SpinnerResidueIsNotAKerf()
    {
      const double Residue = 1.38777878078145E-17;

      KerfResolver.ResolveMm(Residue, -1, 0).Should().Be(0, "that is noise, not a cut");
      KerfResolver.ResolveMm(-1, Residue, 0).Should().Be(0);

      // And it must not shadow a real one that IS available.
      KerfResolver.ResolveMm(Residue, 0.4, 0).Should().Be(0.4);
      KerfResolver.ResolveMm(Residue, -1, 0.34).Should().Be(0.34, "the measured one still comes through");
    }

    /// <summary>
    /// The other side of the threshold, so it never grows into something that swallows a real setting.
    /// A fine laser kerf is a few hundredths of a millimetre; the cut-off is a thousandth.
    /// </summary>
    [Fact]
    public void ASmallButRealKerfStillCounts()
    {
      KerfResolver.ResolveMm(0.05, -1, 0).Should().Be(0.05);
      KerfResolver.ResolveMm(KerfResolver.MinimumMeaningfulKerfMm, -1, 0)
        .Should().Be(KerfResolver.MinimumMeaningfulKerfMm, "the threshold itself is allowed");
      KerfResolver.MinimumMeaningfulKerfMm.Should().BeLessThan(
        0.05, "no cutting process has a kerf near a micron, so this can never reject a real figure");
    }

    /// <summary>Zero is "not set", not "a kerf of zero": nothing can cut a slot of no width.</summary>
    [Fact]
    public void ZeroCountsAsUnset()
    {
      KerfResolver.ResolveMm(0, 0.4, 0.3).Should().Be(0.4);
      KerfResolver.ResolveMm(0, 0, 0.3).Should().Be(0.3);
    }

    /// <summary>
    /// THE REGRESSION ROW. With neither a part nor a job kerf set, which is what every existing project
    /// carries, the answer is exactly what the nest file gave. Nothing about an existing job changes.
    /// </summary>
    [Fact]
    public void AJobThatSaysNothingKeepsTheKerfItAlreadyHad()
    {
      KerfResolver.ResolveDrawingUnits(-1, -1, 0.0134, 1d / 25.4d)
        .Should().Be(0.0134, "the derived value is already in drawing units and must pass through untouched");
    }

    /// <summary>
    /// A typed-in kerf is in millimetres whatever the drawing is in, so it converts; the derived one is
    /// already in drawing units and must NOT be converted again. Converting it twice is the obvious way
    /// to be wrong by a factor of 25.4 and have every part come out at the wrong gap.
    /// </summary>
    [Fact]
    public void TypedMillimetresConvertAndTheDerivedValueDoesNot()
    {
      const double ToInches = 1d / 25.4d;
      KerfResolver.ResolveDrawingUnits(0.5, -1, 0, ToInches).Should().BeApproximately(0.5 / 25.4, 1e-12);
      KerfResolver.ResolveDrawingUnits(-1, 0.4, 0, ToInches).Should().BeApproximately(0.4 / 25.4, 1e-12);
      KerfResolver.ResolveDrawingUnits(-1, -1, 0.02, ToInches).Should().Be(0.02);

      // In a millimetre drawing there is nothing to convert.
      KerfResolver.ResolveDrawingUnits(0.5, -1, 0, 1d).Should().Be(0.5);
    }
  }
}
