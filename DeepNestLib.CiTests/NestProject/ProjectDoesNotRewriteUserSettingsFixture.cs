namespace DeepNestLib.CiTests.NestProject
{
  using System.Text.Json.Nodes;
  using DeepNestLib.NestProject;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Opening somebody's project must not rewrite your own preferences.
  /// <para>Reported on issue #2: a setting "always toggles back to ON". It does, and not because the
  /// checkbox is broken. ProjectInfo.FromJson ends by copying the .dnest's whole config block over the
  /// app-wide one, and every property on that config persists itself in its setter, so each project open
  /// re-saves whatever the file happened to carry as if the operator had chosen it. The shipped default
  /// is on, so it came back on, every time, for ever.</para>
  /// <para>The project's values must still APPLY, or a job would reopen nesting differently from the way
  /// it was saved. Applying and persisting are different things, and only the second one is the bug.</para>
  /// </summary>
  public class ProjectDoesNotRewriteUserSettingsFixture
  {
    [Fact]
    public void OpeningAProjectLeavesTheSavedPreferenceAlone()
    {
      var config = SvgNest.Config;
      bool original = config.MergeLines;

      try
      {
        // What the operator chose.
        config.MergeLines = false;

        // A project that disagrees, the way any .dnest saved with the default on does.
        string json = ProjectWithMergeLines(true);
        json.Should().Contain("MergeLines", "the file has to carry it for this to prove anything");

        ProjectInfo.FromJson(config, json);

        SvgNest.Config.MergeLines.Should().BeFalse(
          "a project's settings are the job's, not a rewrite of what this operator prefers");
      }
      finally
      {
        config.MergeLines = original;
      }
    }

    /// <summary>The other half, and the one that is easy to break while fixing the first: the job still
    /// has to nest the way it was saved.</summary>
    [Fact]
    public void TheProjectStillNestsWithItsOwnSettings()
    {
      var config = SvgNest.Config;
      double originalSpacing = config.Spacing;

      try
      {
        config.Spacing = 0.125;

        var tree = JsonNode.Parse(ProjectWithMergeLines(true));
        tree["Config"]["Spacing"] = 9.0;

        var project = ProjectInfo.FromJson(config, tree.ToJsonString());

        project.Config.Spacing.Should().Be(9.0,
          "the job carries the spacing it was nested with, whatever this machine prefers");
      }
      finally
      {
        config.Spacing = originalSpacing;
      }
    }

    private static string ProjectWithMergeLines(bool mergeLines)
    {
      var config = SvgNest.Config;
      bool original = config.MergeLines;
      try
      {
        config.MergeLines = mergeLines;
        var sut = new ProjectInfo(config);
        sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 1));
        return sut.ToJson();
      }
      finally
      {
        config.MergeLines = original;
      }
    }
  }
}
