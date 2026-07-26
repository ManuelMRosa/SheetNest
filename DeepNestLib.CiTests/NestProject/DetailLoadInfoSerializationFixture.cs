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
  }
}
