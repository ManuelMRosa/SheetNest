namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// A rectangular-offcut nest ends with one real extra cut: the straight line that frees the clean
  /// leftover rectangle. The exporter must emit it as ordinary geometry when an <see cref="OffcutLine"/>
  /// is supplied — and change nothing when it is not.
  /// </summary>
  public class DxfExportOffcutLineFixture : IDisposable
  {
    private readonly string tempDir;

    public DxfExportOffcutLineFixture()
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
        // Leftover temp files must never fail the suite.
      }
    }

    [Fact]
    public async Task OffcutLineIsWrittenAsOrdinaryGeometry()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement();
      var offcut = new[] { new OffcutLine { X1 = 40.5, Y1 = 0, X2 = 40.5, Y2 = 48 } };

      await new DxfExporter().Export(outPath, sheetPlacement, false, false, offcut);

      var lines = DxfFile.Load(outPath).Entities.OfType<DxfLine>()
        .Where(l => l.P1.X == 40.5 && l.P2.X == 40.5)
        .ToList();
      lines.Should().HaveCount(1, "the separation cut is one straight line at the offcut boundary");
      Math.Min(lines[0].P1.Y, lines[0].P2.Y).Should().Be(0, "the cut spans the full short dimension");
      Math.Max(lines[0].P1.Y, lines[0].P2.Y).Should().Be(48);
    }

    [Fact]
    public async Task BothDirectionWritesTwoGuillotineCuts()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement();

      // L-shaped leftover: the long axis's cut runs edge to edge, the short axis's cut stops at it.
      var offcut = new[]
      {
        new OffcutLine { X1 = 40.5, Y1 = 0, X2 = 40.5, Y2 = 48 },
        new OffcutLine { X1 = 0, Y1 = 20.25, X2 = 40.5, Y2 = 20.25 },
      };

      await new DxfExporter().Export(outPath, sheetPlacement, false, false, offcut);

      var lines = DxfFile.Load(outPath).Entities.OfType<DxfLine>().ToList();
      lines.Should().Contain(l => l.P1.X == 40.5 && l.P2.X == 40.5 && Math.Max(l.P1.Y, l.P2.Y) == 48, "the vertical cut spans the full height");
      lines.Should().Contain(l => l.P1.Y == 20.25 && l.P2.Y == 20.25 && Math.Max(l.P1.X, l.P2.X) == 40.5, "the horizontal cut stops at the vertical one");
    }

    [Fact]
    public async Task NoOffcutLineLeavesTheExportUntouched()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement();
      string withoutPath = Path.Combine(this.tempDir, "without.dxf");

      await new DxfExporter().Export(outPath, sheetPlacement, false, false, new[] { new OffcutLine { X1 = 40.5, Y1 = 0, X2 = 40.5, Y2 = 48 } });
      await new DxfExporter().Export(withoutPath, sheetPlacement, false, false);

      var with = DxfFile.Load(outPath).Entities.Count;
      var without = DxfFile.Load(withoutPath).Entities.Count;
      without.Should().Be(with - 1, "omitting the offcut line is the only difference");
    }

    /// <summary>One 10 × 5 rectangle placed on a 96 × 48 sheet, backed by a real DXF on disk (the
    /// exporter reloads originals by <c>Part.Name</c> for precision).</summary>
    private (ISheetPlacement SheetPlacement, string OutPath) BuildPlacement()
    {
      string partPath = Path.Combine(this.tempDir, "rect.dxf");
      var dxf = new DxfFile();
      dxf.Entities.Add(new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(0, 0, 0)),
        new DxfVertex(new DxfPoint(10, 0, 0)),
        new DxfVertex(new DxfPoint(10, 5, 0)),
        new DxfVertex(new DxfPoint(0, 5, 0)),
        new DxfVertex(new DxfPoint(0, 0, 0)),
      }) { IsClosed = true });
      dxf.Save(partPath);

      var det = new NestExecutionHelper().LoadRawDetail(new FileInfo(partPath));
      det.TryConvertToNfp(0, out INfp nfp).Should().BeTrue();

      var sheet = Sheet.NewSheet(1, 96, 48);
      var placements = new List<IPartPlacement>
      {
        new PartPlacement(nfp) { X = 2, Y = 3, Rotation = 0, Id = 0, Source = 0 },
      };

      var sheetPlacement = new SheetPlacement(PlacementTypeEnum.BoundingBox, sheet, placements, 0, SvgNest.Config.ClipperScale);
      return (sheetPlacement, Path.Combine(this.tempDir, "out.dxf"));
    }
  }
}
