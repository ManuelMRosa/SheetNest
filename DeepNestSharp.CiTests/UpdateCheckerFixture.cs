namespace DeepNestSharp.CiTests
{
  using System;
  using DeepNestSharp.Domain;
  using FluentAssertions;
  using Xunit;

  public class UpdateCheckerFixture
  {
    [Fact]
    public void TryParseLatest_ReadsTagAndUrl()
    {
      var json = "{\"tag_name\":\"v1.1.4\",\"html_url\":\"https://github.com/ManuelMRosa/SheetNest/releases/tag/v1.1.4\",\"name\":\"SheetNest 1.1.4\"}";

      var result = UpdateChecker.TryParseLatest(json);

      result.Should().NotBeNull();
      result.Value.Latest.Should().Be(new Version(1, 1, 4));
      result.Value.Url.Should().Be("https://github.com/ManuelMRosa/SheetNest/releases/tag/v1.1.4");
    }

    [Fact]
    public void TryParseLatest_FallsBackToReleasesPageWhenNoUrl()
    {
      var result = UpdateChecker.TryParseLatest("{\"tag_name\":\"1.2.0\"}");

      result.Should().NotBeNull();
      result.Value.Latest.Should().Be(new Version(1, 2, 0));
      result.Value.Url.Should().Be(UpdateChecker.ReleasesPageUrl);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"beta\"}")]
    public void TryParseLatest_ReturnsNullOnGarbage(string json)
    {
      UpdateChecker.TryParseLatest(json).Should().BeNull();
    }

    [Fact]
    public void IsNewer_ComparesThreeFields()
    {
      UpdateChecker.IsNewer(new Version(1, 2, 0), new Version(1, 1, 4)).Should().BeTrue();
      UpdateChecker.IsNewer(new Version(1, 1, 4), new Version(1, 1, 4)).Should().BeFalse();
      UpdateChecker.IsNewer(new Version(1, 1, 3), new Version(1, 1, 4)).Should().BeFalse();
      UpdateChecker.IsNewer(new Version(1, 1, 4, 9), new Version(1, 1, 4)).Should().BeFalse("the 4th field is ignored");
    }
  }
}
