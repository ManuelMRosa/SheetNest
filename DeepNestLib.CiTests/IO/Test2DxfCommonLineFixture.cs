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
  /// Regression coverage for the actual cut geometry in test2.dxf. Its corners are quarter-circle
  /// bulges, not chamfers, and its five circles are holes that must survive a direct common-line cut.
  /// </summary>
  public sealed class Test2DxfCommonLineFixture : IDisposable
  {
    private const double Gap = 0.010D;
    private const double Tolerance = 0.0001D;
    private const double QuarterCircleBulge = -0.4142135623730951D;
    private readonly string tempDirectory;
    private readonly string sourcePath;

    public Test2DxfCommonLineFixture()
    {
      this.tempDirectory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.tempDirectory);
      this.sourcePath = Path.Combine(this.tempDirectory, "test2-minimal.dxf");
      CreateTest2Dxf(this.sourcePath);
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
    public void ExactTest2GeometryImportsAsOneCurvedOuterWithFiveHoles()
    {
      DxfFile source = DxfFile.Load(this.sourcePath);
      DxfLwPolyline sourceOuter = source.Entities.OfType<DxfLwPolyline>().Should().ContainSingle().Subject;
      List<double> sourceBulges = sourceOuter.Vertices.Select(vertex => vertex.Bulge).ToList();

      sourceOuter.IsClosed.Should().BeTrue();
      sourceOuter.Vertices.Should().HaveCount(8);
      sourceBulges.Where(IsNonZero).Should().HaveCount(4);
      sourceBulges.Where(IsNonZero).Should().OnlyContain(bulge =>
        Math.Abs(bulge - QuarterCircleBulge) <= 1e-12);
      source.Entities.OfType<DxfCircle>().Should().HaveCount(5);

      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(this.sourcePath));
      detail.Should().NotBeNull();
      detail.Outers.Should().HaveCount(6, "the LWPOLYLINE is the outside and every CIRCLE is a hole");
      ILocalContour importedOuter = detail.Outers.Should().ContainSingle(contour => !contour.IsChild).Subject;
      importedOuter.CurvedSegments.Should().HaveSameCount(importedOuter.Points);
      importedOuter.CurvedSegments.Should().Contain(isCurved => isCurved);

      detail.TryConvertToNfp(0, out INfp nfp).Should().BeTrue();
      nfp.Children.Should().HaveCount(5);
      nfp.MinX.Should().BeApproximately(0D, Tolerance);
      nfp.MaxX.Should().BeApproximately(7D, Tolerance);
      nfp.MinY.Should().BeApproximately(0D, Tolerance);
      nfp.MaxY.Should().BeApproximately(36D, Tolerance);
      nfp.Should().BeOfType<NoFitPolygon>()
        .Which.GetCurvedSegments().Should().Contain(isCurved => isCurved);
    }

    [Fact]
    public async Task TwoCopiesAtTenThousandthsExportEightBulgesTenHolesAndOneSeam()
    {
      SheetPlacement placement = this.BuildSideBySidePlacement();
      CommonLineGeometryResult geometry = CommonLineGeometryResolver.Resolve(placement);
      CommonLineCluster cluster = geometry.Clusters.Should().ContainSingle().Subject;
      CommonLinePath boundary = cluster.OuterBoundaries.Should().ContainSingle().Subject;

      cluster.PartIds.Should().BeEquivalentTo(new[] { 0, 1 });
      cluster.CommonLines.Should().ContainSingle();
      boundary.IsClosed.Should().BeTrue();
      boundary.Bulges.Should().HaveCount(boundary.Points.Count);
      AssertEightQuarterCircleBulges(boundary.Bulges);

      using var output = new MemoryStream();
      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfFile exported = DxfFile.Load(output);

      List<DxfEntity> innerEntities = exported.Entities
        .Where(entity => entity.Layer == DxfExporter.CutInnerLayer)
        .ToList();
      innerEntities.Should().HaveCount(10, "both copies retain all five original holes");
      innerEntities.Should().OnlyContain(entity => entity is DxfCircle);
      List<DxfCircle> holes = innerEntities.Cast<DxfCircle>().ToList();
      holes.Count(circle => Math.Abs(circle.Radius - 1D) <= Tolerance).Should().Be(2);
      holes.Count(circle => Math.Abs(circle.Radius - 0.125D) <= Tolerance).Should().Be(8);

      DxfEntity outerEntity = exported.Entities
        .Where(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().ContainSingle().Subject;
      DxfLwPolyline outer = outerEntity.Should().BeOfType<DxfLwPolyline>().Subject;
      outer.IsClosed.Should().BeTrue();
      AssertEightQuarterCircleBulges(outer.Vertices.Select(vertex => vertex.Bulge).ToList());

      DxfEntity seamEntity = exported.Entities
        .Where(entity => entity.Layer == DxfExporter.CommonLineLayer)
        .Should().ContainSingle("the two intentional straight sides require one cut only").Subject;
      DxfLine seam = seamEntity.Should().BeOfType<DxfLine>().Subject;
      seam.P1.X.Should().BeApproximately(7D + (Gap / 2D), Tolerance);
      seam.P2.X.Should().BeApproximately(7D + (Gap / 2D), Tolerance);
      Math.Min(seam.P1.Y, seam.P2.Y).Should().BeApproximately(1D, Tolerance);
      Math.Max(seam.P1.Y, seam.P2.Y).Should().BeApproximately(35D, Tolerance);
    }

    private static bool IsNonZero(double value) => Math.Abs(value) > 1e-12;

    private static void AssertEightQuarterCircleBulges(IEnumerable<double> bulges)
    {
      List<double> arcs = bulges.Where(IsNonZero).ToList();
      arcs.Should().HaveCount(8, "each of the two parts contributes its four rounded corners");
      arcs.Should().OnlyContain(bulge =>
        Math.Abs(Math.Abs(bulge) - Math.Abs(QuarterCircleBulge)) <= 1e-12,
        "reversing a boundary changes a bulge's sign but not its quarter-circle geometry");
    }

    private static void CreateTest2Dxf(string path)
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

    private SheetPlacement BuildSideBySidePlacement()
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(this.sourcePath));
      detail.TryConvertToNfp(0, out INfp first).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp second).Should().BeTrue();
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(first) { Id = 0, Source = 0, X = 0D, Y = 0D },
        new PartPlacement(second) { Id = 1, Source = 0, X = 7D + Gap, Y = 0D },
      };

      List<CommonEdge> matches = FindRelationships(parts[0], parts[1]);
      matches.Should().ContainSingle(
        "only the opposing 34-unit straight sides are eligible; tessellated arc chords are not");

      return new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 100, 100),
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: matches,
        commonLineGap: Gap,
        geometryTolerance: Tolerance);
    }

    private static List<CommonEdge> FindRelationships(IPartPlacement first, IPartPlacement second)
    {
      var matches = new List<CommonEdge>();
      for (int a = 0; a < first.Part.Points.Length; a++)
      {
        for (int b = 0; b < second.Part.Points.Length; b++)
        {
          if (CommonLineGeometryResolver.TryCreateCommonEdge(
            first,
            a,
            second,
            b,
            Gap,
            Tolerance,
            out CommonEdge relationship))
          {
            matches.Add(relationship);
          }
        }
      }

      return matches;
    }
  }
}
