namespace DeepNestSharp.CiTests
{
  using System.Text.Json;
  using DeepNestLib.Placement;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;

  public class CommonLineGapSettingsFixture
  {
    [Theory]
    [InlineData(0.010)]
    [InlineData(0.015)]
    [InlineData(0.020)]
    public void InchesAndMillimetersRoundTripWithoutChangingThePhysicalGap(double inches)
    {
      AdvancedSettingsWindow.CommonLineGapForDisplay(inches, false).Should().BeApproximately(inches, 1e-12);

      double millimeters = AdvancedSettingsWindow.CommonLineGapForDisplay(inches, true);
      millimeters.Should().BeApproximately(inches * CommonLineDefaults.InchesToMillimeters, 1e-12);
      AdvancedSettingsWindow.CommonLineGapFromDisplay(millimeters, true).Should().BeApproximately(inches, 1e-12);
    }

    [Theory]
    [InlineData(0.010)]
    [InlineData(0.015)]
    [InlineData(0.020)]
    public void SessionJsonRoundTripPreservesCanonicalInches(double inches)
    {
      var original = new SessionState { CommonLineGapInches = inches };

      string json = JsonSerializer.Serialize(original);
      json.Should().NotContain(nameof(SessionState.EffectiveCommonLineGapInches));
      var reopened = JsonSerializer.Deserialize<SessionState>(json);

      reopened.Should().NotBeNull();
      reopened!.CommonLineGapInches.Should().Be(inches);
      reopened.EffectiveCommonLineGapInches.Should().BeApproximately(inches, 1e-12);
    }

    [Fact]
    public void SessionWithoutAGapUsesTheSingleShippedDefault()
    {
      var reopened = JsonSerializer.Deserialize<SessionState>("{}");

      reopened.Should().NotBeNull();
      reopened!.CommonLineGapInches.Should().BeNull();
      reopened.EffectiveCommonLineGapInches.Should().Be(CommonLineDefaults.DefaultGapInches);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void InvalidLegacyGapFallsBackToTheSingleShippedDefault(double invalidGap)
    {
      var session = new SessionState { CommonLineGapInches = invalidGap };

      session.EffectiveCommonLineGapInches.Should().Be(CommonLineDefaults.DefaultGapInches);
    }
  }
}
