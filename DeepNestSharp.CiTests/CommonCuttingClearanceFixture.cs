namespace DeepNestSharp.CiTests
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
  using DeepNestSharp.Ui.UserControls;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// Hand editing has to judge a pair the same way the nester did, or the operator opens a nest the engine
  /// built correctly and finds it painted red with the exports refused.
  /// <para>
  /// This is the half that used to be missing. A common-line part reached the viewer with its spacing
  /// already flattened to 0, so "may these touch" never had to be asked. Now the part keeps its real
  /// spacing and the mode answers instead, which means the viewer needs the mode too.
  /// </para>
  /// </summary>
  public class CommonCuttingClearanceFixture
  {
    private const string A = @"C:\parts\bracket.dxf";
    private const string B = @"C:\parts\plate.dxf";
    private const string Direct = "direct";

    [Fact]
    public void UnrestrictedSharesWithAnyOtherSharingPart()
    {
      DxfViewer.MayShare(CommonCuttingMode.Unrestricted, A, false, Direct, CommonCuttingMode.Unrestricted, B, false, Direct)
        .Should().BeTrue("unrestricted means any part that also allows it");
    }

    [Fact]
    public void NothingSharesWithAPartSetToNone()
    {
      DxfViewer.MayShare(CommonCuttingMode.Unrestricted, A, false, Direct, CommonCuttingMode.None, A, false, Direct)
        .Should().BeFalse("a part set to None keeps its clearance even from its own copies");
    }

    [Fact]
    public void SamePartSharesOnlyWithItsOwnDrawing()
    {
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, false, Direct, CommonCuttingMode.SamePart, A, false, Direct)
        .Should().BeTrue();
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, false, Direct, CommonCuttingMode.SamePart, B, false, Direct)
        .Should().BeFalse("a different drawing is a different part");
    }

    /// <summary>The stricter mode wins, whichever side it is on. Unrestricted does not get to drag a
    /// Same-part neighbour into a shared cut just by being permissive.</summary>
    [Fact]
    public void TheStricterModeDecidesWhicheverSideItIsOn()
    {
      DxfViewer.MayShare(CommonCuttingMode.Unrestricted, A, false, Direct, CommonCuttingMode.SamePart, B, false, Direct)
        .Should().BeFalse();
      DxfViewer.MayShare(CommonCuttingMode.SamePart, B, false, Direct, CommonCuttingMode.Unrestricted, A, false, Direct)
        .Should().BeFalse("and it must not depend on the argument order");
    }

    /// <summary>A mirrored copy is the other hand: same file, different part. Matches how the nester keys
    /// its share groups, on (path, mirrored).</summary>
    [Fact]
    public void AMirroredCopyIsADifferentPart()
    {
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, false, Direct, CommonCuttingMode.SamePart, A, true, Direct)
        .Should().BeFalse();
      DxfViewer.MayShare(CommonCuttingMode.SamePart, A, true, Direct, CommonCuttingMode.SamePart, A, true, Direct)
        .Should().BeTrue("two mirrored copies are each other's own kind");
    }

    [Fact]
    public void PathsAreComparedCaseInsensitively()
    {
      DxfViewer.MayShare(CommonCuttingMode.SamePart, @"C:\Parts\Bracket.DXF", false, Direct, CommonCuttingMode.SamePart, A, false, Direct)
        .Should().BeTrue("Windows paths are the same file in either case");
    }

    [Theory]
    [InlineData(null, "direct")]
    [InlineData("", "direct")]
    [InlineData("   ", "direct")]
    [InlineData("direct", "sheetcam:job.nest")]
    public void SharingRequiresTheSameNonEmptyContract(string domainA, string domainB)
    {
      DxfViewer.MayShare(
        CommonCuttingMode.Unrestricted, A, false, domainA,
        CommonCuttingMode.Unrestricted, B, false, domainB).Should().BeFalse();
    }

    [Fact]
    public void ContractComparisonIsCaseInsensitive()
    {
      DxfViewer.MayShare(
        CommonCuttingMode.Unrestricted, A, false, "SheetCam:C:\\Jobs\\A.nest",
        CommonCuttingMode.Unrestricted, B, false, "sheetcam:c:\\jobs\\a.NEST").Should().BeTrue();
    }

    /// <summary>
    /// The wiring, and the reason this commit cannot ship apart from the one before it. Two copies the
    /// nester deliberately placed touching must be asked for NO clearance; the same two parts without the
    /// mode must be asked for the full 0.25, which is what would have marked the whole layout red.
    /// </summary>
    [Fact]
    public void OnlyIntentionalPairsUseTheConfiguredCommonLineGap()
    {
      DxfViewer.ClearanceFor(0.25, 0.25, intentionalCommonEdge: true)
        .Should().Be(CommonLineDefaults.DefaultGapInches);
      DxfViewer.ClearanceFor(0.25, 0.25, intentionalCommonEdge: true, commonLineGap: 0.015)
        .Should().Be(0.015);
      DxfViewer.ClearanceFor(0.25, 0.25, intentionalCommonEdge: false).Should().BeApproximately(0.25, 1e-9);
      DxfViewer.ClearanceFor(0.5, 0.1, intentionalCommonEdge: false).Should().BeApproximately(0.3, 1e-9, "still the average of the two");
    }

    [Fact]
    public void OnlySameJobSheetCamPairsKeepTheLegacyMeasuredKerfClearance()
    {
      DxfViewer.TryLegacySheetCamClearance(
        CommonCuttingMode.Unrestricted, A, false, "sheetcam:C:\\jobs\\one.nest", 0.03,
        CommonCuttingMode.Unrestricted, B, false, "SHEETCAM:c:\\JOBS\\ONE.NEST", 0.04,
        out double clearance).Should().BeTrue();
      clearance.Should().Be(0.04);

      DxfViewer.TryLegacySheetCamClearance(
        CommonCuttingMode.Unrestricted, A, false, Direct, 0.03,
        CommonCuttingMode.Unrestricted, B, false, Direct, 0.04,
        out _).Should().BeFalse("direct nominal contours require an explicit CommonEdge record");
      DxfViewer.TryLegacySheetCamClearance(
        CommonCuttingMode.Unrestricted, A, false, "sheetcam:one.nest", 0.03,
        CommonCuttingMode.Unrestricted, B, false, "sheetcam:two.nest", 0.04,
        out _).Should().BeFalse("toolpaths from different jobs cannot share a return cut");
    }

    [Fact]
    public void CommonLineClusterMembersAreLockedButOrdinaryPlacementsRemainEditable()
    {
      var record = new CommonEdge(
        10, 11, 1, 3,
        new CommonLinePoint(10.005, 0),
        new CommonLinePoint(10.005, 10),
        0.01);

      DxfViewer.CanManuallyEdit(10, new[] { record }).Should().BeFalse();
      DxfViewer.CanManuallyEdit(11, new[] { record }).Should().BeFalse();
      DxfViewer.CanManuallyEdit(12, new[] { record }).Should().BeTrue();
      DxfViewer.CanManuallyEdit(10, null).Should().BeTrue();
    }

    [Fact]
    public void AGeometricallyPlausibleButAbsoluteStaleRecordDoesNotValidate()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      var square = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(0, 10),
      });
      var a = new PartPlacement(square) { Id = 1, X = 0, Y = 0 };
      var b = new PartPlacement(square) { Id = 2, X = 10 + gap, Y = 0 };
      CommonEdge rebuilt = CommonLineGeometryResolver.CreateCommonEdge(a, 1, b, 3, gap, tolerance);
      var stale = new CommonEdge(
        rebuilt.PartAId,
        rebuilt.PartBId,
        rebuilt.EdgeAIndex,
        rebuilt.EdgeBIndex,
        new CommonLinePoint(rebuilt.Start.X + 1, rebuilt.Start.Y),
        new CommonLinePoint(rebuilt.End.X + 1, rebuilt.End.Y),
        rebuilt.Gap);

      DxfViewer.SameUndirectedRecord(rebuilt, rebuilt, tolerance).Should().BeTrue();
      DxfViewer.SameUndirectedRecord(stale, rebuilt, tolerance).Should().BeFalse(
        "the indexed geometry alone is insufficient when the persisted absolute seam moved");

      var sheet = new SheetPlacement(
        PlacementTypeEnum.Gravity,
        Sheet.NewSheet(1, 40, 20),
        new IPartPlacement[] { a, b },
        0,
        requiresMergedLines: false,
        commonEdges: new[] { stale },
        commonLineGap: gap,
        geometryTolerance: tolerance);
      var valid = DxfViewer.ValidRecordedEdges(sheet, gap, tolerance);
      valid.Should().BeEmpty();
      var unfit = new PlacementAcceptance.Unfit();
      DxfViewer.MarkInvalidCommonEdgeRecords(sheet, valid.Count, unfit);
      unfit.All.Should().BeEquivalentTo(new IPartPlacement[] { a, b },
        "stale common-line metadata is itself unfit to cut even if ordinary spacing were zero");
    }

    [Fact]
    public void ParametricCommonEdgeRemainsValidInTheViewerAcceptancePath()
    {
      const double gap = 0.01;
      const double tolerance = 0.0001;
      var square = new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(0, 0),
        new SvgPoint(10, 0),
        new SvgPoint(10, 10),
        new SvgPoint(0, 10),
      });
      var a = new PartPlacement(square) { Id = 1, X = 0, Y = 0 };
      var b = new PartPlacement(square) { Id = 2, X = 10 + gap, Y = 0 };
      CommonEdge partial = CommonLineGeometryResolver.CreateCommonEdge(
        a,
        1,
        0.25,
        0.75,
        b,
        3,
        0.25,
        0.75,
        gap,
        tolerance);
      var sheet = new SheetPlacement(
        PlacementTypeEnum.Gravity,
        Sheet.NewSheet(1, 40, 20),
        new IPartPlacement[] { a, b },
        0,
        requiresMergedLines: false,
        commonEdges: new[] { partial },
        commonLineGap: gap,
        geometryTolerance: tolerance);

      IReadOnlyList<CommonEdgeClearance.EdgeReference> valid = DxfViewer.ValidRecordedEdges(
        sheet,
        gap,
        tolerance);

      valid.Should().ContainSingle(
        "schema-2 intervals must not be reinterpreted through the legacy full-edge overload");
    }

    [Fact]
    public void SourceBackedArcRecordMustMatchItsPersistedSchemaSpansAndCenterCurve()
    {
      const double gap = 0.01D;
      const double tolerance = 0.0001D;
      string directory = Path.Combine(
        Path.GetTempPath(),
        "SheetNestTests",
        Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      string convexPath = Path.Combine(directory, "viewer-convex.dxf");
      string concavePath = Path.Combine(directory, "viewer-concave.dxf");

      try
      {
        WriteConvexArcPart(convexPath, 5D);
        WriteConcaveArcPart(concavePath, 5D + gap, 8D);
        IPartPlacement convex = LoadDxfPlacement(convexPath, 31);
        IPartPlacement concave = LoadDxfPlacement(concavePath, 32);
        DxfContourCatalog convexCatalog = DxfContourCatalog.Load(convexPath);
        DxfContourCatalog concaveCatalog = DxfContourCatalog.Load(concavePath);
        DxfSourceContour convexOuter = convexCatalog.Contours.Single(contour => !contour.IsChild);
        DxfSourceContour concaveOuter = concaveCatalog.Contours.Single(contour => !contour.IsChild);
        CommonBoundarySpan convexSpan = DxfCommonLineSpanResolver.CreateSpan(
          convexCatalog,
          convexOuter.Index,
          convexOuter.Primitives.Single(primitive =>
            primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index);
        CommonBoundarySpan concaveSpan = DxfCommonLineSpanResolver.CreateSpan(
          concaveCatalog,
          concaveOuter.Index,
          concaveOuter.Primitives.Single(primitive =>
            primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index);
        CommonEdge edge = CommonLineGeometryResolver.CreateCommonEdge(
          convex,
          convexSpan,
          concave,
          concaveSpan,
          gap,
          tolerance);

        SheetPlacement SheetWith(CommonEdge record) => new SheetPlacement(
          PlacementTypeEnum.BoundingBox,
          Sheet.NewSheet(0, 30, 30),
          new[] { convex, concave },
          0D,
          SvgNest.Config.ClipperScale,
          requiresMergedLines: true,
          commonEdges: new[] { record },
          commonLineGap: gap,
          geometryTolerance: tolerance);

        SheetPlacement reloaded = SheetPlacement.FromJson(SheetWith(edge).ToJson());
        CommonEdge persisted = reloaded.CommonEdges.Should().ContainSingle().Subject;
        IReadOnlyList<CommonEdgeClearance.EdgeReference> valid = DxfViewer.ValidRecordedEdges(
          reloaded,
          gap,
          tolerance);
        valid.Should().ContainSingle(
          "the viewer must rehydrate a persisted exact ARC from its fingerprinted source spans");
        valid[0].SpanA.SourceFingerprint.Should().Be(edge.SpanA.SourceFingerprint);
        valid[0].SpanB.SourceFingerprint.Should().Be(edge.SpanB.SourceFingerprint);

        CommonCenterCurve curve = persisted.CenterCurve;
        var tamperedCurve = new CommonCenterCurve(
          curve.GeometryKind,
          curve.Start,
          curve.End,
          curve.Center,
          curve.Radius + 0.25D,
          curve.StartAngleRadians,
          curve.SweepRadians);
        CommonEdge tampered = CopyEdge(persisted, centerCurve: tamperedCurve);
        DxfViewer.ValidRecordedEdges(SheetWith(tampered), gap, tolerance).Should().BeEmpty(
          "matching endpoints are not enough when the recorded ARC radius was altered");

        CommonEdge wrongKind = CopyEdge(persisted, geometryKind: CommonLineGeometryKind.Line);
        DxfViewer.ValidRecordedEdges(SheetWith(wrongKind), gap, tolerance).Should().BeEmpty(
          "the top-level geometry kind is part of the schema-2 contract");

        var tamperedSpan = new CommonBoundarySpan(
          persisted.SpanA.ContourIndex,
          persisted.SpanA.PrimitiveIndex,
          persisted.SpanA.StartParameter,
          persisted.SpanA.EndParameter,
          persisted.SpanA.SourceFingerprint + "-tampered",
          persisted.SpanA.ContourFingerprint);
        CommonEdge wrongSpan = CopyEdge(persisted, spanA: tamperedSpan);
        DxfViewer.ValidRecordedEdges(SheetWith(wrongSpan), gap, tolerance).Should().BeEmpty(
          "a stale source fingerprint must fail closed before clearance is relaxed");

        WriteConvexArcPart(convexPath, 4D);
        File.SetLastWriteTimeUtc(convexPath, DateTime.UtcNow.AddSeconds(2D));
        DxfViewer.ValidRecordedEdges(reloaded, gap, tolerance).Should().BeEmpty(
          "editing the source drawing invalidates the persisted ARC relationship");
      }
      finally
      {
        try
        {
          Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
          // Cleanup must not hide a geometry assertion on a transient file-system lock.
        }
      }
    }

    /// <summary>
    /// The nesting polygon tessellates an arc into chords that run INSIDE the true curve, by up to
    /// DxfParser.CurveChordTolerance. Taking that polygon as the cheap pre-test would let a pair whose
    /// arcs really do break the clearance answer "there is room", and the parts come off the machine
    /// closer than the job asked for.
    /// <para>
    /// Only a pair that shares no seam can reach that pre-test: a seamed pair sits at the common-line
    /// gap, which the polygon test always calls too close, so the question never gets asked. Hence the
    /// three-part chain, and hence the two ends of it are what this measures.
    /// </para>
    /// </summary>
    [Fact]
    public void AnExactArcBreachIsCaughtEvenWhereTheNestingPolygonSeesRoom()
    {
      const double gap = 0.01D;
      const double tolerance = 0.0001D;
      const double clearance = 0.10D;
      const double barHeight = 2D;
      const double partWidth = 20D;

      // Sized so the arc tessellates into an ODD number of chords, which puts its apex halfway along
      // one of them instead of on a vertex. On an even count a vertex lands on the apex, the polygon
      // loses nothing there, and the pre-test would be entitled to its answer.
      const double partHeight = 22.68D;
      const double radius = 200D;
      double sliver = PlacementCollision.PlacementNoise;
      string directory = Path.Combine(
        Path.GetTempPath(),
        "SheetNestTests",
        Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      string barPath = Path.Combine(directory, "chain-bar.dxf");
      string arcedPath = Path.Combine(directory, "chain-arced.dxf");
      string plainPath = Path.Combine(directory, "chain-plain.dxf");

      try
      {
        WriteRectanglePart(barPath, 60D, barHeight);
        WriteRightBulgedPart(arcedPath, partWidth, partHeight, radius);
        WriteRectanglePart(plainPath, partWidth, partHeight);
        IPartPlacement bar = LoadDxfPlacement(barPath, 41);
        IPartPlacement arced = LoadDxfPlacement(arcedPath, 42);
        IPartPlacement plain = LoadDxfPlacement(plainPath, 43);
        arced.Y = barHeight + gap;
        plain.Y = barHeight + gap;
        DxfContourCatalog barCatalog = DxfContourCatalog.Load(barPath);
        DxfContourCatalog arcedCatalog = DxfContourCatalog.Load(arcedPath);
        DxfContourCatalog plainCatalog = DxfContourCatalog.Load(plainPath);

        // How far the polygon falls short of the arc it stands in for. Measured rather than written
        // down: the angular step is adaptive and clamped (DxfParser.ArcStepDegrees), so a figure
        // worked out by hand here would rot silently the day that changes.
        DxfResolvedBoundarySpan arcWorld = ResolveOnly(
          arcedCatalog,
          arced,
          CommonLineGeometryKind.CircularArc);
        double trueApexX = arcWorld.Center.X + arcWorld.Radius;
        double polygonApexX = arced.PlacedPart.Points.Max(point => point.X);
        double deviation = trueApexX - polygonApexX;
        deviation.Should().BeInRange(0.02D, 0.06D,
          "the polygon has to be cutting this corner by a real distance, or the pre-test would be "
          + "right to trust it and this test would prove nothing");

        // Half the shortfall each way: the true arc lands inside the clearance and its chords outside.
        plain.X = trueApexX + clearance - (deviation / 2D);
        var seams = new[]
        {
          SeamAgainstBar(arcedCatalog, arced, barCatalog, bar),
          SeamAgainstBar(plainCatalog, plain, barCatalog, bar),
        };

        CommonEdgeClearance.TooClose(arced, bar, clearance, sliver, seams, gap, tolerance)
          .Should().BeFalse("the seam onto the bar is intentional and holds the pair together");
        PlacementCollision.TooClose(arced.PlacedPart, plain.PlacedPart, clearance, sliver)
          .Should().BeFalse(
            "the nesting polygon has to be the one saying there is room, or nothing here is being tested");

        var catalogs = new Dictionary<string, DxfContourCatalog>(StringComparer.OrdinalIgnoreCase)
        {
          [Path.GetFullPath(arcedPath)] = arcedCatalog,
          [Path.GetFullPath(plainPath)] = plainCatalog,
        };
        CommonEdgeClearance.TooClose(
          arced,
          plain,
          clearance,
          sliver,
          Array.Empty<CommonEdgeClearance.EdgeReference>(),
          gap,
          tolerance,
          catalogs)
          .Should().BeTrue(
            "a rejected common-line candidate must still keep the exact DXF boundary for Part Space");

        CommonEdgeClearance.TooClose(arced, plain, clearance, sliver, seams, gap, tolerance)
          .Should().BeTrue(
            "the true arc breaks the clearance, and a cluster member is measured against its exact "
            + "source geometry instead of the chords the nester packed with");

        CommonEdgeClearance.TooClose(arced, plain, clearance, sliver, seams.Take(1).ToArray(), gap, tolerance)
          .Should().BeTrue(
            "an exact cluster boundary must also respect normal clearance against a part outside its cluster");
      }
      finally
      {
        DeleteQuietly(directory);
      }
    }

    private static CommonEdge CopyEdge(
      CommonEdge source,
      CommonLineGeometryKind? geometryKind = null,
      CommonBoundarySpan spanA = null,
      CommonCenterCurve centerCurve = null)
      => new CommonEdge(
        source.PartAId,
        source.PartBId,
        source.EdgeAIndex,
        source.EdgeBIndex,
        source.Start,
        source.End,
        source.Gap,
        source.SchemaVersion,
        geometryKind ?? source.GeometryKind,
        spanA ?? source.SpanA,
        source.SpanB,
        centerCurve ?? source.CenterCurve);

    /// <summary>The one primitive of that kind on the outside contour, in world coordinates.</summary>
    private static DxfResolvedBoundarySpan ResolveOnly(
      DxfContourCatalog catalog,
      IPartPlacement placement,
      CommonLineGeometryKind kind)
    {
      DxfSourceContour contour = catalog.Contours.Single(item => !item.IsChild);
      DxfSourcePrimitive primitive = contour.Primitives.Single(item => item.GeometryKind == kind);
      return DxfCommonLineSpanResolver.Resolve(
        catalog,
        DxfCommonLineSpanResolver.CreateSpan(catalog, contour.Index, primitive.Index),
        placement);
    }

    /// <summary>
    /// Seams a part's whole bottom edge onto the stretch of the bar's top edge that faces it. The
    /// stretch has to be a partial span: the seam resolver requires matching lengths, and the bar is
    /// longer than either part on it.
    /// </summary>
    private static CommonEdgeClearance.EdgeReference SeamAgainstBar(
      DxfContourCatalog partCatalog,
      IPartPlacement part,
      DxfContourCatalog barCatalog,
      IPartPlacement bar)
    {
      (DxfSourceContour partContour, DxfSourcePrimitive partBottom) = HorizontalSourceLine(
        partCatalog,
        lowest: true);
      CommonBoundarySpan partSpan = DxfCommonLineSpanResolver.CreateSpan(
        partCatalog,
        partContour.Index,
        partBottom.Index);
      DxfResolvedBoundarySpan partWorld = DxfCommonLineSpanResolver.Resolve(
        partCatalog,
        partSpan,
        part);

      (DxfSourceContour barContour, DxfSourcePrimitive barTop) = HorizontalSourceLine(
        barCatalog,
        lowest: false);
      DxfResolvedBoundarySpan barWorld = DxfCommonLineSpanResolver.Resolve(
        barCatalog,
        DxfCommonLineSpanResolver.CreateSpan(barCatalog, barContour.Index, barTop.Index),
        bar);
      double first = ParameterAlong(barWorld, partWorld.Start);
      double second = ParameterAlong(barWorld, partWorld.End);
      CommonBoundarySpan barSpan = DxfCommonLineSpanResolver.CreateSpan(
        barCatalog,
        barContour.Index,
        barTop.Index,
        Math.Min(first, second),
        Math.Max(first, second));

      return new CommonEdgeClearance.EdgeReference(
        part,
        -1,
        bar,
        -1,
        spanA: partSpan,
        spanB: barSpan);
    }

    private static (DxfSourceContour Contour, DxfSourcePrimitive Primitive) HorizontalSourceLine(
      DxfContourCatalog catalog,
      bool lowest)
    {
      DxfSourceContour contour = catalog.Contours.Single(item => !item.IsChild);
      IEnumerable<DxfSourcePrimitive> horizontal = contour.Primitives.Where(item =>
        item.GeometryKind == CommonLineGeometryKind.Line
        && Math.Abs(item.Start.Y - item.End.Y) <= 1e-9);
      return (contour, lowest
        ? horizontal.OrderBy(item => item.Start.Y).First()
        : horizontal.OrderByDescending(item => item.Start.Y).First());
    }

    private static double ParameterAlong(DxfResolvedBoundarySpan line, CommonLinePoint point)
    {
      double dx = line.End.X - line.Start.X;
      double dy = line.End.Y - line.Start.Y;
      return ((((point.X - line.Start.X) * dx) + ((point.Y - line.Start.Y) * dy))
        / ((dx * dx) + (dy * dy)));
    }

    private static void WriteRectanglePart(string path, double width, double height)
      => WriteDxf(
        path,
        new DxfLwPolyline(new[]
        {
          new DxfLwPolylineVertex { X = 0D, Y = 0D },
          new DxfLwPolylineVertex { X = width, Y = 0D },
          new DxfLwPolylineVertex { X = width, Y = height },
          new DxfLwPolylineVertex { X = 0D, Y = height },
        })
        {
          IsClosed = true,
          Layer = "L0",
        });

    /// <summary>A rectangle whose right side is a shallow arc. The bulge is positive, which for this
    /// winding puts the arc outside its chord instead of biting into the material.</summary>
    private static void WriteRightBulgedPart(
      string path,
      double width,
      double height,
      double radius)
      => WriteDxf(
        path,
        new DxfLwPolyline(new[]
        {
          new DxfLwPolylineVertex { X = 0D, Y = 0D },
          new DxfLwPolylineVertex
          {
            X = width,
            Y = 0D,
            Bulge = Math.Tan(Math.Asin((height / 2D) / radius) / 2D),
          },
          new DxfLwPolylineVertex { X = width, Y = height },
          new DxfLwPolylineVertex { X = 0D, Y = height },
        })
        {
          IsClosed = true,
          Layer = "L0",
        });

    private static void DeleteQuietly(string directory)
    {
      try
      {
        Directory.Delete(directory, recursive: true);
      }
      catch (IOException)
      {
        // Cleanup must not hide a geometry assertion on a transient file-system lock.
      }
    }

    private static IPartPlacement LoadDxfPlacement(string path, int id)
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(path));
      detail.TryConvertToNfp(id, out INfp part).Should().BeTrue();
      return new PartPlacement(part) { Id = id, Source = id };
    }

    private static void WriteConvexArcPart(string path, double radius)
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
      WriteDxf(path, polyline);
    }

    private static void WriteConcaveArcPart(string path, double radius, double farX)
    {
      var polyline = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = -radius },
        new DxfLwPolylineVertex { X = farX, Y = -radius },
        new DxfLwPolylineVertex { X = farX, Y = radius },
        new DxfLwPolylineVertex { X = 0D, Y = radius, Bulge = -1D },
      })
      {
        IsClosed = true,
        Layer = "L0",
      };
      WriteDxf(path, polyline);
    }

    private static void WriteDxf(string path, DxfEntity entity)
    {
      var file = new DxfFile();
      file.Header.Version = DxfAcadVersion.R2007;
      file.Entities.Add(entity);
      file.Save(path, true);
    }
  }
}
