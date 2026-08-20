namespace DeepNestLib.CiTests.NestProject
{
  using System.Text.Json;
  using DeepNestLib.NestProject;
  using FluentAssertions;
  using Xunit;

  public class DetailLoadInfoSerializationFixture
  {
    [Fact]
    public void ShouldRoundTripMirrorQuantity()
    {
      var sut = new DetailLoadInfo { Path = "x.dxf", MirrorQuantity = 5 };
      var json = sut.ToJson();
      DetailLoadInfo actual = JsonSerializer.Deserialize<DetailLoadInfo>(json);

      actual.MirrorQuantity.Should().Be(5);
      actual.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void ShouldDefaultMirrorQuantityToZeroForOldFiles()
    {
      // Pre-MirrorQuantity .dnest files have no such property — it must default to 0.
      DetailLoadInfo actual = JsonSerializer.Deserialize<DetailLoadInfo>("{\"Path\":\"x.dxf\",\"Quantity\":3}");

      actual.MirrorQuantity.Should().Be(0);
      actual.Quantity.Should().Be(3);
    }

    /// <summary>The colour a user picks for a part has to survive closing and reopening the project.</summary>
    [Fact]
    public void ShouldRoundTripTheChosenPartColour()
    {
      var sut = new DetailLoadInfo { Path = "x.dxf", ColorRgb = 0x1E88E5 };
      var json = sut.ToJson();
      DetailLoadInfo actual = JsonSerializer.Deserialize<DetailLoadInfo>(json);

      actual.ColorRgb.Should().Be(0x1E88E5);
      actual.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void ShouldDefaultPartColourToUnsetForOldFiles()
    {
      // A .dnest saved before part colours existed has no such property — the part must fall back to the
      // colour for its place in the list rather than come back black (0x000000).
      DetailLoadInfo actual = JsonSerializer.Deserialize<DetailLoadInfo>("{\"Path\":\"x.dxf\",\"Quantity\":3}");

      actual.ColorRgb.Should().Be(-1);
    }

    /// <summary>
    /// The canary. Two CLR members map to the JSON name "CommonLine" (the ignored bool shim and the
    /// legacy reader); if System.Text.Json ever counts the ignored one, it throws while BUILDING the
    /// contract and every project on disk stops opening. This test is the first thing to fail if so.
    /// </summary>
    [Fact]
    public void ShouldRoundTripCommonCutting()
    {
      var sut = new DetailLoadInfo { Path = "x.dxf", CommonCutting = CommonCuttingMode.SamePart };
      var json = sut.ToJson();
      DetailLoadInfo actual = JsonSerializer.Deserialize<DetailLoadInfo>(json);

      actual.CommonCutting.Should().Be(CommonCuttingMode.SamePart);
      actual.Should().BeEquivalentTo(sut);
    }

    [Fact]
    public void ShouldReadAnOldCommonLineTrueAsUnrestricted()
    {
      // A .dnest saved before common cutting had modes carries a plain boolean.
      DetailLoadInfo actual = JsonSerializer.Deserialize<DetailLoadInfo>("{\"Path\":\"x.dxf\",\"CommonLine\":true}");

      actual.CommonCutting.Should().Be(CommonCuttingMode.Unrestricted);
    }

    [Fact]
    public void ShouldReadAnOldCommonLineFalseAsNone()
    {
      DetailLoadInfo actual = JsonSerializer.Deserialize<DetailLoadInfo>("{\"Path\":\"x.dxf\",\"CommonLine\":false}");

      actual.CommonCutting.Should().Be(CommonCuttingMode.None);
    }

    [Fact]
    public void ShouldDefaultCommonCuttingToNoneForOldFiles()
    {
      DetailLoadInfo actual = JsonSerializer.Deserialize<DetailLoadInfo>("{\"Path\":\"x.dxf\",\"Quantity\":3}");

      actual.CommonCutting.Should().Be(CommonCuttingMode.None);
    }

    /// <summary>
    /// Never write the legacy boolean back. It is the whole reason a file can only carry one of the two
    /// properties, which in turn is why the field order in the file cannot change the answer.
    /// </summary>
    [Fact]
    public void ShouldNotWriteTheLegacyCommonLineProperty()
    {
      new DetailLoadInfo { Path = "x.dxf", CommonCutting = CommonCuttingMode.SamePart }.ToJson()
        .Should().NotContain("CommonLine");
      new DetailLoadInfo { Path = "x.dxf", CommonCutting = CommonCuttingMode.None }.ToJson()
        .Should().NotContain("CommonLine");
    }

    /// <summary>
    /// A hand-edited file could carry both. Whichever order they appear in, the MODE has to win: the
    /// legacy setter only fills in a mode that is still unset.
    /// </summary>
    [Fact]
    public void ShouldPreferCommonCuttingWhateverTheFieldOrder()
    {
      JsonSerializer.Deserialize<DetailLoadInfo>("{\"CommonLine\":true,\"CommonCutting\":\"SamePart\"}")
        .CommonCutting.Should().Be(CommonCuttingMode.SamePart);
      JsonSerializer.Deserialize<DetailLoadInfo>("{\"CommonCutting\":\"SamePart\",\"CommonLine\":true}")
        .CommonCutting.Should().Be(CommonCuttingMode.SamePart);
    }

    /// <summary>Every caller that still asks the old yes/no question has to get the same answer it did.</summary>
    [Fact]
    public void TheBoolShimStillReflectsTheMode()
    {
      new DetailLoadInfo { CommonCutting = CommonCuttingMode.None }.CommonLine.Should().BeFalse();
      new DetailLoadInfo { CommonCutting = CommonCuttingMode.Unrestricted }.CommonLine.Should().BeTrue();
      new DetailLoadInfo { CommonCutting = CommonCuttingMode.SamePart }.CommonLine.Should().BeTrue();

      new DetailLoadInfo { CommonLine = true }.CommonCutting.Should().Be(CommonCuttingMode.Unrestricted);
      new DetailLoadInfo { CommonLine = false }.CommonCutting.Should().Be(CommonCuttingMode.None);
    }
  }
}
