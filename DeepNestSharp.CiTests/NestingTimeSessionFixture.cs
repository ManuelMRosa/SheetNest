namespace DeepNestSharp.CiTests
{
  using System.Linq;
  using System.Reflection;
  using System.Text.Json;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  public class NestingTimeSessionFixture
  {
    /// <summary>
    /// The old nesting time for the whole job is gone. A time limit is now set per sheet
    /// (<see cref="SessionState.NestSecondsPerSheet"/>); the old key must not come back.
    /// </summary>
    [Fact]
    public void ASessionDoesNotStoreANestingTime()
    {
      JsonSerializer.Serialize(new SessionState()).Should().NotContain("NestTime");
    }

    /// <summary>
    /// A session saved by a build that let the operator pick a time, or an effort before that, still opens,
    /// keeps everything else it held, and loses the old choice on the next save.
    /// </summary>
    [Fact]
    public void ASessionThatPickedATimeOrAnEffortStillOpens()
    {
      var old = JsonSerializer.Deserialize<SessionState>("{\"NestTimeSeconds\":300,\"NestEffort\":2,\"AutosaveMinutes\":7}");

      old.Should().NotBeNull();
      old!.AutosaveMinutes.Should().Be(7);
      string saved = JsonSerializer.Serialize(old);
      saved.Should().NotContain("NestTime");
      saved.Should().NotContain("NestEffort");
    }

    /// <summary>
    /// Closing captures the window's own state and must leave the preferences the dialogs own alone.
    /// </summary>
    [Fact]
    public void ClosingKeepsTheDialogPreferences()
    {
      var session = new SessionState
      {
        PreferRectangularOffcut = true,
        OffcutDirection = 3,
        OffcutSpacing = 0.375,
      };

      CaptureClose(session);

      string json = JsonSerializer.Serialize(session);
      var reopened = JsonSerializer.Deserialize<SessionState>(json);

      reopened!.PreferRectangularOffcut.Should().BeTrue("closing must preserve dialog-owned preferences");
      reopened.OffcutDirection.Should().Be(3);
      reopened.OffcutSpacing.Should().Be(0.375);
      reopened.EffectiveCommonLineGapInches.Should().BeApproximately(0.015, 1e-12,
        "closing must retain the physical common-line gap alongside the other dialog preferences");
      reopened.EffectiveCommonLineMaxClusterParts.Should().Be(36,
        "closing must retain the common-line cluster policy alongside the other dialog preferences");
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
