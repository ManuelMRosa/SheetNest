namespace DeepNestSharp.CiTests
{
  using System;
  using System.Text.Json;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;

  public class CommonLineGapSettingsFixture
  {
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(37, 37)]
    [InlineData(500, 500)]
    [InlineData(501, 0)]
    public void CommonLineClusterLimitAcceptsAutoOrTwoThroughFiveHundred(int saved, int expected)
    {
      CommonLineDefaults.NormalizeMaxClusterParts(saved).Should().Be(expected);
      AdvancedSettingsWindow.NormalizeCommonLineMaxClusterParts(saved).Should().Be(expected);
    }

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

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(48)]
    [InlineData(500)]
    public void SessionJsonRoundTripPreservesTheCommonLineClusterPolicy(int limit)
    {
      var original = new SessionState { CommonLineMaxClusterParts = limit };

      string json = JsonSerializer.Serialize(original);
      json.Should().NotContain(nameof(SessionState.EffectiveCommonLineMaxClusterParts));
      var reopened = JsonSerializer.Deserialize<SessionState>(json);

      reopened.Should().NotBeNull();
      reopened!.CommonLineMaxClusterParts.Should().Be(limit);
      reopened.EffectiveCommonLineMaxClusterParts.Should().Be(limit);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(501)]
    public void InvalidSessionClusterLimitsFallBackToAuto(int invalidLimit)
    {
      var session = new SessionState { CommonLineMaxClusterParts = invalidLimit };

      session.EffectiveCommonLineMaxClusterParts.Should().Be(CommonLineDefaults.DefaultMaxClusterParts);
    }

    [Theory]
    [InlineData(double.NaN, 0.0001D, "Common Line Gap")]
    [InlineData(0D, 0.0001D, "Common Line Gap")]
    [InlineData(0.010D, double.NaN, "GeometryTolerance")]
    [InlineData(0.010D, 0D, "GeometryTolerance")]
    [InlineData(0.010D, 0.010D, "GeometryTolerance")]
    public void DirectNesterRejectsInvalidActiveUnitDistances(
      double gap,
      double tolerance,
      string expectedSetting)
    {
      INestResult result = SparrowNestService.Nest(
        Array.Empty<RasterPartInfo>(),
        new[] { (Win: 100, Hin: 100, Qty: 1, Unlimited: false) },
        rotations: 4,
        spacing: 0.25D,
        margin: 0D,
        perSheetBudgetSec: 1,
        sparrowExePath: "not-used-for-invalid-settings",
        error: out string error,
        commonLineGap: gap,
        geometryTolerance: tolerance);

      result.Should().BeNull();
      error.Should().Contain(expectedSetting);
    }

    [Fact]
    public void OpeningAndClosingAProjectDoesNotAdoptItsCommonLineSettingsForNewJobs()
    {
      var savedSession = new SessionState
      {
        CommonLineGapInches = 0.015D,
        CommonLineMaxClusterParts = 36,
      };
      (double preferredGap, int preferredLimit) = MainWindow.CommonLinePreferencesFrom(savedSession);
      var runtime = new SvgNestConfig
      {
        CommonLineGapInches = preferredGap,
        CommonLineMaxClusterParts = preferredLimit,
      };
      string foreignJson = new ProjectInfo(new SvgNestConfig
      {
        CommonLineGapInches = 0.020D,
        CommonLineMaxClusterParts = 80,
      }).ToJson();

      ProjectInfo opened = ProjectInfo.FromJson(runtime, foreignJson);
      opened.CommonLineGapInches.Should().BeApproximately(0.020D, 1e-12D);
      opened.CommonLineMaxClusterParts.Should().Be(80);
      runtime.CommonLineGapInches.Should().BeApproximately(0.020D, 1e-12D,
        "the active document must still drive the current nest");
      runtime.CommonLineMaxClusterParts.Should().Be(80);

      var closedSession = new SessionState();
      closedSession.CaptureApplicationClose(
        0D,
        0D,
        null,
        null,
        false,
        false,
        Array.Empty<SessionSheet>(),
        true,
        5,
        4,
        preferredGap,
        preferredLimit);
      closedSession.EffectiveCommonLineGapInches.Should().BeApproximately(0.015D, 1e-12D,
        "closing an opened document must persist the operator preference, not that document's snapshot");
      closedSession.EffectiveCommonLineMaxClusterParts.Should().Be(36);

      var nextProject = new ProjectInfo(runtime);
      MainWindow.ApplyCommonLinePreferencesToNewProject(nextProject, preferredGap, preferredLimit);
      nextProject.CommonLineGapInches.Should().BeApproximately(0.015D, 1e-12D,
        "a new project in the same process must not inherit the previously active foreign document");
      nextProject.CommonLineMaxClusterParts.Should().Be(36);
    }
  }
}
