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
  }
}
