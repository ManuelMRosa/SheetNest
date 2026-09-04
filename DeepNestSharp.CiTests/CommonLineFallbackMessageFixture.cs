namespace DeepNestSharp.CiTests
{
  using DeepNestSharp.RasterNest;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;

  public class CommonLineFallbackMessageFixture
  {
    [Theory]
    [InlineData("Part 1 has curved outside geometry; V1 cannot rebuild it.")]
    [InlineData("Curved common-line outside geometry must be one closed planar LWPOLYLINE.")]
    public void EveryUnsupportedCurvedOutsideMessageIsClassifiedAsAFormatLimitation(string validationMessage)
    {
      CommonLineFallbackNotice.ReasonFromValidation(validationMessage)
        .Should().Be(CommonLineFallbackNotice.ContourFormatReason);
    }

    [Fact]
    public void MessageIsConciseDeduplicatedByDrawingAndStatesNormalGapWasKept()
    {
      string message = MainWindow.CommonLineFallbackMessage(new[]
      {
        new CommonLineFallbackNotice("curved-part", CommonLineFallbackNotice.ContourFormatReason),
        new CommonLineFallbackNotice("curved-part", CommonLineFallbackNotice.ContourFormatReason),
        new CommonLineFallbackNotice("bad-cluster", CommonLineFallbackNotice.TopologyReason),
      });

      message.Should().Contain("curved-part — " + CommonLineFallbackNotice.ContourFormatReason);
      message.Should().Contain("bad-cluster — " + CommonLineFallbackNotice.TopologyReason);
      message.Split("curved-part").Should().HaveCount(2, "one drawing should occupy one diagnostic row");
      message.Should().Contain("kept the normal part gap");
    }
  }
}
