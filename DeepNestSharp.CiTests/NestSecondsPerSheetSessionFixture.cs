namespace DeepNestSharp.CiTests
{
  using System.Text.Json;
  using FluentAssertions;
  using Xunit;

  /// <summary>The time per sheet set in Advanced Settings, as the session keeps it.</summary>
  public class NestSecondsPerSheetSessionFixture
  {
    /// <summary>A new install, and a session saved before the option existed, both nest at two seconds a sheet.</summary>
    [Fact]
    public void ASessionThatNeverSavedATimeGetsTwoSecondsASheet()
    {
      var old = JsonSerializer.Deserialize<SessionState>("{\"AutosaveMinutes\":7}");

      old!.EffectiveNestSecondsPerSheet.Should().Be(2);
      new SessionState().EffectiveNestSecondsPerSheet.Should().Be(2);
    }

    [Theory]
    [InlineData(0, 0)] // no limit stays no limit
    [InlineData(2, 2)]
    [InlineData(30, 30)]
    [InlineData(600, 600)]
    [InlineData(601, 2)] // a hand-edited value out of range is the default
    [InlineData(-5, 2)]
    public void ASavedTimeIsReadBack(int saved, int expected)
    {
      string json = JsonSerializer.Serialize(new SessionState { NestSecondsPerSheet = saved });

      JsonSerializer.Deserialize<SessionState>(json)!.EffectiveNestSecondsPerSheet.Should().Be(expected);
    }

    /// <summary>Advanced Settings writes the time; closing the window must not put it back.</summary>
    [Fact]
    public void ClosingKeepsTheTime()
    {
      var session = new SessionState { NestSecondsPerSheet = 0 };

      session.CaptureApplicationClose(0.5, 0.42, "ansi", null, true, false, new[] { new SessionSheet { Width = 120, Height = 60 } }, true, 5, 4, 0.015, 36);

      JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(session))!.EffectiveNestSecondsPerSheet.Should().Be(0);
    }
  }
}
