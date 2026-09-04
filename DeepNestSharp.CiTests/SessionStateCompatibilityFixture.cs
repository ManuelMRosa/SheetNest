namespace DeepNestSharp.CiTests
{
  using System.Text.Json;
  using FluentAssertions;
  using Xunit;

  public class SessionStateCompatibilityFixture
  {
    [Fact]
    public void ASessionFromTheEditableToleranceBuildDropsTheRetiredCalibration()
    {
      const string legacyJson = "{\"NestEffort\":1,\"CommonCutting\":{\"AngleToleranceDeg\":0.5}}";

      var session = JsonSerializer.Deserialize<SessionState>(legacyJson);

      session.Should().NotBeNull();
      session.NestEffort.Should().Be(1);
      session.EffectiveCommonLineGapInches.Should().Be(DeepNestLib.Placement.CommonLineDefaults.DefaultGapInches);
      JsonSerializer.Serialize(session).Should().NotContain("\"CommonCutting\"");
    }
  }
}
