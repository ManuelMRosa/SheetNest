namespace DeepNestSharp.CiTests
{
  using System.Linq;
  using DeepNestSharp.RasterNest;
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// A part follows the job's rotation setting until somebody says otherwise, and the engine reads the two
  /// the same way round.
  /// <para>Reported on issue #2, for a month: free rotation "doesn't work anymore", parts only ever coming
  /// out at ninety degree steps. The job setting was free; the parts were not. Edit Part seeded the job's
  /// number into its picker and wrote it straight back on every OK, and the picker had no way to say
  /// "follow the job", so opening a part once and pressing OK pinned it for good and the job setting was
  /// decorative from then on. His parts list said "4-way" on every card, which is exactly what that looks
  /// like.</para>
  /// <para>None of this was covered. Every rotation test drove the GLOBAL argument with the per-part value
  /// left at its sentinel, so the one configuration that fails in production was the one nobody tried.</para>
  /// </summary>
  public class RotationInheritsTheJobFixture
  {
    /// <summary>The sentinel the model is born with and the one the picker writes have to be the same
    /// number, or "follow the job" would quietly mean "turn ninety degrees".</summary>
    [Fact]
    public void ThePickersInheritValueIsTheOneTheEngineTreatsAsUnset()
    {
      RotationSelector.InheritsJob.Should().Be(-1);
      new DeepNestLib.NestProject.DetailLoadInfo().Rotations.Should().Be(RotationSelector.InheritsJob);

      // The engine's rule, from SparrowNestService.LoadAll: anything above zero is the part's own choice.
      (RotationSelector.InheritsJob > 0).Should().BeFalse("a part that has chosen nothing must not win");
    }

    /// <summary>
    /// The rule the whole complaint turns on. Above zero the part has chosen; at or below, the job decides.
    /// </summary>
    [Theory]
    [InlineData(-1, 36, 36)]   // untouched part, job set to free -> free
    [InlineData(0, 36, 36)]    // the other unset value behaves the same
    [InlineData(4, 36, 4)]     // a part deliberately set to 4-way keeps it, even against a free job
    [InlineData(-1, 4, 4)]     // untouched part, job set to 4-way
    [InlineData(36, 4, 36)]    // a part deliberately set to free keeps it against a 4-way job
    public void WhoWinsBetweenThePartAndTheJob(int part, int job, int expected)
    {
      int code = part > 0 ? part : job;
      code.Should().Be(expected);
    }

    /// <summary>
    /// Free is the only code the engine treats as continuous, and 45 degree steps is NOT it. Both the
    /// picker and the parts list used to call 8 "free", so a user could select it, be told twice that it
    /// was free, and be handed eight discrete angles.
    /// </summary>
    [Fact]
    public void FortyFiveDegreeStepsIsNotFree()
    {
      var fortyFive = RotationCodes.PermittedSet(8);
      fortyFive.Should().BeEquivalentTo(new[] { 0, 45, 90, 135, 180, 225, 270, 315 });

      // What SparrowNestService.LoadAll stores as Continuous, and what BuildJaguaJson keys off.
      (8 == 36).Should().BeFalse("45 degree steps is a discrete set, whatever the label says");
    }

    [Fact]
    public void TheFourWayCodeIsTheFourSquareOrientations()
      => RotationCodes.PermittedSet(4).Should().BeEquivalentTo(new[] { 0, 90, 180, 270 });

    /// <summary>Free falls back to a fine discrete set for the hole-filling pass, which is the only place
    /// that needs concrete angles; the nest itself gets no orientation list at all.</summary>
    [Fact]
    public void FreeOffersAFineSetForThePassesThatNeedOne()
    {
      var free = RotationCodes.PermittedSet(36).ToList();
      free.Should().HaveCount(24);
      free.Should().Contain(15);
    }
  }
}
