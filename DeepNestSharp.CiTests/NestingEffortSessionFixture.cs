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
    [InlineData(-1, (int)NestEffort.Fast)]
    [InlineData(0, (int)NestEffort.Fast)]
    [InlineData(1, (int)NestEffort.Normal)]
    [InlineData(2, (int)NestEffort.Best)]
    [InlineData(99, (int)NestEffort.Fast)]
    public void AnUnsetInvalidOrSavedSessionResolvesAgainstTheFastDefault(int saved, int expected)
    {
      NestEffortDefaults.FromPersisted(saved).Should().Be((NestEffort)expected);
    }

    [Theory]
    [InlineData((int)NestEffort.Fast)]
    [InlineData((int)NestEffort.Normal)]
    [InlineData((int)NestEffort.Best)]
    public void ClosingAndReopeningRetainsTheChosenEffortAndOtherPreferences(int chosenValue)
    {
      var chosen = (NestEffort)chosenValue;
      var session = new SessionState
      {
        NestEffort = (int)NestEffort.Fast,
        PreferRectangularOffcut = true,
        OffcutDirection = 3,
        OffcutSpacing = 0.375,
      };

      session.CaptureApplicationClose(
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
        chosen,
        0.015,
        36);

      string json = JsonSerializer.Serialize(session);
      var reopened = JsonSerializer.Deserialize<SessionState>(json);

      NestEffortDefaults.FromPersisted(reopened.NestEffort).Should().Be(chosen);
      reopened.PreferRectangularOffcut.Should().BeTrue("closing must preserve dialog-owned preferences");
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
  }
}
