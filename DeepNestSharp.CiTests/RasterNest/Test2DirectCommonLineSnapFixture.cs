namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using FluentAssertions.Execution;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// Exercises the production direct-snap entry point with the actual curved test2.dxf topology.
  /// The only selectable common edge is straight; its four rounded corners must not make the planner
  /// abandon the otherwise-valid relationship.
  /// </summary>
  public sealed class Test2DirectCommonLineSnapFixture : IDisposable
  {
    private const double NormalPartGap = 0.30D;
    private const double CommonLineGap = 0.010D;
    private const double GeometryTolerance = 0.0001D;
    private const double QuarterCircleBulge = -0.4142135623730951D;
    private readonly string tempDirectory;
    private readonly string sourcePath;

    public Test2DirectCommonLineSnapFixture()
    {
      this.tempDirectory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.tempDirectory);
      this.sourcePath = Path.Combine(this.tempDirectory, "test2-minimal.dxf");
      CreateExactTest2Dxf(this.sourcePath);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.tempDirectory, true);
      }
      catch (IOException)
      {
        // Test cleanup must not hide the actual assertion result.
      }
    }

    [Fact]
    public void CurvedTest2CopiesSnapFromNormalSpacingToOneDirectSeam()
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(this.sourcePath));
      detail.TryConvertToNfp(0, out INfp first).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp second).Should().BeTrue();
      first.Children.Should().HaveCount(5);
      second.Children.Should().HaveCount(5);

      var placements = new List<IPartPlacement>
      {
        new PartPlacement(first) { Source = 0, X = 1D, Y = 1D },
        new PartPlacement(second) { Source = 0, X = 1D + 7D + NormalPartGap, Y = 1D },
      };
      HorizontalGap(placements[0], placements[1]).Should().BeApproximately(NormalPartGap, 1e-9);
      var rules = new Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)>
      {
        [0] = (CommonCuttingMode.Unrestricted, 0, "direct", true, NormalPartGap),
      };

      SparrowNestService.DirectCommonLineSnapResult snapped = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        rules,
        sheetW: 40D,
        sheetH: 50D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      using (new AssertionScope())
      {
        HorizontalGap(placements[0], placements[1]).Should().BeApproximately(CommonLineGap, 1e-8);
        snapped.Edges.Should().ContainSingle();
        snapped.Fallbacks.Should().BeEmpty();
        snapped.GivenUp.Should().Be(0);
      }

      SparrowNestService.CommonEdgeIntent intent = snapped.Edges.Single();
      CommonEdge record = CommonLineGeometryResolver.CreateCommonEdge(
        intent.PartA,
        intent.EdgeAIndex,
        intent.EdgeAStart,
        intent.EdgeAEnd,
        intent.PartB,
        intent.EdgeBIndex,
        intent.EdgeBStart,
        intent.EdgeBEnd,
        CommonLineGap,
        GeometryTolerance);
      var sheet = new SheetPlacement(
        placementType: PlacementTypeEnum.BoundingBox,
        sheet: Sheet.NewSheet(0, 40, 50),
        partPlacements: placements,
        mergedLength: 0D,
        requiresMergedLines: true,
        commonEdges: new[] { record },
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(sheet)
        .Clusters.Should().ContainSingle().Subject;
      CommonLineSegment seam = cluster.CommonLines.Should().ContainSingle().Subject;
      seam.Start.X.Should().BeApproximately(8D + (CommonLineGap / 2D), GeometryTolerance);
      seam.End.X.Should().BeApproximately(8D + (CommonLineGap / 2D), GeometryTolerance);
      Math.Abs(seam.End.Y - seam.Start.Y).Should().BeApproximately(34D, GeometryTolerance);
    }

    private static double HorizontalGap(IPartPlacement first, IPartPlacement second)
      => second.PlacedPart.MinX - first.PlacedPart.MaxX;

    private static void CreateExactTest2Dxf(string path)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2000;
      dxf.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 7D, Y = 35D },
        new DxfLwPolylineVertex { X = 7D, Y = 1D, Bulge = QuarterCircleBulge },
        new DxfLwPolylineVertex { X = 6D, Y = 0D },
        new DxfLwPolylineVertex { X = 1D, Y = 0D, Bulge = QuarterCircleBulge },
        new DxfLwPolylineVertex { X = 0D, Y = 1D },
        new DxfLwPolylineVertex { X = 0D, Y = 35D, Bulge = QuarterCircleBulge },
        new DxfLwPolylineVertex { X = 1D, Y = 36D },
        new DxfLwPolylineVertex { X = 6D, Y = 36D, Bulge = QuarterCircleBulge },
      })
      {
        IsClosed = true,
      });
      dxf.Entities.Add(new DxfCircle(new DxfPoint(3.5D, 33D, 0D), 1D));
      dxf.Entities.Add(new DxfCircle(new DxfPoint(2.5D, 34D, 0D), 0.125D));
      dxf.Entities.Add(new DxfCircle(new DxfPoint(4.5D, 34D, 0D), 0.125D));
      dxf.Entities.Add(new DxfCircle(new DxfPoint(4.5D, 32D, 0D), 0.125D));
      dxf.Entities.Add(new DxfCircle(new DxfPoint(2.5D, 32D, 0D), 0.125D));
      dxf.Save(path, true);
    }
  }
}
