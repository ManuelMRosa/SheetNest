namespace DeepNestSharp.CiTests
{
  using System.Linq;
  using System.Reflection;
  using System.Text.Json;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  public class NestingEffortSessionFixture
  {
    [Theory]
    [InlineData(-1, (int)NestEffort.Normal)]
    [InlineData(0, (int)NestEffort.Fast)]
    [InlineData(1, (int)NestEffort.Normal)]
    [InlineData(2, (int)NestEffort.Best)]
    [InlineData(99, (int)NestEffort.Normal)]
    public void AnUnsetOrInvalidSessionResolvesAgainstTheProductDefault(int saved, int expected)
    {
      NestEffortDefaults.FromPersisted(saved).Should().Be((NestEffort)expected);
    }

    /// <summary>
    /// Closing the app used to write whatever effort was in memory over the stored one, which on an
    /// untouched install was just the default written back. That turned "never chosen" into a choice
    /// nobody made, and it is why the product default only ever reached the very first run.
    /// </summary>
    [Fact]
    public void ClosingDoesNotOverwriteTheStoredEffort()
    {
      var session = new SessionState
      {
        NestEffort = (int)NestEffort.Best,
        PreferRectangularOffcut = true,
        OffcutDirection = 3,
        OffcutSpacing = 0.375,
      };

      CaptureClose(session);

      string json = JsonSerializer.Serialize(session);
      var reopened = JsonSerializer.Deserialize<SessionState>(json);

      NestEffortDefaults.FromPersisted(reopened.NestEffort).Should().Be(NestEffort.Best,
        "the effort is written when the operator picks it, and closing must leave that alone");
      reopened.PreferRectangularOffcut.Should().BeTrue("closing must preserve dialog-owned preferences");
      reopened.OffcutDirection.Should().Be(3);
      reopened.OffcutSpacing.Should().Be(0.375);
      reopened.EffectiveCommonLineGapInches.Should().BeApproximately(0.015, 1e-12,
        "closing must retain the physical common-line gap alongside the other dialog preferences");
      reopened.EffectiveCommonLineMaxClusterParts.Should().Be(36,
        "closing must retain the common-line cluster policy alongside the other dialog preferences");
    }

    /// <summary>And a session that never held a choice still does not hold one after a close.</summary>
    [Fact]
    public void ClosingDoesNotInventAChoiceThatWasNeverMade()
    {
      var session = new SessionState();

      CaptureClose(session);

      session.NestEffort.Should().Be(NestEffortDefaults.Unset);
      NestEffortDefaults.FromPersisted(session.NestEffort).Should().Be(NestEffortDefaults.Default);
    }

    /// <summary>
    /// The one-off correction, and the case that makes it necessary: a file written by a build that
    /// clobbered the marker holds a Fast nobody asked for, with no version stamp on it.
    /// </summary>
    [Fact]
    public void AFastLeftBehindByAnOlderBuildIsForgottenOnce()
    {
      var old = JsonSerializer.Deserialize<SessionState>("{\"NestEffort\":0}");

      old.SettingsVersion.Should().Be(0, "nothing before this build stamped a version");
      NestEffortDefaults.NeedsEffortReset(old.NestEffort, old.SettingsVersion).Should().BeTrue();
      NestEffortDefaults.FromPersisted(NestEffortDefaults.Unset).Should().Be(NestEffort.Normal);
    }

    /// <summary>
    /// And the half that stops it running away with a real preference: once the file carries the stamp,
    /// a stored Fast is a choice and stays. Without the stamp the correction would undo it on every
    /// start and Fast would be unselectable.
    /// </summary>
    [Fact]
    public void AFastChosenAfterTheCorrectionIsKept()
    {
      var current = new SessionState
      {
        NestEffort = (int)NestEffort.Fast,
        SettingsVersion = SessionState.CurrentSettingsVersion,
      };

      NestEffortDefaults.NeedsEffortReset(current.NestEffort, current.SettingsVersion).Should().BeFalse();
      NestEffortDefaults.FromPersisted(current.NestEffort).Should().Be(NestEffort.Fast);
    }

    /// <summary>Normal and Best could only ever have been picked on purpose, so they are never touched.</summary>
    [Theory]
    [InlineData((int)NestEffort.Normal)]
    [InlineData((int)NestEffort.Best)]
    public void AnEffortOnlyAChoiceCouldHaveStoredIsNeverForgotten(int stored)
    {
      NestEffortDefaults.NeedsEffortReset(stored, settingsVersion: 0).Should().BeFalse();
    }

    [Fact]
    public void EveryOptionalEngineEffortUsesTheProductDefault()
    {
      var effortParameters = typeof(SparrowNestService)
        .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .SelectMany(method => method.GetParameters())
        .Where(parameter => parameter.Name == "effort" && parameter.IsOptional)
        .ToList();

      effortParameters.Should().HaveCount(7);
      effortParameters.Should().OnlyContain(parameter =>
        object.Equals(parameter.DefaultValue, NestEffortDefaults.Default));
    }

    private static void CaptureClose(SessionState session)
      => session.CaptureApplicationClose(
        0.5,
        0.42,
        "ansi",
        null,
        true,
        false,
        new[] { new SessionSheet { Width = 120, Height = 60 } },
        true,
        5,
        4,
        0.015,
        36);
  }
}
