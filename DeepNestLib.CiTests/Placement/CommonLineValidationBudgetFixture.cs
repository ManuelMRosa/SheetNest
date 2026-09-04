namespace DeepNestLib.CiTests.Placement
{
  using System;
  using System.IO;
  using System.Linq;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  public sealed class CommonLineValidationBudgetFixture : IDisposable
  {
    private const double Gap = 0.01D;
    private const double Tolerance = 0.0001D;
    private const double Width = 23000D;
    private const double Height = 2200D;
    private readonly string directory;
    private readonly string sourcePath;

    public CommonLineValidationBudgetFixture()
    {
      this.directory = Path.Combine(
        Path.GetTempPath(),
        "SheetNestTests",
        Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.directory);
      this.sourcePath = Path.Combine(this.directory, "aggregate-arc-budget.dxf");
      WriteSource(this.sourcePath);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.directory, recursive: true);
      }
      catch (IOException)
      {
        // Test cleanup must not hide the geometry-safety assertion.
      }
      catch (UnauthorizedAccessException)
      {
        // A transient file handle on Windows must not hide the geometry-safety assertion.
      }
    }

    [Fact]
    public void ExistingExactLineArcDxfOverAggregateSampleBudgetIsRejectedWithoutNfpFallback()
    {
      File.Exists(this.sourcePath).Should().BeTrue();
      DxfContourCatalog catalog = DxfContourCatalog.Load(this.sourcePath, Tolerance);
      catalog.Contours.Should().ContainSingle(contour => !contour.IsChild);
      catalog.Contours.Where(contour => contour.IsChild).Should().HaveCount(10);
      catalog.Contours.SelectMany(contour => contour.Primitives).Should().OnlyContain(primitive =>
        primitive.GeometryKind == CommonLineGeometryKind.Line
        || primitive.GeometryKind == CommonLineGeometryKind.CircularArc);

      NoFitPolygon leftGeometry = Rectangle();
      NoFitPolygon rightGeometry = Rectangle();
      leftGeometry.Name = this.sourcePath;
      rightGeometry.Name = this.sourcePath;
      var left = new PartPlacement(leftGeometry) { Id = 0 };
      var right = new PartPlacement(rightGeometry) { Id = 1, X = Width + Gap };
      CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(
        left,
        1,
        right,
        3,
        Gap,
        Tolerance);
      var placement = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, (int)((Width * 2D) + Gap + 1D), (int)(Height + 1D)),
        new IPartPlacement[] { left, right },
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { edge },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);

      Action resolve = () => CommonLineGeometryResolver.Resolve(placement);

      resolve.Should().Throw<CommonLineValidationException>()
        .WithMessage("*aggregate validation sample budget*",
          "the exact DXF has ten high-radius circular holes while its four-point NFP has none");
    }

    private static NoFitPolygon Rectangle()
      => new NoFitPolygon(new[]
      {
        new SvgPoint(0D, 0D),
        new SvgPoint(Width, 0D),
        new SvgPoint(Width, Height),
        new SvgPoint(0D, Height),
      });

    private static void WriteSource(string path)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2007;
      Add(dxf, new DxfLine(new DxfPoint(0D, 0D, 0D), new DxfPoint(Width, 0D, 0D)));
      Add(dxf, new DxfLine(new DxfPoint(Width, 0D, 0D), new DxfPoint(Width, Height, 0D)));
      Add(dxf, new DxfLine(new DxfPoint(Width, Height, 0D), new DxfPoint(0D, Height, 0D)));
      Add(dxf, new DxfLine(new DxfPoint(0D, Height, 0D), new DxfPoint(0D, 0D, 0D)));

      const double radius = 1000D;
      for (int index = 0; index < 10; index++)
      {
        Add(dxf, new DxfCircle(
          new DxfPoint(1100D + (index * 2200D), Height / 2D, 0D),
          radius));
      }

      dxf.Save(path, true);
    }

    private static void Add(DxfFile file, DxfEntity entity)
    {
      entity.Layer = "L0";
      file.Entities.Add(entity);
    }
  }
}
