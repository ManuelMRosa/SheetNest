namespace DeepNestLib.CiTests.NestProject
{
  using DeepNestLib.NestProject;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Common line only cuts the shared edge once if something on the other end knows the edge is one cut.
  /// A SheetCam round trip does, because the arrangement goes back into that same nest document. A plain
  /// DXF does not, and neither does the DXF exporter, so both parts would be cut in full and the line
  /// between them run twice.
  /// </summary>
  public class CommonLineRuleFixture
  {
    /// <summary>The pair that decides it: the SAME part, asking for the same thing, answered differently
    /// purely by where it came from.</summary>
    [Fact]
    public void TheSameRequestIsAnsweredByWhereThePartCameFrom()
    {
      var fromNest = Part(commonLine: true, nestSource: @"C:\jobs\bracket.nest");
      var plainDxf = Part(commonLine: true, nestSource: string.Empty);

      CommonLineRule.Applies(fromNest).Should().BeTrue();
      CommonLineRule.Applies(plainDxf).Should().BeFalse("nothing downstream of a DXF knows the edge is cut once");
    }

    [Fact]
    public void APartThatDidNotAskForItDoesNotGetIt()
    {
      CommonLineRule.Applies(Part(commonLine: false, nestSource: @"C:\jobs\bracket.nest")).Should().BeFalse();
    }

    [Fact]
    public void APlainPartAsksForNothing()
    {
      CommonLineRule.Applies(Part(commonLine: false, nestSource: string.Empty)).Should().BeFalse();
    }

    /// <summary>A path of nothing but spaces is not a source file. It reaches the model through saved
    /// projects and hand-edited json, so it is worth being explicit about.</summary>
    [Fact]
    public void BlankIsNotASource()
    {
      CommonLineRule.CameFromSheetCamNest(Part(true, "   ")).Should().BeFalse();
      CommonLineRule.Applies(Part(true, "   ")).Should().BeFalse();
    }

    [Fact]
    public void NothingAtAllIsNotAPart()
    {
      CommonLineRule.Applies(null).Should().BeFalse();
      CommonLineRule.CameFromSheetCamNest(null).Should().BeFalse();
    }

    private static IDetailLoadInfo Part(bool commonLine, string nestSource) => new DetailLoadInfo
    {
      Path = @"C:\parts\bracket.dxf",
      CommonLine = commonLine,
      NestSourcePath = nestSource,
    };
  }
}
