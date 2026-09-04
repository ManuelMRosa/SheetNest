namespace DeepNestLib.CiTests.Placement
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

  public sealed class ExactArcCommonLineGeometryFixture : IDisposable
  {
    private const double Gap = 0.01D;
    private const double Tolerance = 0.0001D;
    private readonly string directory;
    private readonly string innerPath;
    private readonly string outerPath;

    public ExactArcCommonLineGeometryFixture()
    {
      this.directory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.directory);
      this.innerPath = Path.Combine(this.directory, "convex-semicircle.dxf");
      this.outerPath = Path.Combine(this.directory, "concave-semicircle.dxf");
      WriteConvexPart(this.innerPath, 5D);
      WriteConcavePart(this.outerPath, 5D + Gap, 8D);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.directory, true);
      }
      catch (IOException)
      {
        // Test cleanup must not mask the geometry assertion.
      }
    }

    [Fact]
    public async Task ConcentricOppositeArcsResolveAndExportExactlyOnce()
    {
      IPartPlacement inner = LoadPlacement(this.innerPath, 0);
      IPartPlacement outer = LoadPlacement(this.outerPath, 1);
      DxfContourCatalog innerCatalog = DxfContourCatalog.Load(this.innerPath);
      DxfContourCatalog outerCatalog = DxfContourCatalog.Load(this.outerPath);
      DxfSourceContour innerContour = innerCatalog.Contours.Single(contour => !contour.IsChild);
      DxfSourceContour outerContour = outerCatalog.Contours.Single(contour => !contour.IsChild);
      int innerArc = innerContour.Primitives.Single(primitive =>
        primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index;
      int outerArc = outerContour.Primitives.Single(primitive =>
        primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index;
      CommonBoundarySpan innerSpan = DxfCommonLineSpanResolver.CreateSpan(
        innerCatalog, innerContour.Index, innerArc);
      CommonBoundarySpan outerSpan = DxfCommonLineSpanResolver.CreateSpan(
        outerCatalog, outerContour.Index, outerArc);
      CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(
        inner, innerSpan, outer, outerSpan, Gap, Tolerance);
      var sheet = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 20, 20),
        new[] { inner, outer },
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { edge },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);

      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(sheet)
        .Clusters.Should().ContainSingle().Subject;

      cluster.OuterBoundaries.Should().ContainSingle(path => path.IsClosed);
      cluster.CommonLines.Should().BeEmpty("a circular seam must never be downgraded to its chord");
      CommonCenterCurve curve = cluster.CommonCurves.Should().ContainSingle().Subject;
      curve.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      curve.Radius.Should().BeApproximately(5D + (Gap / 2D), Tolerance);
      Math.Abs(curve.SweepRadians).Should().BeApproximately(Math.PI, 1e-10D);

      SheetPlacement reloaded = SheetPlacement.FromJson(sheet.ToJson());
      CommonCenterCurve reloadedCurve = CommonLineGeometryResolver.Resolve(reloaded)
        .Clusters.Single().CommonCurves.Single();
      reloadedCurve.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      reloadedCurve.Radius.Should().BeApproximately(curve.Radius, Tolerance);

      using var stream = new MemoryStream();
      await sheet.ExportDxf(stream, mergeLines: false, differentiateChildren: false);
      stream.Position = 0;
      DxfFile exported = DxfFile.Load(stream);
      exported.Entities.Where(entity => entity.Layer == DxfExporter.CommonLineLayer)
        .Should().ContainSingle().Which.Should().BeOfType<DxfLwPolyline>()
        .Which.IsClosed.Should().BeFalse();
      exported.Entities.Where(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().ContainSingle().Which.Should().BeOfType<DxfLwPolyline>()
        .Which.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task ExactlyOneMirroredArcRevalidatesAndExportsAsOneExactSeam()
    {
      IPartPlacement innerSource = LoadPlacement(this.innerPath, 0);
      IPartPlacement inner = new PartPlacement(innerSource.Part.MirrorX().Rotate(180D))
      {
        Id = 0,
        Source = 0,
        IsMirrored = true,
        Rotation = 180D,
      };
      IPartPlacement outer = LoadPlacement(this.outerPath, 1);
      DxfContourCatalog innerCatalog = DxfContourCatalog.Load(this.innerPath);
      DxfContourCatalog outerCatalog = DxfContourCatalog.Load(this.outerPath);
      DxfSourceContour innerContour = innerCatalog.Contours.Single(contour => !contour.IsChild);
      DxfSourceContour outerContour = outerCatalog.Contours.Single(contour => !contour.IsChild);
      CommonBoundarySpan innerSpan = DxfCommonLineSpanResolver.CreateSpan(
        innerCatalog,
        innerContour.Index,
        innerContour.Primitives.Single(primitive =>
          primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index);
      CommonBoundarySpan outerSpan = DxfCommonLineSpanResolver.CreateSpan(
        outerCatalog,
        outerContour.Index,
        outerContour.Primitives.Single(primitive =>
          primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index);
      CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(
        inner,
        innerSpan,
        outer,
        outerSpan,
        Gap,
        Tolerance);
      var sheet = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 20, 20),
        new[] { inner, outer },
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { edge },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);

      SheetPlacement reloaded = SheetPlacement.FromJson(sheet.ToJson());
      reloaded.PartPlacements.Single(part => part.Id == inner.Id).IsMirrored.Should().BeTrue();
      CommonCenterCurve curve = CommonLineGeometryResolver.Resolve(reloaded)
        .Clusters.Single().CommonCurves.Single();
      curve.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      curve.Radius.Should().BeApproximately(5D + (Gap / 2D), Tolerance);

      using var stream = new MemoryStream();
      await reloaded.ExportDxf(stream, mergeLines: false, differentiateChildren: false);
      stream.Position = 0;
      DxfFile exported = DxfFile.Load(stream);
      exported.Entities.Where(entity => entity.Layer == DxfExporter.CommonLineLayer)
        .Should().ContainSingle().Which.Should().BeOfType<DxfLwPolyline>()
        .Which.IsClosed.Should().BeFalse();
      exported.Entities.Where(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().ContainSingle().Which.Should().BeOfType<DxfLwPolyline>()
        .Which.IsClosed.Should().BeTrue();
    }

    [Fact]
    public void ArcCandidateProposesExactCenterAlignmentWithoutMutatingThePlacement()
    {
      IPartPlacement inner = LoadPlacement(this.innerPath, 0);
      IPartPlacement outer = LoadPlacement(this.outerPath, 1);
      outer.X = 0.375D;
      outer.Y = -0.125D;

      DxfCommonLineArcCandidate candidate = DxfCommonLineArcCandidateFinder.Find(
        inner,
        outer,
        Gap,
        Tolerance).Should().ContainSingle().Subject;

      candidate.TranslationX.Should().BeApproximately(-0.375D, Tolerance);
      candidate.TranslationY.Should().BeApproximately(0.125D, Tolerance);
      candidate.CenterCurve.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      outer.X.Should().Be(0.375D, "candidate discovery must be transactional");
      outer.Y.Should().Be(-0.125D);

      outer.X += candidate.TranslationX;
      outer.Y += candidate.TranslationY;
      Action create = () => CommonLineGeometryResolver.CreateCommonEdge(
        inner,
        candidate.SpanA,
        outer,
        candidate.SpanB,
        Gap,
        Tolerance);
      create.Should().NotThrow();
    }

    [Fact]
    public void ArcCandidateKeepsOneExactSpanAcrossTheAngularBranchCut()
    {
      IPartPlacement innerSource = LoadPlacement(this.innerPath, 0);
      IPartPlacement outerSource = LoadPlacement(this.outerPath, 1);
      const double rotation = 37.125D;
      IPartPlacement inner = new PartPlacement(innerSource.Part.Rotate(rotation))
      {
        Id = 0,
        Rotation = rotation,
        Source = 0,
      };
      IPartPlacement outer = new PartPlacement(outerSource.Part.Rotate(rotation))
      {
        Id = 1,
        Rotation = rotation,
        Source = 1,
        X = 0.20D,
      };

      DxfCommonLineArcCandidate candidate = DxfCommonLineArcCandidateFinder.Find(
        inner,
        outer,
        Gap,
        Tolerance).Should().ContainSingle(
          "the overlap crossing 0/2pi is one continuous source interval").Subject;

      using (new FluentAssertions.Execution.AssertionScope())
      {
        candidate.SpanA.StartParameter.Should().BeApproximately(0D, 1e-9D);
        candidate.SpanA.EndParameter.Should().BeApproximately(1D, 1e-9D);
        candidate.SpanB.StartParameter.Should().BeApproximately(0D, 1e-9D);
        candidate.SpanB.EndParameter.Should().BeApproximately(1D, 1e-9D);
        candidate.SharedLength.Should().BeApproximately(Math.PI * 5D, Tolerance);
      }

      outer.X += candidate.TranslationX;
      outer.Y += candidate.TranslationY;
      Action create = () => CommonLineGeometryResolver.CreateCommonEdge(
        inner,
        candidate.SpanA,
        outer,
        candidate.SpanB,
        Gap,
        Tolerance);
      create.Should().NotThrow();
    }

    [Fact]
    public async Task ExplodedLineAndArcSourcesRemainExactThroughExport()
    {
      string explodedInnerPath = Path.Combine(this.directory, "exploded-convex.dxf");
      string explodedOuterPath = Path.Combine(this.directory, "exploded-concave.dxf");
      WriteExplodedConvexPart(explodedInnerPath, 5D);
      WriteExplodedConcavePart(explodedOuterPath, 5D + Gap, 8D);
      IPartPlacement inner = LoadPlacement(explodedInnerPath, 0);
      IPartPlacement outer = LoadPlacement(explodedOuterPath, 1);
      DxfContourCatalog innerCatalog = DxfContourCatalog.Load(explodedInnerPath);
      DxfContourCatalog outerCatalog = DxfContourCatalog.Load(explodedOuterPath);
      DxfSourceContour innerContour = innerCatalog.Contours.Single(contour => !contour.IsChild);
      DxfSourceContour outerContour = outerCatalog.Contours.Single(contour => !contour.IsChild);
      CommonBoundarySpan innerSpan = DxfCommonLineSpanResolver.CreateSpan(
        innerCatalog,
        innerContour.Index,
        innerContour.Primitives.Single(primitive =>
          primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index);
      CommonBoundarySpan outerSpan = DxfCommonLineSpanResolver.CreateSpan(
        outerCatalog,
        outerContour.Index,
        outerContour.Primitives.Single(primitive =>
          primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index);
      CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(
        inner, innerSpan, outer, outerSpan, Gap, Tolerance);
      var sheet = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 20, 20),
        new[] { inner, outer },
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { edge },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);

      using var stream = new MemoryStream();
      await sheet.ExportDxf(stream, mergeLines: false, differentiateChildren: false);
      stream.Position = 0;
      DxfFile exported = DxfFile.Load(stream);

      exported.Entities.Where(entity => entity.Layer == DxfExporter.CommonLineLayer)
        .Should().ContainSingle().Which.Should().BeOfType<DxfLwPolyline>()
        .Which.IsClosed.Should().BeFalse();
      exported.Entities.Where(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().ContainSingle().Which.Should().BeOfType<DxfLwPolyline>()
        .Which.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task EditedStraightSourceRejectsPersistedParametricCommonLineExport()
    {
      string firstPath = Path.Combine(this.directory, "straight-a.dxf");
      string secondPath = Path.Combine(this.directory, "straight-b.dxf");
      WriteExplodedRectangle(firstPath, 10D, 5D);
      WriteExplodedRectangle(secondPath, 10D, 5D);
      IPartPlacement first = LoadPlacement(firstPath, 0);
      IPartPlacement second = LoadPlacement(secondPath, 1);
      second.X = 10D + Gap;
      int right = FindVerticalEdge(first, 10D);
      int left = FindVerticalEdge(second, 10D + Gap);
      CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(
        first, right, 0D, 1D, second, left, 0D, 1D, Gap, Tolerance);
      var original = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 30, 20),
        new[] { first, second },
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { edge },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);
      SheetPlacement reloaded = SheetPlacement.FromJson(original.ToJson());

      WriteExplodedRectangle(firstPath, 11D, 5D);
      File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddSeconds(2D));

      Func<Task> export = async () =>
      {
        using var stream = new MemoryStream();
        await reloaded.ExportDxf(stream, mergeLines: false, differentiateChildren: false);
      };
      await export.Should().ThrowAsync<CommonLineValidationException>()
        .WithMessage("*fingerprint*stale*");
    }

    private static IPartPlacement LoadPlacement(string path, int id)
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      detail.TryConvertToNfp(id, out INfp part).Should().BeTrue();
      return new PartPlacement(part) { Id = id, Source = id };
    }

    private static void WriteConvexPart(string path, double radius)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2007;
      dxf.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = -radius, Bulge = 1D },
        new DxfLwPolylineVertex { X = 0D, Y = radius },
        new DxfLwPolylineVertex { X = -2D, Y = radius },
        new DxfLwPolylineVertex { X = -2D, Y = -radius },
      })
      {
        IsClosed = true,
        Layer = "L0",
      });
      dxf.Save(path, true);
    }

    private static void WriteConcavePart(string path, double radius, double farX)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2007;
      dxf.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = -radius },
        new DxfLwPolylineVertex { X = farX, Y = -radius },
        new DxfLwPolylineVertex { X = farX, Y = radius },
        new DxfLwPolylineVertex { X = 0D, Y = radius, Bulge = -1D },
      })
      {
        IsClosed = true,
        Layer = "L0",
      });
      dxf.Save(path, true);
    }

    private static void WriteExplodedConvexPart(string path, double radius)
    {
      var entities = new DxfEntity[]
      {
        new DxfArc(new DxfPoint(0D, 0D, 0D), radius, 270D, 90D),
        Line(0D, radius, -2D, radius),
        Line(-2D, radius, -2D, -radius),
        Line(-2D, -radius, 0D, -radius),
      };
      WriteEntities(path, entities);
    }

    private static void WriteExplodedConcavePart(string path, double radius, double farX)
    {
      var entities = new DxfEntity[]
      {
        Line(0D, -radius, farX, -radius),
        Line(farX, -radius, farX, radius),
        Line(farX, radius, 0D, radius),
        new DxfArc(new DxfPoint(0D, 0D, 0D), radius, 270D, 90D),
      };
      WriteEntities(path, entities);
    }

    private static DxfLine Line(double x1, double y1, double x2, double y2)
      => new DxfLine(new DxfPoint(x1, y1, 0D), new DxfPoint(x2, y2, 0D));

    private static void WriteExplodedRectangle(string path, double width, double height)
      => WriteEntities(path, new DxfEntity[]
      {
        Line(0D, 0D, width, 0D),
        Line(width, 0D, width, height),
        Line(width, height, 0D, height),
        Line(0D, height, 0D, 0D),
      });

    private static int FindVerticalEdge(IPartPlacement placement, double worldX)
    {
      SvgPoint[] points = placement.Part.Points;
      for (int i = 0; i < points.Length; i++)
      {
        SvgPoint start = points[i];
        SvgPoint end = points[(i + 1) % points.Length];
        if (Math.Abs((start.X + placement.X) - worldX) <= Tolerance
          && Math.Abs((end.X + placement.X) - worldX) <= Tolerance)
        {
          return i;
        }
      }

      throw new InvalidOperationException("Expected a vertical source edge at the requested X coordinate.");
    }

    private static void WriteEntities(string path, IEnumerable<DxfEntity> entities)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2007;
      foreach (DxfEntity entity in entities)
      {
        entity.Layer = "L0";
        dxf.Entities.Add(entity);
      }

      dxf.Save(path, true);
    }
  }
}
