namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
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
  /// End-to-end coverage for deliberate exact circular candidates in RasterNest. The DXFs are source
  /// geometry (not hand-built NFP chords), so every accepted relationship must survive source-span
  /// validation, clearance, cluster topology and final DXF generation.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public sealed class ExactArcCommonLinePlannerFixture : IDisposable
  {
    private const double NormalGap = 0.30D;
    private const double Gap = 0.01D;
    private const double Tolerance = 0.0001D;
    private readonly string directory;

    public ExactArcCommonLinePlannerFixture()
    {
      this.directory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.directory);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.directory, true);
      }
      catch (IOException)
      {
        // Test cleanup must not hide a geometry failure.
      }
    }

    [Fact]
    public async Task PlannerCreatesExactArcAtActiveGapAndExportsClosedCluster()
    {
      (string innerPath, string outerPath) = this.WriteSemicirclePair(Gap, "active-gap");
      IPartPlacement inner = LoadPlacement(innerPath, 0, 10D, 10D);
      IPartPlacement outer = LoadPlacement(outerPath, 1, 10.20D, 10D);
      var placements = new List<IPartPlacement> { inner, outer };
      DxfCommonLineArcCandidate proposed = DxfCommonLineArcCandidateFinder.Find(
        inner, outer, Gap, Tolerance).Should().ContainSingle().Subject;
      outer.X += proposed.TranslationX;
      outer.Y += proposed.TranslationY;
      PlacementCollision.TooClose(
        inner.PlacedPart,
        outer.PlacedPart,
        0D,
        PlacementCollision.PlacementNoise).Should().BeFalse("concentric complementary material must not overlap");
      CommonEdgeClearance.TooClose(
        inner,
        outer,
        NormalGap,
        PlacementCollision.PlacementNoise,
        new[]
        {
          new CommonEdgeClearance.EdgeReference(
            inner, -1, outer, -1, spanA: proposed.SpanA, spanB: proposed.SpanB),
        },
        Gap,
        Tolerance).Should().BeFalse("the complete exact arc corridor is the only reduced clearance");
      outer.X -= proposed.TranslationX;
      outer.Y -= proposed.TranslationY;

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        Rules(),
        Profiles(inner.Part, outer.Part, new[] { 0 }),
        sheetW: 30D,
        sheetH: 30D,
        margin: 0D,
        commonLineGap: Gap,
        geometryTolerance: Tolerance,
        effort: NestEffort.Fast);

      SparrowNestService.CommonEdgeIntent intent = result.Edges.Should().ContainSingle(
        $"givenUp={result.GivenUp}; fallbacks={string.Join(" | ", result.Fallbacks.Select(item => item.Reason))}")
        .Subject;
      CommonEdge record = Record(intent, Gap);
      using (new AssertionScope())
      {
        intent.SpanA.Should().NotBeNull();
        intent.SpanB.Should().NotBeNull();
        record.SchemaVersion.Should().Be(CommonEdge.ParametricSchemaVersion);
        record.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
        record.Gap.Should().BeApproximately(Gap, Tolerance);
        record.EdgeAIndex.Should().Be(-1);
        record.EdgeBIndex.Should().Be(-1);
        placements[1].X.Should().BeApproximately(placements[0].X, Tolerance);
      }

      SheetPlacement sheet = Sheet(placements, record, Gap);
      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(sheet)
        .Clusters.Should().ContainSingle().Subject;
      cluster.OuterBoundaries.Should().ContainSingle(path => path.IsClosed);
      cluster.CommonLines.Should().BeEmpty("an analytic arc must not be emitted as its endpoint chord");
      cluster.CommonCurves.Should().ContainSingle()
        .Which.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);

      using var stream = new MemoryStream();
      await sheet.ExportDxf(stream, mergeLines: false, differentiateChildren: false);
      stream.Position = 0;
      DxfFile exported = DxfFile.Load(stream);
      exported.Entities.Where(entity => entity.Layer == DxfExporter.CommonLineLayer)
        .Should().ContainSingle();
      exported.Entities.Where(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().ContainSingle().Which.Should().BeOfType<DxfLwPolyline>()
        .Which.IsClosed.Should().BeTrue();
    }

    [Fact]
    public void PlannerUsesALegalNinetyDegreeArcPoseAndPreservesTheDxfSourcePath()
    {
      (string innerPath, string outerPath) = this.WriteSemicirclePair(Gap, "rotated");
      IPartPlacement innerSource = LoadPlacement(innerPath, 0, 0D, 0D);
      IPartPlacement outerSource = LoadPlacement(outerPath, 1, 0D, 0D);
      IPartPlacement inner = RotatedPlacement(innerSource.Part, 0, 90D, 10D, 10D);
      IPartPlacement outer = RotatedPlacement(outerSource.Part, 1, 90D, 10.20D, 10D);
      var placements = new List<IPartPlacement> { inner, outer };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        Rules(),
        Profiles(innerSource.Part, outerSource.Part, new[] { 90 }),
        sheetW: 30D,
        sheetH: 30D,
        margin: 0D,
        commonLineGap: Gap,
        geometryTolerance: Tolerance,
        effort: NestEffort.Fast);

      SparrowNestService.CommonEdgeIntent intent = result.Edges.Should().ContainSingle(
        $"givenUp={result.GivenUp}; fallbacks={string.Join(" | ", result.Fallbacks.Select(item => item.Reason))}")
        .Subject;
      CommonEdge record = Record(intent, Gap);
      using (new AssertionScope())
      {
        placements.Should().OnlyContain(placement => Normalize(placement.Rotation) == 90D);
        placements[0].Part.Name.Should().Be(innerPath);
        placements[1].Part.Name.Should().Be(outerPath);
        record.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
        CommonLineGeometryResolver.Resolve(Sheet(placements, record, Gap)).Clusters.Should().ContainSingle();
      }
    }

    [Fact]
    public void PartialArcSpanSplitsTessellationAndDoesNotRelaxTheRemainingCurve()
    {
      string innerPath = Path.Combine(this.directory, "partial-convex.dxf");
      string outerPath = Path.Combine(this.directory, "partial-concave.dxf");
      WriteConvexPart(innerPath, 5D);
      WriteConcavePart(outerPath, 5D + Gap, 8D, -43.7D, 61.3D);
      IPartPlacement inner = LoadPlacement(innerPath, 0, 10D, 10D);
      IPartPlacement outer = LoadPlacement(outerPath, 1, 10.20D, 10D);
      var placements = new List<IPartPlacement> { inner, outer };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        Rules(),
        Profiles(inner.Part, outer.Part, new[] { 0 }),
        sheetW: 30D,
        sheetH: 30D,
        margin: 0D,
        commonLineGap: Gap,
        geometryTolerance: Tolerance,
        effort: NestEffort.Fast);

      SparrowNestService.CommonEdgeIntent intent = result.Edges.Should().ContainSingle(
        $"givenUp={result.GivenUp}; fallbacks={string.Join(" | ", result.Fallbacks.Select(item => item.Reason))}")
        .Subject;
      intent.SpanA.StartParameter.Should().NotBeApproximately(0D, 1e-6D);
      intent.SpanA.EndParameter.Should().NotBeApproximately(1D, 1e-6D);
      CommonEdge record = Record(intent, Gap);
      CommonLineGeometryResolver.Resolve(Sheet(placements, record, Gap)).Clusters.Should().ContainSingle();

      var fullReference = new CommonEdgeClearance.EdgeReference(
        intent.PartA,
        -1,
        intent.PartB,
        -1,
        spanA: intent.SpanA,
        spanB: intent.SpanB);
      CommonEdgeClearance.TooClose(
        intent.PartA,
        intent.PartB,
        NormalGap,
        PlacementCollision.PlacementNoise,
        new[] { fullReference },
        Gap,
        Tolerance).Should().BeFalse(
          "the exact angular endpoints are split inside tessellated NFP chords");

      CommonBoundarySpan narrowA = Narrow(intent.PartA, intent.SpanA, 0.35D, 0.65D);
      CommonBoundarySpan narrowB = Narrow(intent.PartB, intent.SpanB, 0.35D, 0.65D);
      var narrowReference = new CommonEdgeClearance.EdgeReference(
        intent.PartA,
        -1,
        intent.PartB,
        -1,
        spanA: narrowA,
        spanB: narrowB);
      CommonEdgeClearance.TooClose(
        intent.PartA,
        intent.PartB,
        NormalGap,
        PlacementCollision.PlacementNoise,
        new[] { narrowReference },
        Gap,
        Tolerance).Should().BeTrue(
          "a source-backed span grants no blanket clearance exception to the rest of the arc");
    }

    [Fact]
    public void ContinuousPolicyRetainsAnArbitraryExactArcPose()
    {
      (string innerPath, string outerPath) = this.WriteSemicirclePair(Gap, "continuous");
      IPartPlacement innerSource = LoadPlacement(innerPath, 0, 0D, 0D);
      IPartPlacement outerSource = LoadPlacement(outerPath, 1, 0D, 0D);
      const double arbitraryRotation = 37.125D;
      IPartPlacement inner = RotatedPlacement(innerSource.Part, 0, arbitraryRotation, 10D, 10D);
      IPartPlacement outer = RotatedPlacement(outerSource.Part, 1, arbitraryRotation, 10.20D, 10D);
      var placements = new List<IPartPlacement> { inner, outer };
      DxfCommonLineArcCandidateFinder.Find(inner, outer, Gap, Tolerance).Should().ContainSingle(
        "the current arbitrary Continuous pose is an explicitly legal planner pose");
      var profiles = new Dictionary<int, SparrowNestService.DirectOrientationProfile>
      {
        [0] = new SparrowNestService.DirectOrientationProfile(
          innerSource.Part,
          new[] { 0, 90, 180, 270 },
          continuous: true),
        [1] = new SparrowNestService.DirectOrientationProfile(
          outerSource.Part,
          new[] { 0, 90, 180, 270 },
          continuous: true),
      };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        Rules(),
        profiles,
        sheetW: 30D,
        sheetH: 30D,
        margin: 0D,
        commonLineGap: Gap,
        geometryTolerance: Tolerance,
        effort: NestEffort.Fast);

      SparrowNestService.CommonEdgeIntent intent = result.Edges.Should().ContainSingle(
        $"givenUp={result.GivenUp}; fallbacks={string.Join(" | ", result.Fallbacks.Select(item => item.Reason))}")
        .Subject;
      placements.Should().OnlyContain(placement =>
        Math.Abs(Normalize(placement.Rotation) - arbitraryRotation) <= 1e-8D);
      Record(intent, Gap).GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
    }

    [Fact]
    public void ContinuousPolicyDerivesANonCardinalPoseFromEntirelyCurvedPartialArcs()
    {
      const double fixedRotation = 37.125D;
      const double initialMovingRotation = 211.75D;
      const double alignmentReferenceRotation = fixedRotation;
      (string innerPath, string outerPath) = this.WriteAllCurvedSectorPair("derived-continuous");
      IPartPlacement innerSource = LoadPlacement(innerPath, 0, 0D, 0D);
      IPartPlacement outerSource = LoadPlacement(outerPath, 1, 0D, 0D);
      ((NoFitPolygon)innerSource.Part).GetCurvedSegments().Should().OnlyContain(value => value);
      ((NoFitPolygon)outerSource.Part).GetCurvedSegments().Should().OnlyContain(value => value);

      const double targetOriginX = 15D;
      const double targetOriginY = 15D;
      IPartPlacement inner = RotatedPlacement(
        innerSource.Part,
        0,
        fixedRotation,
        targetOriginX,
        targetOriginY);
      IPartPlacement outer = RotatedPlacementPreservingFutureOrigin(
        outerSource.Part,
        1,
        initialMovingRotation,
        alignmentReferenceRotation,
        targetOriginX,
        targetOriginY);
      DxfCommonLineArcCandidateFinder.Find(inner, outer, Gap, Tolerance).Should().BeEmpty(
        "the supplied non-cardinal poses have disjoint partial angular supports");

      var placements = new List<IPartPlacement> { inner, outer };
      var profiles = new Dictionary<int, SparrowNestService.DirectOrientationProfile>
      {
        // Part 0 intentionally has no profile: its arbitrary Sparrow pose is fixed. Part 1 is genuinely
        // Continuous, so the planner must derive its exact counterpart from source arcs alone.
        [1] = new SparrowNestService.DirectOrientationProfile(
          outerSource.Part,
          new[] { 0, 90, 180, 270 },
          continuous: true),
      };

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        Rules(),
        profiles,
        sheetW: 40D,
        sheetH: 40D,
        margin: 0D,
        commonLineGap: Gap,
        geometryTolerance: Tolerance,
        effort: NestEffort.Fast);

      SparrowNestService.CommonEdgeIntent intent = result.Edges.Should().ContainSingle(
        $"givenUp={result.GivenUp}; fallbacks={string.Join(" | ", result.Fallbacks.Select(item => item.Reason))}")
        .Subject;
      using (new AssertionScope())
      {
        Normalize(placements[0].Rotation).Should().BeApproximately(fixedRotation, 1e-6D);
        Normalize(placements[1].Rotation).Should().NotBeApproximately(initialMovingRotation, 1e-6D);
        new[] { 0D, 90D, 180D, 270D }
          .Min(cardinal => Math.Abs(Normalize(placements[1].Rotation) - cardinal))
          .Should().BeGreaterThan(1D,
            "no cardinal/sample pose overlaps the fixed narrow source arc");
        Record(intent, Gap).GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
        DxfCommonLineArcCandidateFinder.Find(
          placements[0],
          placements[1],
          Gap,
          Tolerance).Should().ContainSingle();
      }
    }

    [Fact]
    public void IncompatibleRadiusKeepsTheNormalGapAndOriginalPlacementIdentity()
    {
      (string innerPath, string outerPath) = this.WriteSemicirclePair(Gap + 0.05D, "wrong-radius");
      IPartPlacement inner = LoadPlacement(innerPath, 0, 10D, 10D);
      IPartPlacement outer = LoadPlacement(outerPath, 1, inner.MaxX + NormalGap, 10D);
      var placements = new List<IPartPlacement> { inner, outer };
      IPartPlacement[] references = placements.ToArray();
      double[] xs = placements.Select(placement => placement.X).ToArray();
      double[] ys = placements.Select(placement => placement.Y).ToArray();

      SparrowNestService.DirectCommonLineSnapResult result = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        Rules(),
        Profiles(inner.Part, outer.Part, new[] { 0 }),
        sheetW: 40D,
        sheetH: 30D,
        margin: 0D,
        commonLineGap: Gap,
        geometryTolerance: Tolerance,
        effort: NestEffort.Fast);

      using (new AssertionScope())
      {
        result.Edges.Should().BeEmpty();
        placements[0].Should().BeSameAs(references[0]);
        placements[1].Should().BeSameAs(references[1]);
        placements[0].X.Should().Be(xs[0]);
        placements[0].Y.Should().Be(ys[0]);
        placements[1].X.Should().Be(xs[1]);
        placements[1].Y.Should().Be(ys[1]);
        (placements[1].MinX - placements[0].MaxX).Should().BeApproximately(NormalGap, Tolerance);
      }
    }

    [Fact]
    public void ArcPoseSearchUsesItsCapturedCatalogsAndFinalRecordsRevalidateTheSource()
    {
      (string innerPath, string outerPath) = this.WriteSemicirclePair(Gap, "snapshots");
      IPartPlacement inner = LoadPlacement(innerPath, 0, 10D, 10D);
      IPartPlacement outer = LoadPlacement(outerPath, 1, 10.2D, 10D);
      DxfContourCatalog innerCatalog = DxfContourCatalog.Load(innerPath, Tolerance);
      DxfContourCatalog outerCatalog = DxfContourCatalog.Load(outerPath, Tolerance);
      DxfCommonLineArcCandidate expected = DxfCommonLineArcCandidateFinder.Find(
        inner, outer, Gap, Tolerance, innerCatalog, outerCatalog).Should().ContainSingle().Subject;

      WriteConcavePart(outerPath, 5.1D, 8D, -90D, 90D);

      DxfCommonLineArcCandidate captured = DxfCommonLineArcCandidateFinder.Find(
        inner, outer, Gap, Tolerance, innerCatalog, outerCatalog).Should().ContainSingle().Subject;
      captured.TranslationX.Should().Be(expected.TranslationX);
      captured.SharedLength.Should().Be(expected.SharedLength);
      DxfCommonLineArcCandidateFinder.Find(inner, outer, Gap, Tolerance).Should().BeEmpty();
      outer.X += captured.TranslationX;
      outer.Y += captured.TranslationY;
      CommonLineGeometryResolver.TryCreateCommonEdge(
        inner, captured.SpanA, outer, captured.SpanB, Gap, Tolerance, out _)
        .Should().BeFalse("a captured search proposal does not bypass final source validation");
    }

    private (string InnerPath, string OuterPath) WriteSemicirclePair(double radiusDifference, string suffix)
    {
      string innerPath = Path.Combine(this.directory, $"convex-{suffix}.dxf");
      string outerPath = Path.Combine(this.directory, $"concave-{suffix}.dxf");
      WriteConvexPart(innerPath, 5D);
      WriteConcavePart(outerPath, 5D + radiusDifference, 8D, -90D, 90D);
      return (innerPath, outerPath);
    }

    private static IPartPlacement LoadPlacement(string path, int source, double x, double y)
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      detail.TryConvertToNfp(source, out INfp part).Should().BeTrue();
      part.Name.Should().Be(path, "exact candidate discovery resolves its catalog through Part.Name");
      return new PartPlacement(part)
      {
        Id = source,
        Source = source,
        X = x,
        Y = y,
      };
    }

    private static IPartPlacement RotatedPlacement(
      INfp sourcePart,
      int source,
      double rotation,
      double x,
      double y)
      => new PartPlacement(sourcePart.Rotate(rotation))
      {
        Id = source,
        Source = source,
        Rotation = rotation,
        X = x,
        Y = y,
      };

    private static IPartPlacement RotatedPlacementPreservingFutureOrigin(
      INfp sourcePart,
      int source,
      double initialRotation,
      double futureRotation,
      double futureX,
      double futureY)
    {
      INfp initial = sourcePart.Rotate(initialRotation);
      INfp future = sourcePart.Rotate(futureRotation);
      double initialCenterX = (initial.MinX + initial.MaxX) / 2D;
      double initialCenterY = (initial.MinY + initial.MaxY) / 2D;
      double futureCenterX = (future.MinX + future.MaxX) / 2D;
      double futureCenterY = (future.MinY + future.MaxY) / 2D;
      return new PartPlacement(initial)
      {
        Id = source,
        Source = source,
        Rotation = initialRotation,
        X = futureX - initialCenterX + futureCenterX,
        Y = futureY - initialCenterY + futureCenterY,
      };
    }

    private static Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> Rules()
      => new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>
      {
        [0] = (CommonCuttingMode.Unrestricted, 0, "arc-direct", true, NormalGap),
        [1] = (CommonCuttingMode.Unrestricted, 1, "arc-direct", true, NormalGap),
      };

    private static Dictionary<int, SparrowNestService.DirectOrientationProfile> Profiles(
      INfp inner,
      INfp outer,
      IEnumerable<int> angles)
      => new Dictionary<int, SparrowNestService.DirectOrientationProfile>
      {
        [0] = new SparrowNestService.DirectOrientationProfile(inner, angles),
        [1] = new SparrowNestService.DirectOrientationProfile(outer, angles),
      };

    private static CommonEdge Record(SparrowNestService.CommonEdgeIntent intent, double gap)
      => CommonLineGeometryResolver.CreateCommonEdge(
        intent.PartA,
        intent.SpanA,
        intent.PartB,
        intent.SpanB,
        gap,
        Tolerance);

    private static SheetPlacement Sheet(
      IReadOnlyList<IPartPlacement> placements,
      CommonEdge edge,
      double gap)
      => new SheetPlacement(
        PlacementTypeEnum.BoundingBox,
        DeepNestLib.Sheet.NewSheet(0, 30, 30),
        placements,
        0D,
        SvgNest.Config.ClipperScale,
        requiresMergedLines: true,
        commonEdges: new[] { edge },
        commonLineGap: gap,
        geometryTolerance: Tolerance);

    private static CommonBoundarySpan Narrow(
      IPartPlacement placement,
      CommonBoundarySpan source,
      double from,
      double to)
    {
      DxfContourCatalog catalog = DxfContourCatalog.Load(placement.Part.Name);
      double range = source.EndParameter - source.StartParameter;
      return DxfCommonLineSpanResolver.CreateSpan(
        catalog,
        source.ContourIndex,
        source.PrimitiveIndex,
        source.StartParameter + (range * from),
        source.StartParameter + (range * to));
    }

    private static void WriteConvexPart(string path, double radius)
    {
      var polyline = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = -radius, Bulge = 1D },
        new DxfLwPolylineVertex { X = 0D, Y = radius },
        new DxfLwPolylineVertex { X = -2D, Y = radius },
        new DxfLwPolylineVertex { X = -2D, Y = -radius },
      })
      {
        IsClosed = true,
        Layer = "L0",
      };
      Write(path, polyline);
    }

    private static void WriteConcavePart(
      string path,
      double radius,
      double farX,
      double startDegrees,
      double endDegrees)
    {
      double start = startDegrees * Math.PI / 180D;
      double end = endDegrees * Math.PI / 180D;
      double startX = radius * Math.Cos(start);
      double startY = radius * Math.Sin(start);
      double endX = radius * Math.Cos(end);
      double endY = radius * Math.Sin(end);
      double clockwiseSweep = -(endDegrees - startDegrees) * Math.PI / 180D;
      double bulge = Math.Tan(clockwiseSweep / 4D);
      var polyline = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = startX, Y = startY },
        new DxfLwPolylineVertex { X = farX, Y = startY },
        new DxfLwPolylineVertex { X = farX, Y = endY },
        new DxfLwPolylineVertex { X = endX, Y = endY, Bulge = bulge },
      })
      {
        IsClosed = true,
        Layer = "L0",
      };
      Write(path, polyline);
    }

    private static void Write(string path, DxfEntity entity)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2007;
      dxf.Entities.Add(entity);
      dxf.Save(path, true);
    }

    private (string InnerPath, string OuterPath) WriteAllCurvedSectorPair(string suffix)
    {
      string innerPath = Path.Combine(this.directory, $"curved-sector-convex-{suffix}.dxf");
      string outerPath = Path.Combine(this.directory, $"curved-sector-concave-{suffix}.dxf");
      const double startDegrees = -8D;
      const double endDegrees = 8D;
      double start = startDegrees * Math.PI / 180D;
      double end = endDegrees * Math.PI / 180D;
      double commonBulge = Math.Tan((end - start) / 4D);

      var convex = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex
        {
          X = 5D * Math.Cos(start),
          Y = 5D * Math.Sin(start),
          Bulge = commonBulge,
        },
        new DxfLwPolylineVertex
        {
          X = 5D * Math.Cos(end),
          Y = 5D * Math.Sin(end),
          Bulge = 0.02D,
        },
        new DxfLwPolylineVertex { X = -3D, Y = 2D, Bulge = 0.02D },
        new DxfLwPolylineVertex { X = -3D, Y = -2D, Bulge = 0.02D },
      })
      {
        IsClosed = true,
        Layer = "L0",
      };

      double outerRadius = 5D + Gap;
      var concave = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex
        {
          X = outerRadius * Math.Cos(start),
          Y = outerRadius * Math.Sin(start),
          Bulge = 0.025D,
        },
        new DxfLwPolylineVertex { X = 8D, Y = -2D, Bulge = 0.025D },
        new DxfLwPolylineVertex { X = 8D, Y = 2D, Bulge = 0.025D },
        new DxfLwPolylineVertex
        {
          X = outerRadius * Math.Cos(end),
          Y = outerRadius * Math.Sin(end),
          Bulge = -commonBulge,
        },
      })
      {
        IsClosed = true,
        Layer = "L0",
      };

      Write(innerPath, convex);
      Write(outerPath, concave);
      return (innerPath, outerPath);
    }

    private static double Normalize(double angle)
    {
      double result = angle % 360D;
      return result < 0D ? result + 360D : result;
    }
  }
}
