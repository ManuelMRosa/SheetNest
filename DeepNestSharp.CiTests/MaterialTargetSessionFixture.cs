namespace DeepNestSharp.CiTests
{
  using System.Text.Json;
  using FluentAssertions;
  using Xunit;

  /// <summary>The material usage target set in Advanced Settings, as the session keeps it.</summary>
  public class MaterialTargetSessionFixture
  {
    /// <summary>A new install, and a session saved before the target existed, both start at 90 %.</summary>
    [Fact]
    public void ASessionThatNeverSavedATargetGetsTheDefault()
    {
      var old = JsonSerializer.Deserialize<SessionState>("{\"AutosaveMinutes\":7}");

      old!.EffectiveMaterialTargetPercent.Should().Be(90);
      new SessionState().EffectiveMaterialTargetPercent.Should().Be(90);
    }

    [Theory]
    [InlineData(0, 0)] // off stays off
    [InlineData(75, 75)]
    [InlineData(100, 100)]
    [InlineData(150, 90)] // a hand-edited value out of range is the default
    [InlineData(-5, 90)]
    public void ASavedTargetIsReadBack(int saved, int expected)
    {
      string json = JsonSerializer.Serialize(new SessionState { MaterialTargetPercent = saved });

      JsonSerializer.Deserialize<SessionState>(json)!.EffectiveMaterialTargetPercent.Should().Be(expected);
    }

    /// <summary>Advanced Settings writes the target; closing the window must not put it back.</summary>
    [Fact]
    public void ClosingKeepsTheTarget()
    {
      var session = new SessionState { MaterialTargetPercent = 0 };

      session.CaptureApplicationClose(0.5, 0.42, "ansi", null, true, false, new[] { new SessionSheet { Width = 120, Height = 60 } }, true, 5, 4, 0.015, 36);

      JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(session))!.EffectiveMaterialTargetPercent.Should().Be(0);
    }
  }
}
