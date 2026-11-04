namespace DeepNestSharp.CiTests
{
  using System.IO;
  using DeepNestSharp.Ui.Services;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// A save dialog told to open on a drive that has been pulled out throws instead of opening; at the
  /// laser that crashed SheetNest while it asked whether to save on closing.
  /// </summary>
  public class FileDialogFolderFixture
  {
    [Fact]
    public void AFolderOnADriveThatIsGoneIsNotOfferedToTheDialog()
    {
      FileIoService.UsableFolder(@"Q:\2026.10.05\alu").Should().BeEmpty();
      FileIoService.UsableFolder(null).Should().BeEmpty();
      FileIoService.UsableFolder("  ").Should().BeEmpty();
    }

    [Fact]
    public void AFolderThatIsThereIsKept()
    {
      string here = Path.GetTempPath();
      FileIoService.UsableFolder(here).Should().Be(here);
    }
  }
}
