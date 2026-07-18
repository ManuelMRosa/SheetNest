namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.IO;
  using DeepNestLib;
  using DeepNestLib.IO;
  using FakeItEasy;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Regression for GitHub issue #1 ("No valid parts to nest"): a DXF whose file extension is
  /// upper-cased (e.g. PART.DXF, common from CAD/CAM exporters) must load for nesting exactly like a
  /// lower-cased one. The thumbnail path already lower-cased before matching, so the part previewed
  /// but nesting silently dropped it — LoadRawDetail's extension check was case-sensitive.
  /// </summary>
  public class LoadRawDetailExtensionCaseFixture : IDisposable
  {
    private static readonly DxfGenerator DxfGenerator = new DxfGenerator();
    private readonly string tempDir;

    public LoadRawDetailExtensionCaseFixture()
    {
      tempDir = Path.Combine(Path.GetTempPath(), "SheetNestExtCase_" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(tempDir, true);
      }
      catch
      {
        // Best-effort cleanup; a locked temp file must not fail the test run.
      }
    }

    [Theory]
    [InlineData("part.dxf")]
    [InlineData("part.DXF")]
    [InlineData("part.Dxf")]
    public void GivenExtensionCaseWhenLoadRawDetailThenLoadsNestableNfp(string fileName)
    {
      var path = Path.Combine(tempDir, fileName);
      DxfGenerator.GenerateRectangle(11D, 7D, RectangleType.FileLoad, isClosed: true).Save(path, asText: true);

      var det = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));

      det.Should().NotBeNull("an upper- or lower-cased .dxf extension must both load for nesting");
      det.TryConvertToNfp(A.Dummy<int>(), out INfp nfp).Should().BeTrue();
      nfp.Points.Length.Should().BeGreaterThan(2);
    }
  }
}
