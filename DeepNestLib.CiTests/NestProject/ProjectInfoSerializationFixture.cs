namespace DeepNestLib.CiTests.NestProject
{
  using System.Text.Json.Nodes;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
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

    [Theory]
    [InlineData(0.010)]
    [InlineData(0.015)]
    [InlineData(0.020)]
    public void ShouldRoundTripTheProjectsCanonicalCommonLineGap(double gapInches)
    {
      var savedConfig = new SvgNestConfig { CommonLineGapInches = gapInches };
      var sut = new ProjectInfo(savedConfig);
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 1));

      string json = sut.ToJson();
      var activeConfig = new SvgNestConfig { CommonLineGapInches = 0.123 };
      ProjectInfo actual = ProjectInfo.FromJson(activeConfig, json);

      json.Should().Contain(nameof(ISvgNestConfig.CommonLineGapInches));
      actual.Config.Should().BeSameAs(activeConfig);
      actual.Config.CommonLineGapInches.Should().BeApproximately(gapInches, 1e-12);
      actual.CommonLineGapInches.Should().BeApproximately(gapInches, 1e-12);
    }

    [Fact]
    public void ConcurrentlyOpenProjectsKeepIndependentCommonLineGapsWhenSwitchedAndSaved()
    {
      string jsonA = new ProjectInfo(new SvgNestConfig { CommonLineGapInches = 0.010 }).ToJson();
      string jsonB = new ProjectInfo(new SvgNestConfig { CommonLineGapInches = 0.020 }).ToJson();
      var runtime = new SvgNestConfig { CommonLineGapInches = 0.123 };

      ProjectInfo projectA = ProjectInfo.FromJson(runtime, jsonA);
      ProjectInfo projectB = ProjectInfo.FromJson(runtime, jsonB);
      runtime.CommonLineGapInches.Should().BeApproximately(0.020, 1e-12);

      // MainWindow_ActiveDocumentChanged performs this assignment when the operator returns to A.
      runtime.CommonLineGapInches = projectA.CommonLineGapInches;
      runtime.CommonLineGapInches.Should().BeApproximately(0.010, 1e-12);
      projectB.CommonLineGapInches.Should().BeApproximately(0.020, 1e-12);

      // Saving either document must use its snapshot, regardless of which value the global config holds.
      runtime.CommonLineGapInches = 0.333;
      ProjectInfo.FromJson(new SvgNestConfig(), projectA.ToJson()).CommonLineGapInches
        .Should().BeApproximately(0.010, 1e-12);
      ProjectInfo.FromJson(new SvgNestConfig(), projectB.ToJson()).CommonLineGapInches
        .Should().BeApproximately(0.020, 1e-12);
    }

    [Fact]
    public void ProjectSavedBeforeCommonLineGapUsesTheCanonicalDefault()
    {
      var sut = new ProjectInfo(new SvgNestConfig { CommonLineGapInches = 0.015 });
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 1));
      var tree = JsonNode.Parse(sut.ToJson());
      var savedConfig = tree[nameof(ProjectInfo.Config)].AsObject();
      savedConfig.Remove(nameof(ISvgNestConfig.CommonLineGapInches)).Should().BeTrue();

      var activeConfig = new SvgNestConfig { CommonLineGapInches = 0.020 };
      ProjectInfo actual = ProjectInfo.FromJson(activeConfig, tree.ToJsonString());

      actual.SheetLoadInfos.Should().ContainSingle();
      actual.Config.CommonLineGapInches.Should().Be(CommonLineDefaults.DefaultGapInches);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.010)]
    public void InvalidSavedCommonLineGapUsesTheCanonicalDefault(double invalidGap)
    {
      var sut = new ProjectInfo(new SvgNestConfig());
      sut.SheetLoadInfos.Add(new SheetLoadInfo(120, 60, 1));
      var tree = JsonNode.Parse(sut.ToJson());
      tree[nameof(ProjectInfo.Config)][nameof(ISvgNestConfig.CommonLineGapInches)] = invalidGap;

      var activeConfig = new SvgNestConfig { CommonLineGapInches = 0.020 };
      ProjectInfo actual = ProjectInfo.FromJson(activeConfig, tree.ToJsonString());

      actual.SheetLoadInfos.Should().ContainSingle();
      actual.Config.CommonLineGapInches.Should().Be(CommonLineDefaults.DefaultGapInches);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(64)]
    [InlineData(500)]
    public void ShouldRoundTripTheProjectsCommonLineClusterPolicy(int limit)
    {
      var sut = new ProjectInfo(new SvgNestConfig { CommonLineMaxClusterParts = limit });

      string json = sut.ToJson();
      var activeConfig = new SvgNestConfig { CommonLineMaxClusterParts = 12 };
      ProjectInfo actual = ProjectInfo.FromJson(activeConfig, json);

      json.Should().Contain(nameof(ISvgNestConfig.CommonLineMaxClusterParts));
      actual.Config.Should().BeSameAs(activeConfig);
      actual.Config.CommonLineMaxClusterParts.Should().Be(limit);
      actual.CommonLineMaxClusterParts.Should().Be(limit);
    }

    [Fact]
    public void ConcurrentProjectsKeepIndependentCommonLineClusterPolicies()
    {
      string jsonA = new ProjectInfo(new SvgNestConfig { CommonLineMaxClusterParts = 20 }).ToJson();
      string jsonB = new ProjectInfo(new SvgNestConfig { CommonLineMaxClusterParts = 80 }).ToJson();
      var runtime = new SvgNestConfig();

      ProjectInfo projectA = ProjectInfo.FromJson(runtime, jsonA);
      ProjectInfo projectB = ProjectInfo.FromJson(runtime, jsonB);
      runtime.CommonLineMaxClusterParts.Should().Be(80);

      runtime.CommonLineMaxClusterParts = projectA.CommonLineMaxClusterParts;
      runtime.CommonLineMaxClusterParts.Should().Be(20);
      projectB.CommonLineMaxClusterParts.Should().Be(80);

      runtime.CommonLineMaxClusterParts = 200;
      ProjectInfo.FromJson(new SvgNestConfig(), projectA.ToJson()).CommonLineMaxClusterParts.Should().Be(20);
      ProjectInfo.FromJson(new SvgNestConfig(), projectB.ToJson()).CommonLineMaxClusterParts.Should().Be(80);
    }

    [Fact]
    public void ProjectSavedBeforeCommonLineClusterLimitUsesAuto()
    {
      var sut = new ProjectInfo(new SvgNestConfig { CommonLineMaxClusterParts = 25 });
      var tree = JsonNode.Parse(sut.ToJson());
      tree[nameof(ProjectInfo.Config)].AsObject()
        .Remove(nameof(ISvgNestConfig.CommonLineMaxClusterParts)).Should().BeTrue();

      var activeConfig = new SvgNestConfig { CommonLineMaxClusterParts = 40 };
      ProjectInfo actual = ProjectInfo.FromJson(activeConfig, tree.ToJsonString());

      actual.Config.CommonLineMaxClusterParts.Should().Be(CommonLineDefaults.DefaultMaxClusterParts);
      actual.CommonLineMaxClusterParts.Should().Be(CommonLineDefaults.DefaultMaxClusterParts);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(501)]
    public void InvalidSavedCommonLineClusterLimitUsesAuto(int invalidLimit)
    {
      var tree = JsonNode.Parse(new ProjectInfo(new SvgNestConfig()).ToJson());
      tree[nameof(ProjectInfo.Config)][nameof(ISvgNestConfig.CommonLineMaxClusterParts)] = invalidLimit;

      var activeConfig = new SvgNestConfig { CommonLineMaxClusterParts = 40 };
      ProjectInfo actual = ProjectInfo.FromJson(activeConfig, tree.ToJsonString());

      actual.Config.CommonLineMaxClusterParts.Should().Be(CommonLineDefaults.DefaultMaxClusterParts);
      actual.CommonLineMaxClusterParts.Should().Be(CommonLineDefaults.DefaultMaxClusterParts);
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
      sut.DetailLoadInfos.Add(new DetailLoadInfo
      {
        Path = "a.dxf",
        CommonCutting = CommonCuttingMode.SamePart,
        DirectCommonCuttingEnabled = true,
      });
      sut.DetailLoadInfos.Add(new DetailLoadInfo { Path = "b.dxf", CommonCutting = CommonCuttingMode.Unrestricted });
      sut.DetailLoadInfos.Add(new DetailLoadInfo { Path = "c.dxf", CommonCutting = CommonCuttingMode.None });

      ProjectInfo actual = ProjectInfo.FromJson(config, sut.ToJson());

      actual.DetailLoadInfos[0].CommonCutting.Should().Be(CommonCuttingMode.SamePart);
      actual.DetailLoadInfos[0].DirectCommonCuttingEnabled.Should().BeTrue();
      actual.DetailLoadInfos[0].EffectiveCommonCutting.Should().Be(CommonCuttingMode.SamePart);
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
