namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.IO;
  using System.Linq;
  using System.Threading;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  [Collection(SparrowEngineCollection.Name)]
  public sealed class CommonLinePerformanceSafetyFixture : IDisposable
  {
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
    private readonly string source;

    public CommonLinePerformanceSafetyFixture()
    {
      Directory.CreateDirectory(this.directory);
      this.source = Path.Combine(this.directory, "square.dxf");
      var file = new DxfFile();
      file.Header.Version = DxfAcadVersion.R2007;
      file.Entities.Add(new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0, Y = 0 },
        new DxfLwPolylineVertex { X = 10, Y = 0 },
        new DxfLwPolylineVertex { X = 10, Y = 10 },
        new DxfLwPolylineVertex { X = 0, Y = 10 },
      }) { IsClosed = true });
      file.Save(this.source);
    }

    public void Dispose() => Directory.Delete(this.directory, true);

    [Theory]
    [InlineData(1.0)]
    [InlineData(25.4)]
    public void SpatialIndexPreservesEveryExhaustiveDistanceMatch(double unitScale)
    {
      var random = new Random(1847);
      var segments = Enumerable.Range(0, 600).Select(i =>
      {
        double x = (random.NextDouble() * 100 - 50) * unitScale;
        double y = (random.NextDouble() * 100 - 50) * unitScale;
        return new CommonEdgeClearance.BoundarySegment(i, 0, 1,
          new CommonEdgeClearance.Point(x, y),
          new CommonEdgeClearance.Point(x + random.NextDouble() * unitScale, y + random.NextDouble() * unitScale));
      }).ToArray();
      var index = new CommonEdgeClearance.SegmentIndex(segments);
      int examined = 0;
      double distance = 0.30 * unitScale;
      double tolerance = 0.0001 * unitScale;
      foreach (var segment in segments)
      {
        int[] candidates = index.Query(segment, distance).ToArray();
        examined += candidates.Length;
        candidates.Should().OnlyHaveUniqueItems();
        int[] indexedMatches = candidates.Where(i => CommonEdgeClearance.SegmentsWithin(
          segment, segments[i], tolerance, distance * distance)).OrderBy(i => i).ToArray();
        int[] exhaustiveMatches = Enumerable.Range(0, segments.Length).Where(i => CommonEdgeClearance.SegmentsWithin(
          segment, segments[i], tolerance, distance * distance)).ToArray();
        indexedMatches.Should().Equal(exhaustiveMatches);
      }

      examined.Should().BeLessThan(segments.Length * segments.Length / 10,
        "distant fine-arc chords must not reach the costly relationship/distance checks");
    }

    [Theory]
    [InlineData("poses")]
    [InlineData("candidates")]
    [InlineData("beam")]
    [InlineData("clearance")]
    [InlineData("topology")]
    [InlineData("source-revalidation")]
    public void CancellationRestoresTheEntireProvisionalLayout(string stage)
    {
      List<IPartPlacement> parts = this.Parts(0.30);
      IPartPlacement[] originals = parts.ToArray();
      var poses = parts.Select(part => (part.X, part.Y, part.Rotation, part.Id)).ToArray();
      using var cancellation = new CancellationTokenSource();
      var latency = new Stopwatch();
      bool reached = false;
      Action run = () => this.Snap(parts, cancellation.Token, current =>
      {
        if (current == stage)
        {
          reached = true;
          latency.Start();
          cancellation.Cancel();
        }
      });

      run.Should().Throw<OperationCanceledException>();
      reached.Should().BeTrue();
      latency.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
      for (int i = 0; i < parts.Count; i++)
      {
        parts[i].Should().BeSameAs(originals[i]);
        (parts[i].X, parts[i].Y, parts[i].Rotation, parts[i].Id).Should().Be(poses[i]);
      }
    }

    [Fact]
    public void ReusedClearanceVerdictsTrackGeometrySettingsAndConsumedIntervals()
    {
      List<IPartPlacement> parts = this.Parts(0.01);
      var cache = new CommonEdgeClearance.PairVerdictCache();
      var full = new[] { new CommonEdgeClearance.EdgeReference(parts[0], 1, parts[1], 3) };
      var partial = new[] { new CommonEdgeClearance.EdgeReference(parts[0], 1, parts[1], 3, 0.25, 0.75, 0.25, 0.75) };
      int Evaluate(CommonEdgeClearance.EdgeReference[] edges, double spacing = 0.30,
        CommonEdgeClearance.PairVerdictCache reuse = null)
        => CommonEdgeClearance.FindUnfit(parts, 30, 20, 0, (a, b) => spacing,
          (a, b) => PlacementCollision.PlacementNoise, edges, 0.01, 0.0001,
          verdictCache: reuse).All.Count;

      Evaluate(full, reuse: cache).Should().Be(0);
      Evaluate(full, reuse: cache).Should().Be(0);
      cache.Hits.Should().BeGreaterThan(0);
      Evaluate(partial, reuse: cache).Should().Be(Evaluate(partial)).And.BeGreaterThan(0);
      Evaluate(full, reuse: cache).Should().Be(0);
      Evaluate(partial, 0.005, cache).Should().Be(Evaluate(partial, 0.005));
      parts[1].X += 0.10;
      Evaluate(full, reuse: cache).Should().Be(Evaluate(full)).And.BeGreaterThan(0);
      parts[1].X += 0.30;
      Evaluate(full, reuse: cache).Should().Be(Evaluate(full)).And.Be(0);
    }

    [Fact]
    public void CachedRecordsDoNotReopenTheirCapturedSource()
    {
      List<IPartPlacement> parts = this.Parts(0.01);
      var catalogs = new Dictionary<string, DxfContourCatalog>(StringComparer.OrdinalIgnoreCase)
      {
        [this.source] = DxfContourCatalog.Load(this.source, 0.0001),
      };
      // FileShare.None makes any unintended DXF reread fail, without timing-dependent assertions.
      using var locked = new FileStream(this.source, FileMode.Open, FileAccess.Read, FileShare.None);
      for (int i = 0; i < 100; i++)
      {
        CommonLineGeometryResolver.TryCreateCommonEdge(parts[0], 1, 0, 1,
          parts[1], 3, 0, 1, 0.01, 0.0001, out CommonEdge edge, catalogs).Should().BeTrue();
        edge.SpanA.SourceFingerprint.Should().Be(catalogs[this.source].SourceFingerprint);
      }
    }

    [Fact]
    public void AChangedSourceRejectsTheCachedWinnerAndRestoresNormalSpacing()
    {
      List<IPartPlacement> parts = this.Parts(0.30);
      IPartPlacement[] originals = parts.ToArray();
      double[] positions = parts.Select(part => part.X).ToArray();
      Action run = () => this.Snap(parts, default, stage =>
      {
        if (stage == "source-revalidation")
        {
          DxfFile changed = DxfFile.Load(this.source);
          ((DxfLwPolyline)changed.Entities.Single()).Vertices[1].X += 0.5;
          changed.Save(this.source);
        }
      });

      run.Should().Throw<CommonLineValidationException>().WithMessage("*source changed*");
      for (int i = 0; i < parts.Count; i++)
      {
        parts[i].Should().BeSameAs(originals[i]);
        parts[i].X.Should().Be(positions[i]);
      }
    }

    private List<IPartPlacement> Parts(double gap)
    {
      var outline = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(10, 0), new SvgPoint(10, 10), new SvgPoint(0, 10),
      }) { Name = this.source };
      return new List<IPartPlacement>
      {
        new PartPlacement(outline) { Id = 17, Source = 0, X = 1, Y = 1 },
        new PartPlacement(outline) { Id = 19, Source = 0, X = 11 + gap, Y = 1 },
      };
    }

    private SparrowNestService.DirectCommonLineSnapResult Snap(
      List<IPartPlacement> parts, CancellationToken cancel, Action<string> onStage)
      => SparrowNestService.SnapDirectCommonLineEdges(parts,
        new Dictionary<int, (CommonCuttingMode, int, string, bool, double)>
        {
          [0] = (CommonCuttingMode.Unrestricted, 0, "direct", true, 0.30),
        },
        new Dictionary<int, SparrowNestService.DirectOrientationProfile>
        {
          [0] = new SparrowNestService.DirectOrientationProfile(parts[0].Part, new[] { 0 }),
        },
        30, 20, 0, 0.01, 0.0001, cancel: cancel, onStage: onStage);
  }
}
