namespace DeepNestLib.CiTests.NestProject
{
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
  }
}
