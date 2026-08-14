namespace DeepNestLib.CiTests.NestProject
{
  using System.Text.Json.Nodes;
  using DeepNestLib.NestProject;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Common cutting needs a kerf, and the kerf is measured off the leads of a SheetCam nest file, so a
  /// part that did not come from one has nothing to share. These say the part answers that for itself
  /// instead of every caller having to remember to ask where it came from.
  /// </summary>
  public class EffectiveCommonCuttingFixture
  {
    [Theory]
    [InlineData(CommonCuttingMode.Unrestricted)]
    [InlineData(CommonCuttingMode.SamePart)]
    public void APartThatDidNotComeFromANestSharesNothing(CommonCuttingMode stored)
    {
      var sut = new DetailLoadInfo { Path = "a.dxf", CommonCutting = stored };

      sut.NestSourcePath.Should().BeEmpty("a plain DXF is what this is about");
      sut.EffectiveCommonCutting.Should().Be(CommonCuttingMode.None);
    }

    /// <summary>
    /// Declining to act on the setting is not the same as deleting it. Opening a project must not rewrite
    /// what the user saved, so the stored value has to survive being ignored.
    /// </summary>
    [Fact]
    public void IgnoringTheSettingDoesNotEraseIt()
    {
      var sut = new DetailLoadInfo { Path = "a.dxf", CommonCutting = CommonCuttingMode.Unrestricted };

      sut.EffectiveCommonCutting.Should().Be(CommonCuttingMode.None);
      sut.CommonCutting.Should().Be(CommonCuttingMode.Unrestricted, "the file said so and we have no business changing it");
    }

    /// <summary>
    /// The one that stops this becoming a blanket off switch: with a nest behind it, the part shares
    /// exactly what it was told to.
    /// </summary>
    [Theory]
    [InlineData(CommonCuttingMode.Unrestricted)]
    [InlineData(CommonCuttingMode.SamePart)]
    [InlineData(CommonCuttingMode.None)]
    public void APartFromANestKeepsTheModeItWasGiven(CommonCuttingMode stored)
    {
      var sut = new DetailLoadInfo
      {
        Path = "a.dxf",
        CommonCutting = stored,
        NestSourcePath = @"C:\Jobs\job.nest",
        NestPartName = "Bracket",
      };

      sut.EffectiveCommonCutting.Should().Be(stored);
    }

    /// <summary>
    /// Where this actually bites. Before common cutting was restricted to nest jobs the setting was a
    /// plain checkbox offered for ANY part, so a 1.1.7 project can carry it on a plain DXF, and there is
    /// no longer a control to turn it off. The migration still reads the old boolean; what changes is that
    /// nothing acts on it.
    /// </summary>
    [Fact]
    public void APreModeProjectDoesNotCommonCutAPlainDxf()
    {
      var config = SvgNest.Config;
      var sut = new ProjectInfo(config);
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 1));
      sut.DetailLoadInfos.Add(new DetailLoadInfo { Path = "a.dxf" });

      // Rewrite the saved project the way the pre-mode build wrote it: a plain boolean, no mode. Edit the
      // JSON TREE, not the text — ProjectInfo.ToJson writes indented, so a string replace silently matches
      // nothing and the test passes while proving nothing.
      var tree = JsonNode.Parse(sut.ToJson());
      var part = tree["DetailLoadInfos"].AsArray()[0].AsObject();
      part.Remove("CommonCutting");
      part["CommonLine"] = true;
      string legacyJson = tree.ToJsonString();
      legacyJson.Should().Contain("CommonLine").And.NotContain("CommonCutting");

      ProjectInfo actual = ProjectInfo.FromJson(config, legacyJson);

      actual.DetailLoadInfos[0].CommonCutting.Should().Be(CommonCuttingMode.Unrestricted, "the migration still reads it");
      actual.DetailLoadInfos[0].EffectiveCommonCutting.Should().Be(CommonCuttingMode.None, "but nothing acts on it any more");
    }
  }
}
