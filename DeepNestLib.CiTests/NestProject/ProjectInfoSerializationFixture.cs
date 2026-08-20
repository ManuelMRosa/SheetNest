namespace DeepNestLib.CiTests.NestProject
{
  using System.Text.Json.Nodes;
  using DeepNestLib.NestProject;
  using FluentAssertions;
  using Xunit;

  public class ProjectInfoSerializationFixture
  {
    [Fact]
    public void ShouldRoundTripSerialize()
    {
      var config = SvgNest.Config;
      var sut = new ProjectInfo(config);
      sut.SheetLoadInfos.Should().BeEmpty(); // new projects start with no stock
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 1));
      var json = sut.ToJson();
      ProjectInfo actual = ProjectInfo.FromJson(config, json);

      actual.Should().BeEquivalentTo(sut);
    }

    /// <summary>
    /// The part-level round-trip is tested on its own, but the path the app actually takes runs through
    /// DetailLoadInfoJsonConverter and WrappableListJsonConverter. A mode that survives one and not the
    /// other would look fine in unit tests and lose the user's setting on every save.
    /// </summary>
    [Fact]
    public void ShouldRoundTripCommonCuttingThroughTheWholeProject()
    {
      var config = SvgNest.Config;
      var sut = new ProjectInfo(config);
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 1));
      sut.DetailLoadInfos.Add(new DetailLoadInfo { Path = "a.dxf", CommonCutting = CommonCuttingMode.SamePart });
      sut.DetailLoadInfos.Add(new DetailLoadInfo { Path = "b.dxf", CommonCutting = CommonCuttingMode.Unrestricted });
      sut.DetailLoadInfos.Add(new DetailLoadInfo { Path = "c.dxf", CommonCutting = CommonCuttingMode.None });

      ProjectInfo actual = ProjectInfo.FromJson(config, sut.ToJson());

      actual.DetailLoadInfos[0].CommonCutting.Should().Be(CommonCuttingMode.SamePart);
      actual.DetailLoadInfos[1].CommonCutting.Should().Be(CommonCuttingMode.Unrestricted);
      actual.DetailLoadInfos[2].CommonCutting.Should().Be(CommonCuttingMode.None);
    }

    /// <summary>
    /// A setting that gets withdrawn leaves projects behind that still carry it, and those have to open
    /// as if it had never been there. Nobody asserts this anywhere else, and it is the sort of thing that
    /// only breaks once a serializer option gets tightened years later.
    /// </summary>
    [Fact]
    public void ShouldStillOpenAProjectCarryingSettingsThisBuildNoLongerHas()
    {
      var config = SvgNest.Config;
      var sut = new ProjectInfo(config);
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 3));
      sut.DetailLoadInfos.Add(new DetailLoadInfo { Path = "a.dxf", Quantity = 7 });

      // Put the withdrawn kerf back into the saved file, the way a build from that fortnight wrote it.
      // The JSON TREE, not the text: ToJson writes indented, so a string replace matches nothing and the
      // test would pass while proving nothing.
      var tree = JsonNode.Parse(sut.ToJson());
      tree["KerfMm"] = 0.55;
      tree["DetailLoadInfos"].AsArray()[0].AsObject()["KerfMm"] = 0.9;
      string json = tree.ToJsonString();
      json.Should().Contain("KerfMm");

      ProjectInfo actual = ProjectInfo.FromJson(config, json);

      // The CONTENT, not just non-null: FromJson swallows a failure and hands back an empty project, so
      // anything weaker than this would pass with the deserialization blown up underneath it.
      actual.DetailLoadInfos.Should().HaveCount(1);
      actual.DetailLoadInfos[0].Path.Should().Be("a.dxf");
      actual.DetailLoadInfos[0].Quantity.Should().Be(7);
      actual.SheetLoadInfos.Should().HaveCount(1);
      actual.SheetLoadInfos[0].Quantity.Should().Be(3);
    }

    /// <summary>
    /// An unlimited size has to survive a save. Its Quantity is asserted alongside on purpose: the whole
    /// point of the flag is that the number is IGNORED rather than thrown away, so a save that quietly
    /// zeroed it would still be wrong even with the flag intact.
    /// </summary>
    [Fact]
    public void ShouldRoundTripAnUnlimitedSheet()
    {
      var config = SvgNest.Config;
      var sut = new ProjectInfo(config);
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 4) { Unlimited = true });
      sut.SheetLoadInfos.Add(new SheetLoadInfo(48, 24, 3));

      ProjectInfo actual = ProjectInfo.FromJson(config, sut.ToJson());

      actual.SheetLoadInfos.Should().HaveCount(2);
      actual.SheetLoadInfos[0].Unlimited.Should().BeTrue();
      actual.SheetLoadInfos[0].Quantity.Should().Be(4);
      actual.SheetLoadInfos[1].Unlimited.Should().BeFalse();
      actual.SheetLoadInfos[1].Quantity.Should().Be(3);
    }

    /// <summary>
    /// Every .dnest written before the flag existed has to open as counted stock, which is what those
    /// projects meant. This is the reason Unlimited is a settable property and not another argument to
    /// SheetLoadInfo's [JsonConstructor]: a constructor parameter has nothing to bind to here.
    /// </summary>
    [Fact]
    public void ShouldReadASheetSavedBeforeUnlimitedExistedAsCounted()
    {
      var config = SvgNest.Config;
      var sut = new ProjectInfo(config);
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 5));

      // Strip the field the way a build from before it wrote the file. The JSON TREE, not the text:
      // ToJson writes indented, so a string replace matches nothing and the test proves nothing.
      var tree = JsonNode.Parse(sut.ToJson());
      var sheet = tree["SheetLoadInfos"].AsArray()[0].AsObject();
      sheet.ContainsKey("Unlimited").Should().BeTrue("the field must be there to be worth removing");
      sheet.Remove("Unlimited");
      string legacyJson = tree.ToJsonString();
      legacyJson.Should().NotContain("Unlimited");

      ProjectInfo actual = ProjectInfo.FromJson(config, legacyJson);

      // The CONTENT, not just non-null: FromJson swallows a failure and hands back an empty project, so
      // asserting only "Unlimited is false" would pass with the deserialization blown up underneath it.
      actual.SheetLoadInfos.Should().HaveCount(1);
      actual.SheetLoadInfos[0].Width.Should().Be(120);
      actual.SheetLoadInfos[0].Quantity.Should().Be(5);
      actual.SheetLoadInfos[0].Unlimited.Should().BeFalse();
    }

    /// <summary>A project saved by 1.1.7 has to come back with its common line still on.</summary>
    [Fact]
    public void ShouldReadAPreModeProjectAsUnrestricted()
    {
      var config = SvgNest.Config;
      var sut = new ProjectInfo(config);
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 1));
      sut.DetailLoadInfos.Add(new DetailLoadInfo { Path = "a.dxf" });

      // Rewrite the saved project the way the pre-mode build wrote it: a plain boolean, no mode. Edit
      // the JSON TREE, not the text — ProjectInfo.ToJson writes indented, so a string replace silently
      // matches nothing and the test passes while proving nothing.
      var tree = JsonNode.Parse(sut.ToJson());
      var part = tree["DetailLoadInfos"].AsArray()[0].AsObject();
      part.Remove("CommonCutting");
      part["CommonLine"] = true;
      string legacyJson = tree.ToJsonString();

      // Fail loudly if the rewrite above ever stops rewriting anything.
      legacyJson.Should().Contain("CommonLine").And.NotContain("CommonCutting");

      ProjectInfo actual = ProjectInfo.FromJson(config, legacyJson);

      actual.DetailLoadInfos[0].CommonCutting.Should().Be(CommonCuttingMode.Unrestricted);
    }
  }
}
