namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
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
  using Xunit.Abstractions;

  /// <summary>
  /// Hermetic coverage for the model-space cut geometry in Hodgson.dxf. The original file is mostly
  /// CAD tables and paper-space records; its part is exactly one closed twelve-vertex LWPOLYLINE.
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public sealed class HodgsonDirectCommonLineFixture : IDisposable
  {
    private const double NormalPartGap = 0.30D;
    private const double CommonLineGap = 0.010D;
    private const double GeometryTolerance = 0.0001D;
    private const double HalfCircleBulge = 0.9999999999999998D;
    private readonly ITestOutputHelper output;
    private readonly string tempDirectory;
    private readonly string sourcePath;

    public HodgsonDirectCommonLineFixture(ITestOutputHelper output)
    {
      this.output = output;
      this.tempDirectory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.tempDirectory);
      this.sourcePath = Path.Combine(this.tempDirectory, "hodgson-minimal.dxf");
      CreateExactHodgsonDxf(this.sourcePath);
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
    public void ExactHodgsonPolylineImportsAsOneCurvedOuterWithoutHoles()
    {
      DxfLwPolyline source = DxfFile.Load(this.sourcePath).Entities
        .Should().ContainSingle().Subject
        .Should().BeOfType<DxfLwPolyline>().Subject;
      source.IsClosed.Should().BeTrue();
      source.Layer.Should().Be("L0");
      source.Vertices.Should().HaveCount(12);
      source.Vertices.Where(vertex => IsNonZero(vertex.Bulge)).Should().HaveCount(2);
      source.Vertices.Where(vertex => IsNonZero(vertex.Bulge)).Should().OnlyContain(vertex =>
        Math.Abs(Math.Abs(vertex.Bulge) - 1D) <= 1e-12);
      (double X, double Y, double Bulge)[] expected = ExactHodgsonVertices();
      for (int i = 0; i < expected.Length; i++)
      {
        source.Vertices[i].X.Should().BeApproximately(expected[i].X, 1e-12);
        source.Vertices[i].Y.Should().BeApproximately(expected[i].Y, 1e-12);
        source.Vertices[i].Bulge.Should().BeApproximately(expected[i].Bulge, 1e-12);
      }

      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(this.sourcePath));
      detail.Outers.Should().ContainSingle(contour => !contour.IsChild);
      detail.TryConvertToNfp(0, out INfp part).Should().BeTrue();
      using (new AssertionScope())
      {
        part.Children.Should().BeEmpty();
        part.MinX.Should().BeApproximately(0D, GeometryTolerance);
        part.MaxX.Should().BeApproximately(10D, GeometryTolerance);
        part.MinY.Should().BeApproximately(0D, GeometryTolerance);
        part.MaxY.Should().BeApproximately(1.375D, GeometryTolerance);
        part.Should().BeOfType<NoFitPolygon>()
          .Which.GetCurvedSegments().Should().Contain(isCurved => isCurved);
      }
    }

    [Fact]
    public async Task PlannerTurnsOneNinetyDegreeCopyToExportOneCompleteTenInchSeam()
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(this.sourcePath));
      detail.TryConvertToNfp(0, out INfp leftSource).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp rightSource).Should().BeTrue();
      INfp leftGeometry = leftSource.Rotate(90D);
      INfp rightGeometry = rightSource.Rotate(90D);

      var left = new PartPlacement(leftGeometry)
      {
        Source = 0,
        Rotation = leftGeometry.Rotation,
        X = 2D,
        Y = 1D - leftGeometry.MinY,
      };
      var right = new PartPlacement(rightGeometry)
      {
        Source = 0,
        Rotation = rightGeometry.Rotation,
        X = left.MaxX + NormalPartGap - rightGeometry.MinX,
        Y = left.MinY - rightGeometry.MinY,
      };
      var placements = new List<IPartPlacement> { left, right };
      HorizontalGap(left, right).Should().BeApproximately(NormalPartGap, 1e-9);

      SparrowNestService.DirectCommonLineSnapResult snapped = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        new Dictionary<int, SparrowNestService.DirectOrientationProfile>
        {
          [0] = new SparrowNestService.DirectOrientationProfile(leftSource, new[] { 90, 270 }),
        },
        sheetW: 20D,
        sheetH: 15D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      using (new AssertionScope())
      {
        placements.Select(placement => NormalizeRotation(placement.Rotation)).OrderBy(angle => angle)
          .Should().Equal(new[] { 90D, 270D },
            "the full straight ten-inch seam requires exactly one initially identical copy to turn around");
        snapped.Edges.Should().ContainSingle();
        snapped.Fallbacks.Should().BeEmpty();
        snapped.GivenUp.Should().Be(0);
      }

      IPartPlacement placedLeft = placements.OrderBy(placement => placement.PlacedPart.MinX).First();
      IPartPlacement placedRight = placements.OrderBy(placement => placement.PlacedPart.MinX).Last();
      HorizontalGap(placedLeft, placedRight).Should().BeApproximately(CommonLineGap, 1e-8);
      SheetPlacement sheet = BuildSheet(placements, snapped.Edges.Single(), 20, 15);
      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(sheet)
        .Clusters.Should().ContainSingle().Subject;
      CommonLinePath boundary = cluster.OuterBoundaries.Should().ContainSingle().Subject;
      CommonLineSegment seam = cluster.CommonLines.Should().ContainSingle().Subject;
      double expectedX = placedLeft.MaxX + (CommonLineGap / 2D);

      using (new AssertionScope())
      {
        boundary.IsClosed.Should().BeTrue();
        AssertFourHalfCircleBulges(boundary.Bulges);
        seam.Start.X.Should().BeApproximately(expectedX, GeometryTolerance);
        seam.End.X.Should().BeApproximately(expectedX, GeometryTolerance);
        Math.Min(seam.Start.Y, seam.End.Y).Should().BeApproximately(placedLeft.MinY, GeometryTolerance);
        Math.Max(seam.Start.Y, seam.End.Y).Should().BeApproximately(placedLeft.MaxY, GeometryTolerance);
        Distance(seam.Start, seam.End).Should().BeApproximately(10D, GeometryTolerance);
      }

      using var output = new MemoryStream();
      await sheet.ExportDxf(output, mergeLines: false, differentiateChildren: false);
      output.Position = 0;
      DxfFile exported = DxfFile.Load(output);

      exported.Entities.Should().HaveCount(2,
        "the cluster must contain only its closed outer boundary and its one resolved seam");
      exported.Entities.Where(entity => entity.Layer == DxfExporter.CutInnerLayer)
        .Should().BeEmpty("Hodgson has no hole contours");
      DxfLwPolyline outer = exported.Entities
        .Where(entity => entity.Layer == DxfExporter.CutOuterLayer)
        .Should().ContainSingle().Subject
        .Should().BeOfType<DxfLwPolyline>().Subject;
      outer.IsClosed.Should().BeTrue();
      AssertFourHalfCircleBulges(outer.Vertices.Select(vertex => vertex.Bulge));

      DxfLine exportedSeam = exported.Entities
        .Where(entity => entity.Layer == DxfExporter.CommonLineLayer)
        .Should().ContainSingle().Subject
        .Should().BeOfType<DxfLine>().Subject;
      exportedSeam.P1.X.Should().BeApproximately(expectedX, GeometryTolerance);
      exportedSeam.P2.X.Should().BeApproximately(expectedX, GeometryTolerance);
      DxfDistance(exportedSeam.P1, exportedSeam.P2).Should().BeApproximately(10D, GeometryTolerance);
      ContainsVerticalOverlap(outer, expectedX, placedLeft.MinY, placedLeft.MaxY).Should().BeFalse(
        "neither a complete nor a fragmented original edge may remain under COMMON_LINE");
    }

    [Fact]
    public void FixedOrientationCopiesCanShareTheOnePointThreeSevenFiveInchSide()
    {
      IRawDetail detail = new NestExecutionHelper().LoadRawDetail(new FileInfo(this.sourcePath));
      detail.TryConvertToNfp(0, out INfp first).Should().BeTrue();
      detail.TryConvertToNfp(0, out INfp second).Should().BeTrue();
      var placements = new List<IPartPlacement>
      {
        new PartPlacement(first) { Source = 0, X = 1D, Y = 1D },
        new PartPlacement(second) { Source = 0, X = 11D + NormalPartGap, Y = 1D },
      };

      SparrowNestService.DirectCommonLineSnapResult snapped = SparrowNestService.SnapDirectCommonLineEdges(
        placements,
        DirectRules(),
        sheetW: 25D,
        sheetH: 10D,
        margin: 0D,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);

      HorizontalGap(placements[0], placements[1]).Should().BeApproximately(CommonLineGap, 1e-8);
      SparrowNestService.CommonEdgeIntent intent = snapped.Edges.Should().ContainSingle().Subject;
      SheetPlacement sheet = BuildSheet(placements, intent, 25, 10);
      CommonLineCluster cluster = CommonLineGeometryResolver.Resolve(sheet)
        .Clusters.Should().ContainSingle().Subject;
      CommonLineSegment seam = cluster.CommonLines.Should().ContainSingle().Subject;
      Distance(seam.Start, seam.End).Should().BeApproximately(1.375D, GeometryTolerance);
      cluster.OuterBoundaries.Should().ContainSingle().Which.IsClosed.Should().BeTrue();
      AssertFourHalfCircleBulges(cluster.OuterBoundaries.Single().Bulges);
    }

    [Fact]
    public void DirectPlannerKeepsBothRequestedHodgsonOrientationsAvailable()
    {
      int code = SparrowNestService.CommonLineRotationCode(
        RotationCodes.Rot90And270,
        requiresMergedLines: true);

      RotationCodes.PermittedSet(code).Should().Equal(new[] { 90, 270 },
          "the packing planner must be able to hand the direct snapper the two baked orientations");
    }

    [SparrowTheory]
    [InlineData(15)]
    [InlineData(25)]
    [InlineData(50)]
    public void RealSparrowPlacesRequestedCopiesAndResolvesCompleteHodgsonSeams(int quantity)
    {
      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        throw new InvalidOperationException("The native Hodgson regression requires SPARROW_EXE.");
      }

      var part = new RasterPartInfo
      {
        Path = Environment.GetEnvironmentVariable("SHEETNEST_HODGSON_DXF") ?? this.sourcePath,
        Quantity = quantity,
        Rotations = RotationCodes.Rot90And270,
        Spacing = 0.25D,
        Cc = CommonCuttingMode.Unrestricted,
        CommonCuttingDomain = "direct",
        RequiresMergedLines = true,
      };
      long sparrowBefore = SparrowNestService.DiagSparrowMs;
      long mapBefore = SparrowNestService.DiagMapMs;
      SparrowNestService.DiagCommonLineTimings.Clear();
      var watch = Stopwatch.StartNew();
      INestResult result = SparrowNestService.Nest(
        new[] { part },
        sheetWin: 42D,
        sheetHin: 31D,
        rotations: RotationCodes.Rot90And270,
        spacing: 0.25D,
        margin: 0.25D,
        timeLimitSec: 5,
        sparrowExePath: exe,
        error: out string error,
        effort: NestEffort.Fast,
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);
      watch.Stop();

      result.Should().NotBeNull($"the real Sparrow nest must succeed (error={error})");
      List<ISheetPlacement> sheets = result.UsedSheets.ToList();
      this.output.WriteLine(FormattableString.Invariant(
        $"HODGSON: elapsed={watch.ElapsedMilliseconds}ms sparrow={SparrowNestService.DiagSparrowMs - sparrowBefore}ms map={SparrowNestService.DiagMapMs - mapBefore}ms sheets={sheets.Count} seams={sheets.Sum(sheet => sheet.CommonEdges?.Count ?? 0)}"));
      foreach (string timing in SparrowNestService.DiagCommonLineTimings)
      {
        this.output.WriteLine(timing);
        if (Environment.GetEnvironmentVariable("SHEETNEST_ASSERT_COMMON_LINE_PERF") == "1")
        {
          long milliseconds = System.Text.RegularExpressions.Regex.Matches(timing, @"=(\d+)ms")
            .Cast<System.Text.RegularExpressions.Match>().Sum(match => long.Parse(match.Groups[1].Value));
          milliseconds.Should().BeLessThan(quantity >= 50 ? 30000 : 10000,
            "the dedicated Release performance job must meet its common-line processing target");
        }
      }

      using (new AssertionScope())
      {
        sheets.Sum(sheet => sheet.PartPlacements.Count).Should().Be(quantity);
        result.UnplacedParts.Should().BeEmpty();
        if (quantity <= 25)
        {
          sheets.SelectMany(sheet => sheet.CommonEdges ?? Array.Empty<CommonEdge>())
            .Should().Contain(edge => Distance(edge.Start, edge.End) >= 10D - GeometryTolerance,
              "the known 15/25-copy fixtures have a material-safe complete ten-inch seam");
        }
      }

      bool resolvedCompleteTenInchSeam = false;
      foreach (ISheetPlacement sheet in sheets)
      {
        CommonLineGeometryResult resolved = CommonLineGeometryResolver.Resolve(sheet);
        foreach (CommonLineCluster cluster in resolved.Clusters)
        {
          cluster.OuterBoundaries.Should().NotBeEmpty();
          cluster.OuterBoundaries.Should().OnlyContain(boundary => boundary.IsClosed);
        }

        List<CommonLineSegment> seams = resolved.Clusters.SelectMany(cluster => cluster.CommonLines).ToList();
        for (int i = 0; i < seams.Count; i++)
        {
          for (int j = i + 1; j < seams.Count; j++)
          {
            CollinearOverlap(seams[i], seams[j]).Should().BeLessOrEqualTo(GeometryTolerance,
              "the resolved cut graph must contain each physical seam exactly once, even when split at nodes");
          }
        }

        foreach (CommonEdge record in (sheet.CommonEdges ?? Array.Empty<CommonEdge>())
          .Where(edge => Distance(edge.Start, edge.End) >= 10D - GeometryTolerance))
        {
          resolvedCompleteTenInchSeam |= CoveredLength(record.Start, record.End, seams)
            >= Distance(record.Start, record.End) - GeometryTolerance;
        }
      }

      if (quantity <= 25)
      {
        resolvedCompleteTenInchSeam.Should().BeTrue(
          "the raw ten-inch relationship must survive cluster splitting as one complete resolved cut");
      }
    }

    private static Dictionary<int, (CommonCuttingMode Cc, int ShareKey, string CommonCuttingDomain, bool RequiresMergedLines, double Spacing)> DirectRules()
      => new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>
      {
        [0] = (CommonCuttingMode.Unrestricted, 0, "direct", true, NormalPartGap),
      };

    private static SheetPlacement BuildSheet(
      IReadOnlyList<IPartPlacement> placements,
      SparrowNestService.CommonEdgeIntent intent,
      int width,
      int height)
    {
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
      return new SheetPlacement(
        placementType: PlacementTypeEnum.BoundingBox,
        sheet: Sheet.NewSheet(0, width, height),
        partPlacements: placements,
        mergedLength: 0D,
        requiresMergedLines: true,
        commonEdges: new[] { record },
        commonLineGap: CommonLineGap,
        geometryTolerance: GeometryTolerance);
    }

    private static double HorizontalGap(IPartPlacement left, IPartPlacement right)
      => right.PlacedPart.MinX - left.PlacedPart.MaxX;

    private static bool IsNonZero(double value) => Math.Abs(value) > 1e-12;

    private static double NormalizeRotation(double value)
      => ((value % 360D) + 360D) % 360D;

    private static double CoveredLength(
      CommonLinePoint start,
      CommonLinePoint end,
      IReadOnlyList<CommonLineSegment> segments)
    {
      double dx = end.X - start.X;
      double dy = end.Y - start.Y;
      double length = Math.Sqrt((dx * dx) + (dy * dy));
      double ux = dx / length;
      double uy = dy / length;
      var intervals = new List<(double Start, double End)>();
      foreach (CommonLineSegment segment in segments)
      {
        double aCross = Math.Abs(((segment.Start.X - start.X) * uy) - ((segment.Start.Y - start.Y) * ux));
        double bCross = Math.Abs(((segment.End.X - start.X) * uy) - ((segment.End.Y - start.Y) * ux));
        if (aCross > GeometryTolerance || bCross > GeometryTolerance)
        {
          continue;
        }

        double a = ((segment.Start.X - start.X) * ux) + ((segment.Start.Y - start.Y) * uy);
        double b = ((segment.End.X - start.X) * ux) + ((segment.End.Y - start.Y) * uy);
        double lo = Math.Max(0D, Math.Min(a, b));
        double hi = Math.Min(length, Math.Max(a, b));
        if (hi > lo)
        {
          intervals.Add((lo, hi));
        }
      }

      double covered = 0D;
      double cursorStart = 0D;
      double cursorEnd = 0D;
      bool hasInterval = false;
      foreach ((double intervalStart, double intervalEnd) in intervals.OrderBy(interval => interval.Start))
      {
        if (!hasInterval)
        {
          cursorStart = intervalStart;
          cursorEnd = intervalEnd;
          hasInterval = true;
        }
        else if (intervalStart <= cursorEnd + GeometryTolerance)
        {
          cursorEnd = Math.Max(cursorEnd, intervalEnd);
        }
        else
        {
          covered += cursorEnd - cursorStart;
          cursorStart = intervalStart;
          cursorEnd = intervalEnd;
        }
      }

      return hasInterval ? covered + cursorEnd - cursorStart : 0D;
    }

    private static double CollinearOverlap(CommonLineSegment first, CommonLineSegment second)
    {
      double dx = first.End.X - first.Start.X;
      double dy = first.End.Y - first.Start.Y;
      double length = Math.Sqrt((dx * dx) + (dy * dy));
      if (length <= GeometryTolerance)
      {
        return 0D;
      }

      double ux = dx / length;
      double uy = dy / length;
      double startCross = Math.Abs(((second.Start.X - first.Start.X) * uy) - ((second.Start.Y - first.Start.Y) * ux));
      double endCross = Math.Abs(((second.End.X - first.Start.X) * uy) - ((second.End.Y - first.Start.Y) * ux));
      if (startCross > GeometryTolerance || endCross > GeometryTolerance)
      {
        return 0D;
      }

      double a = ((second.Start.X - first.Start.X) * ux) + ((second.Start.Y - first.Start.Y) * uy);
      double b = ((second.End.X - first.Start.X) * ux) + ((second.End.Y - first.Start.Y) * uy);
      return Math.Max(0D, Math.Min(length, Math.Max(a, b)) - Math.Max(0D, Math.Min(a, b)));
    }

    private static void AssertFourHalfCircleBulges(IEnumerable<double> bulges)
    {
      List<double> curves = bulges.Where(IsNonZero).ToList();
      curves.Should().HaveCount(4, "each Hodgson copy contributes both source semicircles");
      curves.Should().OnlyContain(bulge => Math.Abs(Math.Abs(bulge) - 1D) <= 1e-12,
        "boundary traversal may reverse a bulge but must not alter its geometry");
    }

    private static double Distance(CommonLinePoint first, CommonLinePoint second)
    {
      double dx = second.X - first.X;
      double dy = second.Y - first.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double DxfDistance(DxfPoint first, DxfPoint second)
    {
      double dx = second.X - first.X;
      double dy = second.Y - first.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static bool ContainsVerticalOverlap(DxfLwPolyline polyline, double x, double minY, double maxY)
    {
      for (int i = 0; i < polyline.Vertices.Count; i++)
      {
        DxfLwPolylineVertex start = polyline.Vertices[i];
        DxfLwPolylineVertex end = polyline.Vertices[(i + 1) % polyline.Vertices.Count];
        double overlap = Math.Min(maxY, Math.Max(start.Y, end.Y)) - Math.Max(minY, Math.Min(start.Y, end.Y));
        if (Math.Abs(start.X - x) <= GeometryTolerance
          && Math.Abs(end.X - x) <= GeometryTolerance
          && overlap > GeometryTolerance)
        {
          return true;
        }
      }

      return false;
    }

    private static void CreateExactHodgsonDxf(string path)
    {
      var dxf = new DxfFile();
      dxf.Header.Version = DxfAcadVersion.R2007;
      dxf.Entities.Add(new DxfLwPolyline(ExactHodgsonVertices().Select(vertex =>
        new DxfLwPolylineVertex { X = vertex.X, Y = vertex.Y, Bulge = vertex.Bulge }))
      {
        IsClosed = true,
        Layer = "L0",
      });
      dxf.Save(path, true);
    }

    private static (double X, double Y, double Bulge)[] ExactHodgsonVertices()
      => new[]
      {
        (10D, 0D, 0D),
        (9.625D, 0D, 0D),
        (9.625D, 0.375D, HalfCircleBulge),
        (8.375D, 0.375D, 0D),
        (8.375D, 0D, 0D),
        (1.625D, 0D, 0D),
        (1.625D, 0.375D, HalfCircleBulge),
        (0.375D, 0.375D, 0D),
        (0.375D, 0D, 0D),
        (0D, 0D, 0D),
        (0D, 1.375D, 0D),
        (10D, 1.375D, 0D),
      };
  }
}
