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
  /// A sheet produced with direct common-line geometry owns its export requirement. Callers may still
  /// request merging globally, but cannot accidentally turn it off for that particular layout.
  /// </summary>
  public class RequiredMergedLinesExportFixture : IDisposable
  {
    private readonly string tempDir;

    public RequiredMergedLinesExportFixture()
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
    public async Task PlacementRequirementOverridesAFalseExportArgument()
    {
      var optional = this.BuildPlacement(requiresMergedLines: false);
      var required = this.BuildPlacement(requiresMergedLines: true);

      var optionalLines = await ExportLines(optional);
      var requiredLines = await ExportLines(required);
      double optionalLength = TotalLineLength(optionalLines);
      double requiredLength = TotalLineLength(requiredLines);

      optionalLength.Should().BeApproximately(60, 1e-6, "both 10 by 5 contours are present before merging");
      requiredLength.Should().BeApproximately(55, 1e-6, "the shared five-unit edge is emitted only once");
      optionalLines.Count(IsSharedEdge).Should().Be(2);
      requiredLines.Count(IsSharedEdge).Should().Be(1, "the common edge must not be cut twice");
      requiredLines.Should().HaveCountLessThan(optionalLines.Count, "collinear outer edges are safely coalesced too");
    }

    private static bool IsSharedEdge(DxfLine line)
      => Math.Abs(line.P1.X - 12) < 1e-6
        && Math.Abs(line.P2.X - 12) < 1e-6
        && Math.Abs(Math.Abs(line.P2.Y - line.P1.Y) - 5) < 1e-6;

    private static double TotalLineLength(IEnumerable<DxfLine> lines)
      => lines.Sum(line => Math.Sqrt(
        ((line.P2.X - line.P1.X) * (line.P2.X - line.P1.X))
        + ((line.P2.Y - line.P1.Y) * (line.P2.Y - line.P1.Y))));

    private static async Task<IReadOnlyList<DxfLine>> ExportLines(SheetPlacement placement)
    {
      using var output = new MemoryStream();
      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;

      return new DxfLineMerger().SplitLines(DxfFile.Load(output).Entities).OfType<DxfLine>().ToList();
    }

    private SheetPlacement BuildPlacement(bool requiresMergedLines)
    {
      string partPath = Path.Combine(this.tempDir, "rect.dxf");
      if (!File.Exists(partPath))
      {
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
      }

      var detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(partPath));
      detail.TryConvertToNfp(0, out INfp nfp).Should().BeTrue();

      var placements = new List<IPartPlacement>
      {
        new PartPlacement(nfp) { X = 2, Y = 3, Rotation = 0, Id = 0, Source = 0 },
        new PartPlacement(nfp) { X = 12, Y = 3, Rotation = 0, Id = 1, Source = 0 },
      };

      return new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(1, 96, 48),
        placements,
        0,
        SvgNest.Config.ClipperScale,
        requiresMergedLines);
    }
  }
}
