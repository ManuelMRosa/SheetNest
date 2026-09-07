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

  public class CommonLineDxfExportFixture : IDisposable
  {
    private const double Gap = 0.01D;
    private const double Tolerance = 0.0001D;
    private readonly string tempDirectory;
    private readonly string sourcePath;

    public CommonLineDxfExportFixture()
    {
      this.tempDirectory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.tempDirectory);
      this.sourcePath = Path.Combine(this.tempDirectory, "rectangle-with-hole.dxf");

      var source = new DxfFile();
      source.Entities.Add(ClosedRectangle(0D, 0D, 10D, 5D));
      source.Entities.Add(ClosedRectangle(2D, 1D, 2D, 1D));
      source.Save(this.sourcePath, true);
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
    public async Task CommonLineExportUsesThreeRealLayersAndSuppressesBothOriginalOuters()
    {
      SheetPlacement placement = this.BuildPlacement();
      using var output = new MemoryStream();

      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfFile actual = DxfFile.Load(output);

      actual.Layers.Select(layer => layer.Name).Should().Contain(new[]
      {
        DxfExporter.CutInnerLayer,
        DxfExporter.CommonLineLayer,
        DxfExporter.CutOuterLayer,
      });

      List<DxfEntity> inner = actual.Entities.Where(entity => entity.Layer == DxfExporter.CutInnerLayer).ToList();
      List<DxfEntity> common = actual.Entities.Where(entity => entity.Layer == DxfExporter.CommonLineLayer).ToList();
      List<DxfEntity> outer = actual.Entities.Where(entity => entity.Layer == DxfExporter.CutOuterLayer).ToList();
      inner.Should().HaveCount(2, "each part hole remains an independent closed CUT_INNER contour");
      inner.Should().OnlyContain(entity => IsClosed(entity));
      outer.Should().ContainSingle("the cluster replaces both original outside contours");
      outer.Single().Should().Match<DxfEntity>(entity => IsClosed(entity));
      common.Should().ContainSingle(entity => entity is DxfLine);

      var seam = (DxfLine)common.Single();
      seam.P1.X.Should().BeApproximately(10D + (Gap / 2D), Tolerance);
      seam.P2.X.Should().BeApproximately(10D + (Gap / 2D), Tolerance);

      List<DxfLine> outerSegments = new DxfLineMerger().SplitLines(outer).OfType<DxfLine>().ToList();
      outerSegments.Should().NotContain(line =>
        Math.Abs(line.P1.X - seam.P1.X) <= Tolerance
        && Math.Abs(line.P2.X - seam.P2.X) <= Tolerance,
        "no original outside edge may remain underneath COMMON_LINE");
    }

    [Fact]
    public async Task ClusterVoidFromFacingCShapesExportsAsClosedCutInner()
    {
      string leftPath = Path.Combine(this.tempDirectory, "left-c.dxf");
      string rightPath = Path.Combine(this.tempDirectory, "right-c.dxf");
      WriteClosedPolyline(leftPath, new[]
      {
        (0D, 0D), (5D, 0D), (5D, 2D), (2D, 2D),
        (2D, 8D), (5D, 8D), (5D, 10D), (0D, 10D),
      });
      WriteClosedPolyline(rightPath, new[]
      {
        (0D, 0D), (5D, 0D), (5D, 10D), (0D, 10D),
        (0D, 8D), (3D, 8D), (3D, 2D), (0D, 2D),
      });
      IRawDetail leftDetail = new NestExecutionHelper().LoadRawDetail(new FileInfo(leftPath));
      IRawDetail rightDetail = new NestExecutionHelper().LoadRawDetail(new FileInfo(rightPath));
      leftDetail.TryConvertToNfp(0, out INfp left).Should().BeTrue();
      rightDetail.TryConvertToNfp(1, out INfp right).Should().BeTrue();
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(left) { Id = 0, Source = 0 },
        new PartPlacement(right) { Id = 1, Source = 1, X = 5D + Gap },
      };
      var relationships = new List<CommonEdge>();
      for (int a = 0; a < left.Points.Length; a++)
      {
        for (int b = 0; b < right.Points.Length; b++)
        {
          if (CommonLineGeometryResolver.TryCreateCommonEdge(
            parts[0], a, parts[1], b, Gap, Tolerance, out CommonEdge relationship))
          {
            relationships.Add(relationship);
          }
        }
      }

      relationships.Should().HaveCount(2, "the facing C profiles share their upper and lower bar ends");
      var placement = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 100, 100),
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: relationships,
        commonLineGap: Gap,
        geometryTolerance: Tolerance);
      using var output = new MemoryStream();

      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfFile actual = DxfFile.Load(output);

      actual.Entities.Where(entity => entity.Layer == DxfExporter.CutInnerLayer)
        .Should().ContainSingle(entity => IsClosed(entity),
          "the enclosed cluster void must be a closed CUT_INNER path");
      actual.Entities.Where(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().ContainSingle(entity => IsClosed(entity));
      actual.Entities.Where(entity => entity.Layer == DxfExporter.CommonLineLayer)
        .Should().HaveCount(2);
    }

    [Fact]
    public async Task InvalidRelationshipRejectsExport()
    {
      SheetPlacement valid = this.BuildPlacement();
      valid.PartPlacements[1].X += 0.004D;
      using var output = new MemoryStream();

      Func<Task> act = () => valid.ExportDxf(output, mergeLines: true, differentiateChildren: false);

      await act.Should().ThrowAsync<CommonLineValidationException>();
      output.Length.Should().Be(0, "an invalid cluster must never leave a partial ambiguous DXF");
    }

    [Fact]
    public void ValidatorRejectsNonFiniteLineArcAndCircleGeometry()
    {
      var invalidEntities = new (string Name, DxfEntity Entity)[]
      {
        (
          "LINE P1",
          new DxfLine(
            new DxfPoint(double.NaN, 0D, 0D),
            new DxfPoint(1D, 0D, 0D))),
        (
          "LINE P2",
          new DxfLine(
            new DxfPoint(0D, 0D, 0D),
            new DxfPoint(1D, double.PositiveInfinity, 0D))),
        (
          "ARC center",
          new DxfArc(new DxfPoint(double.NaN, 0D, 0D), 1D, 0D, 90D)),
        (
          "ARC radius",
          new DxfArc(new DxfPoint(0D, 0D, 0D), double.PositiveInfinity, 0D, 90D)),
        (
          "ARC start angle",
          new DxfArc(new DxfPoint(0D, 0D, 0D), 1D, double.NaN, 90D)),
        (
          "ARC end angle",
          new DxfArc(new DxfPoint(0D, 0D, 0D), 1D, 0D, double.NegativeInfinity)),
        (
          "ARC overflowing sweep",
          new DxfArc(
            new DxfPoint(0D, 0D, 0D),
            1D,
            double.MaxValue,
            -double.MaxValue)),
        (
          "CIRCLE center",
          new DxfCircle(new DxfPoint(0D, double.NaN, 0D), 1D)),
        (
          "CIRCLE radius",
          new DxfCircle(new DxfPoint(0D, 0D, 0D), double.NaN)),
      };

      foreach ((string name, DxfEntity entity) in invalidEntities)
      {
        entity.Layer = DxfExporter.CommonLineLayer;
        var file = new DxfFile();
        file.Entities.Add(entity);

        Action validate = () => DxfExporter.ValidateCommonLineDxf(file, Tolerance);

        validate.Should().Throw<CommonLineValidationException>(
          "{0} must never reach the machine",
          name).WithMessage("*non-finite*");
      }
    }

    [Fact]
    public void ValidatorRejectsInvalidClosedCutContours()
    {
      var nonFiniteLightweight = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D },
        new DxfLwPolylineVertex { X = double.NaN, Y = 0D },
        new DxfLwPolylineVertex { X = 0D, Y = 1D },
      })
      { IsClosed = true };
      var nonFiniteBulge = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D, Bulge = double.PositiveInfinity },
        new DxfLwPolylineVertex { X = 1D, Y = 0D },
        new DxfLwPolylineVertex { X = 0D, Y = 1D },
      })
      { IsClosed = true };
      var zeroLengthLegacy = new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(0D, 0D, 0D)),
        new DxfVertex(new DxfPoint(1D, 0D, 0D)),
        new DxfVertex(new DxfPoint(1D, 0D, 0D)),
        new DxfVertex(new DxfPoint(0D, 1D, 0D)),
      })
      { IsClosed = true };
      var nonFiniteCircleCenter = new DxfCircle(
        new DxfPoint(double.PositiveInfinity, 0D, 0D),
        1D);
      var nonFiniteEllipseCenter = new DxfEllipse(
        new DxfPoint(0D, double.NaN, 0D),
        new DxfVector(2D, 0D, 0D),
        0.5D)
      {
        StartParameter = 0D,
        EndParameter = 2D * Math.PI,
      };
      var invalidEntities = new (string Name, DxfEntity Entity)[]
      {
        ("non-finite LWPOLYLINE coordinate", nonFiniteLightweight),
        ("non-finite LWPOLYLINE bulge", nonFiniteBulge),
        ("zero-length POLYLINE segment", zeroLengthLegacy),
        ("non-finite CIRCLE center", nonFiniteCircleCenter),
        ("non-finite ELLIPSE center", nonFiniteEllipseCenter),
      };

      foreach ((string name, DxfEntity entity) in invalidEntities)
      {
        entity.Layer = DxfExporter.CutOuterLayer;
        var file = new DxfFile();
        file.Entities.Add(entity);

        Action validate = () => DxfExporter.ValidateCommonLineDxf(file, Tolerance);

        validate.Should().Throw<CommonLineValidationException>(
          "{0} must fail closed before anything receives CUT_OUTER",
          name).WithMessage("*invalid entity*");
      }
    }

    [Fact]
    public async Task OpenOffcutUsesItsOwnLayerInsteadOfCutOuter()
    {
      SheetPlacement placement = this.BuildPlacement();
      string target = Path.Combine(this.tempDirectory, "with-offcut.dxf");
      var exporter = new DxfExporter();

      await exporter.Export(
        target,
        placement,
        doMergeLines: false,
        differentiateChildren: false,
        offcutLines: new[] { new OffcutLine { X1 = 0D, Y1 = 20D, X2 = 20D, Y2 = 20D } });

      DxfFile actual = DxfFile.Load(target);
      actual.Entities.Where(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().OnlyContain(entity => IsClosed(entity));
      actual.Entities.Should().ContainSingle(entity => entity.Layer == DxfExporter.OffcutLayer && entity is DxfLine);
    }

    [Fact]
    public async Task NonZeroSheetIdDoesNotOffsetLocalClusterHoleOrOrdinaryPartGeometry()
    {
      SheetPlacement placement = this.BuildPlacement(sheetId: 17, includeOrdinaryPart: true);
      using var output = new MemoryStream();

      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfFile actual = DxfFile.Load(output);

      List<(double MinX, double MaxX)> outers = actual.Entities
        .Where(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Select(BoundsX)
        .OrderBy(bounds => bounds.MinX)
        .ToList();
      List<(double MinX, double MaxX)> holes = actual.Entities
        .Where(entity => entity.Layer == DxfExporter.CutInnerLayer)
        .Select(BoundsX)
        .OrderBy(bounds => bounds.MinX)
        .ToList();

      outers.Should().HaveCount(2);
      outers[0].MinX.Should().BeApproximately(0D, Tolerance);
      outers[0].MaxX.Should().BeApproximately(20D + Gap, Tolerance);
      outers[1].MinX.Should().BeApproximately(30D, Tolerance);
      outers[1].MaxX.Should().BeApproximately(40D, Tolerance);
      holes.Select(bounds => bounds.MinX).Should().BeEquivalentTo(
        new[] { 2D, 12D + Gap, 32D },
        options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task OrdinaryClosedEllipseRemainsAnEllipseOnACommonLineSheet()
    {
      string ellipsePath = Path.Combine(this.tempDirectory, "ordinary-ellipse.dxf");
      var source = new DxfFile();
      source.Header.Version = DxfAcadVersion.R2000;
      source.Entities.Add(new DxfEllipse(
        new DxfPoint(0D, 0D, 0D),
        new DxfVector(3D, 0D, 0D),
        2D / 3D)
      {
        StartParameter = 0D,
        EndParameter = 2D * Math.PI,
      });
      source.Save(ellipsePath, true);

      SheetPlacement placement = this.AddOrdinaryPart(this.BuildPlacement(), ellipsePath, 35D, 10D);
      using var output = new MemoryStream();

      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfFile actual = DxfFile.Load(output);

      actual.Entities.Should().ContainSingle(entity =>
        entity.Layer == DxfExporter.CutOuterLayer && entity is DxfEllipse,
        "curves on normal parts are not part of the straight common-line restriction");
    }

    [Fact]
    public async Task OrdinaryEllipseMajorAxisFollowsItsNinetyDegreePartRotation()
    {
      string ellipsePath = Path.Combine(this.tempDirectory, "rotated-ordinary-ellipse.dxf");
      var source = new DxfFile();
      source.Header.Version = DxfAcadVersion.R2000;
      source.Entities.Add(new DxfEllipse(
        new DxfPoint(0D, 0D, 0D),
        new DxfVector(3D, 0D, 0D),
        0.5D)
      {
        StartParameter = 0D,
        EndParameter = 2D * Math.PI,
      });
      source.Save(ellipsePath, true);

      SheetPlacement placement = this.AddOrdinaryPart(this.BuildPlacement(), ellipsePath, 35D, 10D);
      placement.PartPlacements.Single(part => part.Id == 2).Rotation = 90D;
      using var output = new MemoryStream();

      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfFile actual = DxfFile.Load(output);

      DxfEllipse exported = actual.Entities
        .Single(entity => entity.Layer == DxfExporter.CutOuterLayer && entity is DxfEllipse)
        .Should().BeOfType<DxfEllipse>().Subject;
      exported.MajorAxis.X.Should().BeApproximately(0D, Tolerance);
      exported.MajorAxis.Y.Should().BeApproximately(3D, Tolerance);
      exported.MinorAxisRatio.Should().BeApproximately(0.5D, Tolerance);
    }

    [Fact]
    public async Task OrdinaryClosedSplineRemainsASplineOnACommonLineSheet()
    {
      string splinePath = Path.Combine(this.tempDirectory, "ordinary-spline.dxf");
      var spline = new DxfSpline { DegreeOfCurve = 1, IsClosed = true };
      foreach (DxfPoint point in new[]
      {
        new DxfPoint(0D, 0D, 0D),
        new DxfPoint(4D, 0D, 0D),
        new DxfPoint(4D, 4D, 0D),
        new DxfPoint(0D, 4D, 0D),
        new DxfPoint(0D, 0D, 0D),
      })
      {
        spline.ControlPoints.Add(new DxfControlPoint(point, 1D));
      }

      foreach (double knot in new[] { 0D, 0D, 1D, 2D, 3D, 4D, 4D })
      {
        spline.KnotValues.Add(knot);
      }

      var source = new DxfFile();
      source.Header.Version = DxfAcadVersion.R2000;
      source.Entities.Add(spline);
      source.Save(splinePath, true);

      SheetPlacement placement = this.AddOrdinaryPart(this.BuildPlacement(), splinePath, 35D, 10D);
      using var output = new MemoryStream();

      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfFile actual = DxfFile.Load(output);

      DxfEntity exported = actual.Entities.Should().ContainSingle(entity =>
        entity.Layer == DxfExporter.CutOuterLayer && entity is DxfSpline,
        "a normal part's declared closed spline must remain a closed source primitive").Subject;
      exported.Should().BeOfType<DxfSpline>().Which.IsClosed.Should().BeTrue();
    }

    [Fact]
    public async Task OrthogonalLChainExportsOneClosedOuterAndTwoNodalSeams()
    {
      SheetPlacement placement = this.BuildLPlacement();
      using var output = new MemoryStream();

      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfFile actual = DxfFile.Load(output);

      DxfEntity outerEntity = actual.Entities.Single(entity => entity.Layer == DxfExporter.CutOuterLayer);
      outerEntity.Should().Match<DxfEntity>(entity => IsClosed(entity));
      List<DxfLine> seams = actual.Entities
        .Where(entity => entity.Layer == DxfExporter.CommonLineLayer)
        .OfType<DxfLine>()
        .ToList();
      seams.Should().HaveCount(2);
      seams.Select(line =>
        $"{Math.Min(line.P1.X, line.P2.X):F6},{Math.Min(line.P1.Y, line.P2.Y):F6}|" +
        $"{Math.Max(line.P1.X, line.P2.X):F6},{Math.Max(line.P1.Y, line.P2.Y):F6}")
        .Should().OnlyHaveUniqueItems();

      var outerNodes = ((DxfLwPolyline)outerEntity).Vertices
        .Select(vertex => $"{vertex.X:F6},{vertex.Y:F6}")
        .ToHashSet();
      seams.SelectMany(line => new[] { line.P1, line.P2 })
        .Should().OnlyContain(point => outerNodes.Contains($"{point.X:F6},{point.Y:F6}"));
    }

    [Fact]
    public async Task ReloadedCurvedClusterPreservesItsSourceBulgesInsteadOfChording()
    {
      string curvedPath = Path.Combine(this.tempDirectory, "curved-outside.dxf");
      var source = new DxfFile();
      source.Header.Version = DxfAcadVersion.R2000;
      source.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 5D, Bulge = 0.25D },
        new DxfLwPolylineVertex { X = 0D, Y = 5D },
      })
      {
        IsClosed = true,
      });
      source.Save(curvedPath, true);

      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(curvedPath));
      detail.TryConvertToNfp(0, out INfp first).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp second).Should().BeTrue();
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(first) { Id = 0, Source = 0, X = 0D, Y = 0D },
        new PartPlacement(second) { Id = 1, Source = 0, X = 10D + Gap, Y = 0D },
      };
      CommonEdge relationship = FindRelationship(parts[0], parts[1]);
      var original = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 100, 100),
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { relationship },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);

      SheetPlacement reloaded = SheetPlacement.FromJson(original.ToJson());
      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(reloaded).Clusters.Should().ContainSingle(
        "the resolver reloads the source LWPOLYLINE even after CurvedSegments metadata is lost").Subject;
      cluster.OuterBoundaries.Should().ContainSingle();
      cluster.OuterBoundaries.Single().Bulges
        .Where(bulge => Math.Abs(bulge) > 1e-12)
        .Select(Math.Abs)
        .Should().BeEquivalentTo(new[] { 0.25D, 0.25D });
      using var output = new MemoryStream();

      await reloaded.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfLwPolyline exported = DxfFile.Load(output).Entities
        .Single(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().BeOfType<DxfLwPolyline>().Subject;
      exported.Vertices.Where(vertex => Math.Abs(vertex.Bulge) > 1e-12)
        .Select(vertex => Math.Abs(vertex.Bulge))
        .Should().BeEquivalentTo(new[] { 0.25D, 0.25D },
          "the external cluster boundary must retain both exact source arcs");
    }

    [Fact]
    public void SourceOuterCacheInvalidatesWhenContentChangesAtTheSameSizeAndTimestamp()
    {
      string path = Path.Combine(this.tempDirectory, "content-addressed-curved-source.dxf");
      byte[] firstBytes = BulgedLightweightPolylineBytes(0.25D);
      byte[] secondBytes = BulgedLightweightPolylineBytes(0.75D);
      int equalLength = Math.Max(firstBytes.Length, secondBytes.Length) + 128;
      firstBytes = PadTextDxf(firstBytes, equalLength);
      secondBytes = PadTextDxf(secondBytes, equalLength);
      DateTime fixedTimestamp = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);

      File.WriteAllBytes(path, firstBytes);
      File.SetLastWriteTimeUtc(path, fixedTimestamp);
      DateTime unchangedTimestamp = File.GetLastWriteTimeUtc(path);
      SheetPlacement first = SheetPlacement.FromJson(
        this.BuildTransformedCurvedPlacement(path, rotation: 0D, mirrored: false).ToJson());
      CommonLineGeometryResolver.Resolve(first).Clusters.Single().OuterBoundaries.Single().Bulges
        .Where(bulge => Math.Abs(bulge) > 1e-12)
        .Select(Math.Abs)
        .Should().BeEquivalentTo(new[] { 0.25D, 0.25D });

      File.WriteAllBytes(path, secondBytes);
      File.SetLastWriteTimeUtc(path, unchangedTimestamp);
      new FileInfo(path).Length.Should().Be(equalLength);
      File.GetLastWriteTimeUtc(path).Should().Be(unchangedTimestamp);
      SheetPlacement second = SheetPlacement.FromJson(
        this.BuildTransformedCurvedPlacement(path, rotation: 0D, mirrored: false).ToJson());

      CommonLineGeometryResolver.Resolve(second).Clusters.Single().OuterBoundaries.Single().Bulges
        .Where(bulge => Math.Abs(bulge) > 1e-12)
        .Select(Math.Abs)
        .Should().BeEquivalentTo(new[] { 0.75D, 0.75D },
          "the source-boundary cache must follow drawing content, not length and mtime hints");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactSourceLineMappingAccountsForPointFRoundTripFarFromOrigin(bool metric)
    {
      double scale = metric ? CommonLineDefaults.InchesToMillimeters : 1D;
      double gap = Gap * scale;
      double tolerance = Tolerance * scale;
      double sourceX = 1048576.03125D * scale;
      double sourceY = 524288.03125D * scale;
      double width = 8D * scale;
      double height = 4D * scale;
      string path = Path.Combine(
        this.tempDirectory,
        metric ? "far-origin-metric.dxf" : "far-origin-inch.dxf");
      WriteOffsetRectangle(
        path,
        sourceX,
        sourceY,
        width,
        height,
        metric ? DxfUnits.Millimeters : DxfUnits.Inches,
        metric ? DxfDrawingUnits.Metric : DxfDrawingUnits.English);
      Math.Abs(sourceX - (double)(float)sourceX).Should().BeGreaterThan(tolerance,
        "the fixture must exercise real PointF precision loss");

      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      detail.TryConvertToNfp(0, out INfp firstNfp).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp secondNfp).Should().BeTrue();
      var first = new PartPlacement(firstNfp)
      {
        Id = 0,
        Source = 0,
        X = -sourceX,
        Y = -sourceY,
      };
      var second = new PartPlacement(secondNfp)
      {
        Id = 1,
        Source = 0,
        X = -sourceX + width + gap,
        Y = -sourceY,
      };
      int firstRight = FindExtremeVerticalEdge(first.Part, maximumX: true);
      int secondLeft = FindExtremeVerticalEdge(second.Part, maximumX: false);

      CommonEdge relationship = CommonLineGeometryResolver.CreateCommonEdge(
        first,
        firstRight,
        0D,
        1D,
        second,
        secondLeft,
        0D,
        1D,
        gap,
        tolerance);
      relationship.SchemaVersion.Should().Be(CommonEdge.ParametricSchemaVersion);
      relationship.SpanA.SourceFingerprint.Should().NotBeNullOrWhiteSpace();
      var sheet = new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(
          0,
          (int)Math.Ceiling(20D * scale),
          (int)Math.Ceiling(20D * scale)),
        new IPartPlacement[] { first, second },
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { relationship },
        commonLineGap: gap,
        geometryTolerance: tolerance);

      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(sheet)
        .Clusters.Should().ContainSingle().Subject;
      cluster.OuterBoundaries.Should().ContainSingle(path => path.IsClosed);
      CommonCenterCurve seam = cluster.CommonCurves.Should().ContainSingle().Subject;
      seam.GeometryKind.Should().Be(CommonLineGeometryKind.Line);
      seam.Start.X.Should().BeApproximately(width + (gap / 2D), tolerance);
      seam.End.X.Should().BeApproximately(width + (gap / 2D), tolerance);
    }

    [Fact]
    public async Task ReloadedNinetyDegreeClusterKeepsDirectedSourceBulges()
    {
      string path = this.CreateBulgedLightweightPolyline("rotated-curved-outside.dxf", bulgedVertexIndex: 1);
      SheetPlacement placement = SheetPlacement.FromJson(
        this.BuildTransformedCurvedPlacement(path, rotation: 90D, mirrored: false).ToJson());

      CommonLinePath boundary = CommonLineGeometryResolver.Resolve(placement)
        .Clusters.Should().ContainSingle().Subject
        .OuterBoundaries.Should().ContainSingle().Subject;
      boundary.Bulges.Count(bulge => Math.Abs(bulge) > 1e-12).Should().Be(2);
      foreach (IPartPlacement part in placement.PartPlacements)
      {
        AssertDirectedBulge(
          boundary,
          TransformSourcePoint(part, 10D, 0D),
          TransformSourcePoint(part, 10D, 5D),
          0.25D);
      }

      using var output = new MemoryStream();
      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfLwPolyline exported = DxfFile.Load(output).Entities
        .Single(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().BeOfType<DxfLwPolyline>().Subject;
      exported.Vertices.Count(vertex => Math.Abs(vertex.Bulge) > 1e-12).Should().Be(2);
    }

    [Fact]
    public async Task ReloadedOneEightyDegreeClusterKeepsDirectedSourceBulges()
    {
      string path = this.CreateBulgedLightweightPolyline("half-turn-curved-outside.dxf", bulgedVertexIndex: 2);
      SheetPlacement placement = SheetPlacement.FromJson(
        this.BuildTransformedCurvedPlacement(path, rotation: 180D, mirrored: false).ToJson());

      CommonLinePath boundary = CommonLineGeometryResolver.Resolve(placement)
        .Clusters.Should().ContainSingle().Subject
        .OuterBoundaries.Should().ContainSingle().Subject;
      foreach (IPartPlacement part in placement.PartPlacements)
      {
        AssertDirectedBulge(
          boundary,
          TransformSourcePoint(part, 10D, 5D),
          TransformSourcePoint(part, 0D, 5D),
          0.25D);
      }

      using var output = new MemoryStream();
      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfLwPolyline exported = DxfFile.Load(output).Entities
        .Single(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().BeOfType<DxfLwPolyline>().Subject;
      exported.Vertices.Count(vertex => Math.Abs(vertex.Bulge) > 1e-12).Should().Be(2);
    }

    [Fact]
    public async Task ReloadedMirroredClusterInvertsDirectedSourceBulges()
    {
      string path = this.CreateBulgedLightweightPolyline("mirrored-curved-outside.dxf", bulgedVertexIndex: 2);
      SheetPlacement placement = SheetPlacement.FromJson(
        this.BuildTransformedCurvedPlacement(path, rotation: 0D, mirrored: true).ToJson());

      CommonLinePath boundary = CommonLineGeometryResolver.Resolve(placement)
        .Clusters.Should().ContainSingle().Subject
        .OuterBoundaries.Should().ContainSingle().Subject;
      boundary.Bulges.Count(bulge => Math.Abs(bulge) > 1e-12).Should().Be(2);
      foreach (IPartPlacement part in placement.PartPlacements)
      {
        AssertDirectedBulge(
          boundary,
          TransformSourcePoint(part, 10D, 5D),
          TransformSourcePoint(part, 0D, 5D),
          -0.25D);
      }

      using var output = new MemoryStream();
      await placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfLwPolyline exported = DxfFile.Load(output).Entities
        .Single(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().BeOfType<DxfLwPolyline>().Subject;
      exported.Vertices.Count(vertex => Math.Abs(vertex.Bulge) > 1e-12).Should().Be(2);
    }

    [Fact]
    public void CurvedClusterInAnUnsupportedPolylineContainerIsRejected()
    {
      string path = Path.Combine(this.tempDirectory, "unsupported-curved-polyline.dxf");
      var source = new DxfFile();
      source.Entities.Add(new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(0D, 0D, 0D)),
        new DxfVertex(new DxfPoint(10D, 0D, 0D)),
        new DxfVertex(new DxfPoint(10D, 5D, 0D)) { Bulge = 0.25D },
        new DxfVertex(new DxfPoint(0D, 5D, 0D)),
      })
      {
        IsClosed = true,
      });
      source.Save(path, true);
      SheetPlacement placement = this.BuildTransformedCurvedPlacement(path, rotation: 0D, mirrored: false);

      Action act = () => CommonLineGeometryResolver.Resolve(placement);

      act.Should().Throw<CommonLineValidationException>()
        .WithMessage("*one closed planar LWPOLYLINE*");
    }

    [Fact]
    public async Task OrdinarySelfIntersectingOuterRejectsTheWholeCommonLineExport()
    {
      string bowTiePath = Path.Combine(this.tempDirectory, "bow-tie.dxf");
      var source = new DxfFile();
      source.Header.Version = DxfAcadVersion.R2000;
      source.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 5D },
        new DxfLwPolylineVertex { X = 0D, Y = 5D },
        new DxfLwPolylineVertex { X = 10D, Y = 0D },
      })
      {
        IsClosed = true,
      });
      source.Save(bowTiePath, true);

      SheetPlacement clustered = this.BuildPlacement();
      var bowTie = new NoFitPolygon(new[]
      {
        new SvgPoint(0D, 0D),
        new SvgPoint(10D, 5D),
        new SvgPoint(0D, 5D),
        new SvgPoint(10D, 0D),
      })
      {
        Name = bowTiePath,
      };
      var parts = clustered.PartPlacements.ToList();
      parts.Add(new PartPlacement(bowTie) { Id = 2, Source = 1, X = 30D, Y = 0D });
      var placement = new SheetPlacement(
        clustered.PlacementType,
        clustered.Sheet,
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: clustered.CommonEdges,
        commonLineGap: clustered.CommonLineGap,
        geometryTolerance: clustered.GeometryTolerance);
      using var output = new MemoryStream();

      Func<Task> act = () => placement.ExportDxf(output, mergeLines: false, differentiateChildren: false);

      await act.Should().ThrowAsync<CommonLineValidationException>()
        .WithMessage("*CUT_OUTER*self-intersecting*");
      output.Length.Should().Be(0, "validation must finish before any ambiguous DXF is written");
    }

    private static bool IsClosed(DxfEntity entity)
      => entity is DxfCircle
        || (entity is DxfLwPolyline lightweight && lightweight.IsClosed)
        || (entity is DxfPolyline polyline && polyline.IsClosed);

    private static DxfPolyline ClosedRectangle(double x, double y, double width, double height)
      => new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(x, y, 0D)),
        new DxfVertex(new DxfPoint(x + width, y, 0D)),
        new DxfVertex(new DxfPoint(x + width, y + height, 0D)),
        new DxfVertex(new DxfPoint(x, y + height, 0D)),
      })
      {
        IsClosed = true,
      };

    private static void WriteClosedPolyline(string path, IEnumerable<(double X, double Y)> points)
    {
      var source = new DxfFile();
      source.Header.Version = DxfAcadVersion.R2000;
      source.Entities.Add(new DxfLwPolyline(points.Select(point => new DxfLwPolylineVertex
      {
        X = point.X,
        Y = point.Y,
      }))
      {
        IsClosed = true,
      });
      source.Save(path, true);
    }

    private static (double MinX, double MaxX) BoundsX(DxfEntity entity)
    {
      List<DxfLine> segments = new DxfLineMerger()
        .SplitLines(new[] { entity })
        .OfType<DxfLine>()
        .ToList();
      segments.Should().NotBeEmpty();
      return (
        segments.SelectMany(line => new[] { line.P1.X, line.P2.X }).Min(),
        segments.SelectMany(line => new[] { line.P1.X, line.P2.X }).Max());
    }

    private string CreateBulgedLightweightPolyline(string name, int bulgedVertexIndex)
    {
      string path = Path.Combine(this.tempDirectory, name);
      var vertices = new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 5D },
        new DxfLwPolylineVertex { X = 0D, Y = 5D },
      };
      vertices[bulgedVertexIndex].Bulge = 0.25D;
      var source = new DxfFile();
      source.Header.Version = DxfAcadVersion.R2000;
      source.Entities.Add(new DxfLwPolyline(vertices) { IsClosed = true });
      source.Save(path, true);
      return path;
    }

    private static byte[] BulgedLightweightPolylineBytes(double bulge)
    {
      var source = new DxfFile();
      source.Header.Version = DxfAcadVersion.R2000;
      source.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 5D, Bulge = bulge },
        new DxfLwPolylineVertex { X = 0D, Y = 5D },
      })
      {
        IsClosed = true,
      });
      using var stream = new MemoryStream();
      source.Save(stream, true);
      return stream.ToArray();
    }

    private static byte[] PadTextDxf(byte[] source, int length)
    {
      if (source.Length == length)
      {
        return source;
      }

      const string windowsEof = "0\r\nEOF\r\n";
      const string unixEof = "0\nEOF\n";
      string text = System.Text.Encoding.UTF8.GetString(source);
      string eof = text.EndsWith(windowsEof, StringComparison.Ordinal)
        ? windowsEof
        : unixEof;
      int eofIndex = text.LastIndexOf(eof, StringComparison.Ordinal);
      eofIndex.Should().BeGreaterThanOrEqualTo(0);

      string newline = eof == windowsEof ? "\r\n" : "\n";
      string commentPrefix = $"999{newline}";
      string commentSuffix = newline;
      int commentLength = length - source.Length - commentPrefix.Length - commentSuffix.Length;
      commentLength.Should().BeGreaterThanOrEqualTo(0);
      string padded = text.Insert(
        eofIndex,
        commentPrefix + new string(' ', commentLength) + commentSuffix);
      byte[] result = System.Text.Encoding.UTF8.GetBytes(padded);
      if (result.Length != length)
      {
        throw new InvalidOperationException("The DXF cache fixture failed to produce equal-size source files.");
      }

      return result;
    }

    private static void WriteOffsetRectangle(
      string path,
      double x,
      double y,
      double width,
      double height,
      DxfUnits units,
      DxfDrawingUnits drawingUnits)
    {
      var source = new DxfFile();
      source.Header.Version = DxfAcadVersion.R2000;
      source.Header.DefaultDrawingUnits = units;
      source.Header.DrawingUnits = drawingUnits;
      source.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = x, Y = y },
        new DxfLwPolylineVertex { X = x + width, Y = y },
        new DxfLwPolylineVertex { X = x + width, Y = y + height },
        new DxfLwPolylineVertex { X = x, Y = y + height },
      })
      {
        IsClosed = true,
      });
      source.Save(path, true);
    }

    private static int FindExtremeVerticalEdge(INfp part, bool maximumX)
    {
      SvgPoint[] points = part.Points;
      int count = points.Length;
      if (count > 1
        && points[0].X == points[^1].X
        && points[0].Y == points[^1].Y)
      {
        count--;
      }

      int result = -1;
      double best = maximumX ? double.NegativeInfinity : double.PositiveInfinity;
      for (int i = 0; i < count; i++)
      {
        SvgPoint start = points[i];
        SvgPoint end = points[(i + 1) % count];
        if (start.X != end.X)
        {
          continue;
        }

        double x = start.X;
        if ((maximumX && x > best) || (!maximumX && x < best))
        {
          result = i;
          best = x;
        }
      }

      result.Should().BeGreaterThanOrEqualTo(0, "the source rectangle must retain a vertical edge");
      return result;
    }

    private SheetPlacement BuildTransformedCurvedPlacement(string path, double rotation, bool mirrored)
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      detail.TryConvertToNfp(0, out INfp firstSource).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp secondSource).Should().BeTrue();
      INfp first = mirrored ? firstSource.MirrorX() : firstSource;
      INfp second = mirrored ? secondSource.MirrorX() : secondSource;
      if (rotation != 0D)
      {
        first = first.Rotate(rotation);
        second = second.Rotate(rotation);
      }

      double minX = first.Points.Min(point => point.X);
      double minY = first.Points.Min(point => point.Y);
      double width = first.Points.Max(point => point.X) - minX;
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(first)
        {
          Id = 0,
          Source = 0,
          X = -minX,
          Y = -minY,
          Rotation = rotation,
          IsMirrored = mirrored,
        },
        new PartPlacement(second)
        {
          Id = 1,
          Source = 0,
          X = -minX + width + Gap,
          Y = -minY,
          Rotation = rotation,
          IsMirrored = mirrored,
        },
      };
      CommonEdge relationship = FindRelationship(parts[0], parts[1]);
      return new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 100, 100),
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { relationship },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);
    }

    private static CommonLinePoint TransformSourcePoint(
      IPartPlacement placement,
      double x,
      double y)
    {
      if (placement.IsMirrored)
      {
        x = -x;
      }

      double radians = placement.Rotation * Math.PI / 180D;
      return new CommonLinePoint(
        (x * Math.Cos(radians)) - (y * Math.Sin(radians)) + placement.X,
        (x * Math.Sin(radians)) + (y * Math.Cos(radians)) + placement.Y);
    }

    private static void AssertDirectedBulge(
      CommonLinePath boundary,
      CommonLinePoint expectedStart,
      CommonLinePoint expectedEnd,
      double expectedBulge)
    {
      for (int i = 0; i < boundary.Points.Count; i++)
      {
        CommonLinePoint start = boundary.Points[i];
        CommonLinePoint end = boundary.Points[(i + 1) % boundary.Points.Count];
        if (Coincide(start, expectedStart) && Coincide(end, expectedEnd))
        {
          boundary.Bulges[i].Should().BeApproximately(expectedBulge, 1e-12);
          return;
        }

        if (Coincide(start, expectedEnd) && Coincide(end, expectedStart))
        {
          boundary.Bulges[i].Should().BeApproximately(-expectedBulge, 1e-12);
          return;
        }
      }

      throw new Xunit.Sdk.XunitException("The expected directed curved source segment was not retained.");
    }

    private static bool Coincide(CommonLinePoint first, CommonLinePoint second)
      => Math.Abs(first.X - second.X) <= Tolerance
        && Math.Abs(first.Y - second.Y) <= Tolerance;

    private SheetPlacement BuildPlacement(int sheetId = 0, bool includeOrdinaryPart = false)
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(this.sourcePath));
      detail.TryConvertToNfp(0, out INfp first).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp second).Should().BeTrue();
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(first) { Id = 0, Source = 0, X = 0D, Y = 0D },
        new PartPlacement(second) { Id = 1, Source = 0, X = 10D + Gap, Y = 0D },
      };
      if (includeOrdinaryPart)
      {
        detail.TryConvertToNfp(0, out INfp ordinary).Should().BeTrue();
        parts.Add(new PartPlacement(ordinary) { Id = 2, Source = 0, X = 30D, Y = 0D });
      }

      CommonEdge relationship = FindRelationship(parts[0], parts[1]);
      Sheet sheet = Sheet.NewSheet(0, 100, 100);
      sheet.Id = sheetId;
      return new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        sheet,
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { relationship },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);
    }

    private SheetPlacement BuildLPlacement()
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(this.sourcePath));
      detail.TryConvertToNfp(0, out INfp first).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp second).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp third).Should().BeTrue();
      var parts = new List<IPartPlacement>
      {
        new PartPlacement(first) { Id = 0, Source = 0, X = 0D, Y = 0D },
        new PartPlacement(second) { Id = 1, Source = 0, X = 10D + Gap, Y = 0D },
        new PartPlacement(third) { Id = 2, Source = 0, X = 0D, Y = 5D + Gap },
      };
      CommonEdge horizontal = FindRelationship(parts[0], parts[1]);
      CommonEdge vertical = FindRelationship(parts[0], parts[2]);

      return new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        Sheet.NewSheet(0, 100, 100),
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { horizontal, vertical },
        commonLineGap: Gap,
        geometryTolerance: Tolerance);
    }

    private SheetPlacement AddOrdinaryPart(
      SheetPlacement clustered,
      string path,
      double x,
      double y)
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      detail.TryConvertToNfp(1, out INfp ordinary).Should().BeTrue();
      var parts = clustered.PartPlacements.ToList();
      parts.Add(new PartPlacement(ordinary) { Id = 2, Source = 1, X = x, Y = y });
      return new SheetPlacement(
        clustered.PlacementType,
        clustered.Sheet,
        parts,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: clustered.CommonEdges,
        commonLineGap: clustered.CommonLineGap,
        geometryTolerance: clustered.GeometryTolerance);
    }

    private static CommonEdge FindRelationship(IPartPlacement first, IPartPlacement second)
    {
      CommonEdge relationship = null;
      for (int a = 0; a < first.Part.Points.Length && relationship == null; a++)
      {
        for (int b = 0; b < second.Part.Points.Length && relationship == null; b++)
        {
          CommonLineGeometryResolver.TryCreateCommonEdge(
            first,
            a,
            second,
            b,
            Gap,
            Tolerance,
            out relationship);
        }
      }

      relationship.Should().NotBeNull("the source rectangles have one intentional full-length side pair");
      return relationship;
    }
  }
}
