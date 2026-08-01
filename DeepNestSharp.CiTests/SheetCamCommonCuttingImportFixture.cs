namespace DeepNestSharp.CiTests
{
  using System.IO;
  using DeepNestLib.IO;
  using DeepNestLib.NestProject;
  using DeepNestSharp.Ui.Views;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// A SheetCam .nest has no common-cutting field. It signals the whole job with PartSpacing="0", which
  /// says the parts share their cuts and nothing at all about WHICH parts may share with which.
  /// </summary>
  public class SheetCamCommonCuttingImportFixture
  {
    [Theory]
    [InlineData("0", CommonCuttingMode.Unrestricted)]
    [InlineData("0.25", CommonCuttingMode.None)]
    public void AZeroPartSpacingJobImportsAsUnrestricted(string partSpacing, CommonCuttingMode expected)
    {
      // Never Same part: the file does not say that, and inventing it would refuse seams between different
      // parts that SheetCam laid out expecting them, packing the sheet looser than the job asked for.
      var nest = Load(partSpacing);
      nest.Should().NotBeNull();
      MainWindow.CommonCuttingOf(nest).Should().Be(expected);
    }

    private static SheetCamNestFile Load(string partSpacing)
    {
      string xml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<NestingData ID=""SheetCam01"" Version=""1.0"">
  <Settings Units=""in"" PartSpacing=""{partSpacing}"" EdgeSpacing=""0.25"" />
  <Sheets>
    <Sheet Name=""Sheet"" Quantity=""1"">
      <External>
        <Point X=""0"" Y=""0"" /><Point X=""3048"" Y=""0"" /><Point X=""3048"" Y=""1524"" /><Point X=""0"" Y=""1524"" />
      </External>
    </Sheet>
  </Sheets>
  <Parts>
    <Part Name=""bar"">
      <Positions><Position X=""0"" Y=""0"" A=""0"" Sheet=""0"" /></Positions>
      <External>
        <Point X=""-10"" Y=""-20"" /><Point X=""10"" Y=""-20"" /><Point X=""10"" Y=""20"" /><Point X=""-10"" Y=""20"" />
      </External>
    </Part>
  </Parts>
</NestingData>";

      string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".nest");
      File.WriteAllText(path, xml, new System.Text.UTF8Encoding(true));
      try
      {
        return SheetCamNestFile.Load(path);
      }
      finally
      {
        File.Delete(path);
      }
    }
  }
}
