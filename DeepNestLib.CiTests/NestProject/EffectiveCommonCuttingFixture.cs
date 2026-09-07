namespace DeepNestLib.CiTests.NestProject
{
  using System.Text.Json.Nodes;
  using DeepNestLib.NestProject;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Common cutting is only active on a DXF/STEP part whose mode the operator has explicitly confirmed,
  /// and it then uses exact contour contact and merged DXF lines. These say the part answers which
  /// contract is active instead of every caller having to reconstruct that choice.
  /// </summary>
  public class EffectiveCommonCuttingFixture
  {
    [Theory]
    [InlineData(CommonCuttingMode.Unrestricted)]
    [InlineData(CommonCuttingMode.SamePart)]
    public void AnUnconfirmedDirectPartSharesNothing(CommonCuttingMode stored)
    {
      var sut = new DetailLoadInfo { Path = "a.dxf", CommonCutting = stored };

      sut.EffectiveCommonCutting.Should().Be(CommonCuttingMode.None);
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
