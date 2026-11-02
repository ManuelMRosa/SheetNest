namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  using DeepNestLib.IO;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// Reading a drawing must not slow down with the square of its segments. The parser chained every curve's
  /// chords by asking all the segments left which one came next, so a perforated panel took minutes before the
  /// nest even started: a flat pattern with 1330 holes took 320 s to read, and the same file is read again
  /// for the nest and for the export.
  /// </summary>
  public class DxfParserChainingFixture : IDisposable
  {
    private readonly string tempDir;

    public DxfParserChainingFixture()
    {
      this.tempDir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.tempDir, true);
      }
      catch (IOException)
      {
      }
    }

    /// <summary>A 300 x 180 plate with 540 round holes, about 32,000 segments once the holes are cut into
    /// chords. The old chaining took tens of seconds on it.</summary>
    [Fact]
    public void APlateWithHundredsOfHolesReadsInUnderASecondOrSo()
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2000;
      dxf.Entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(300, 0, 0)));
      dxf.Entities.Add(new DxfLine(new DxfPoint(300, 0, 0), new DxfPoint(300, 180, 0)));
      dxf.Entities.Add(new DxfLine(new DxfPoint(300, 180, 0), new DxfPoint(0, 180, 0)));
      dxf.Entities.Add(new DxfLine(new DxfPoint(0, 180, 0), new DxfPoint(0, 0, 0)));
      for (int i = 0; i < 30; i++)
      {
        for (int j = 0; j < 18; j++)
        {
          dxf.Entities.Add(new DxfCircle(new DxfPoint(5 + (10 * i), 5 + (10 * j), 0), 2));
        }
      }

      string path = Path.Combine(this.tempDir, "perforated.dxf");
      dxf.Save(path);

      var watch = Stopwatch.StartNew();
      var detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      watch.Stop();

      detail.Outers.Should().HaveCount(541, "the plate and every hole close on their own");
      detail.Outers.Count(c => !c.IsChild).Should().Be(1, "the plate is the part, the circles its holes");
      watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "reading a drawing must not grow with the square of its segments");
    }
  }
}
