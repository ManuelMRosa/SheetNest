namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// Manual edit mode asks "may this part sit here?" on every mouse move, and ~68 more times for a single
  /// refused drop (ResolveDropToContact). <see cref="DragCollisionCache"/> answers those from geometry
  /// built once per drag; these tests pin that it answers EXACTLY what the uncached rule answers (a stale
  /// or subtly different cache would let parts overlap), and that it is fast enough to drag against.
  /// </summary>
  public class PlacementCollisionFixture
  {
    /// <summary>An ordinary laser kerf: the depth a cut removes, and so the depth an overlap has to beat
    /// before it means anything. The same figure on both sides of every comparison here, because what is
    /// being pinned is that the cache and the rule agree, not what the figure is.</summary>
    private const double Sliver = 0.006;

    private readonly ITestOutputHelper output;

    public PlacementCollisionFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Fact]
    public void CachedDragVerdictsMatchTheUncachedRuleEverywhere()
    {
      const double Clearance = 0.25;
      var neighbours = BuildSheet();
      var dragged = Part(0);
      var cache = new DragCollisionCache(dragged, neighbours.Select(n => (n, Clearance, Sliver)).ToList());

      // Deterministic sweep across the whole sheet, deliberately including positions that land ON a
      // neighbour, just clear of one, and exactly at the clearance boundary.
      var rng = new Random(1234);
      int disagreements = 0, tooCloseCount = 0;
      for (int i = 0; i < 600; i++)
      {
        double x = rng.NextDouble() * 60;
        double y = rng.NextDouble() * 30;

        bool cached = cache.AnyTooClose(x, y);
        var placed = dragged.Shift(new PartPlacement(dragged) { X = x, Y = y });
        bool direct = neighbours.Any(n => PlacementCollision.TooClose(placed, n, Clearance, Sliver));

        if (cached != direct)
        {
          disagreements++;
        }

        if (direct)
        {
          tooCloseCount++;
        }
      }

      tooCloseCount.Should().BeGreaterThan(50, "the sweep must actually exercise colliding positions");
      tooCloseCount.Should().BeLessThan(550, "the sweep must also exercise free positions");
      disagreements.Should().Be(0, "a cached verdict that differs from the real rule can let parts overlap");
    }

    [Fact]
    public void CachedDragVerdictsMatchWithMixedClearancesAndCommonLine()
    {
      var neighbours = BuildSheet();
      // Every neighbour asks for a different clearance, including 0 (common-line: touching is allowed).
      var clearances = neighbours.Select((_, i) => i % 3 == 0 ? 0.0 : 0.1 + (i * 0.05)).ToList();
      var dragged = Part(0);
      var cache = new DragCollisionCache(dragged, neighbours.Select((n, i) => (n, clearances[i], Sliver)).ToList());

      var rng = new Random(99);
      for (int i = 0; i < 400; i++)
      {
        double x = rng.NextDouble() * 60;
        double y = rng.NextDouble() * 30;

        bool cached = cache.AnyTooClose(x, y);
        var placed = dragged.Shift(new PartPlacement(dragged) { X = x, Y = y });
        bool direct = neighbours.Where((n, k) => PlacementCollision.TooClose(placed, n, clearances[k], Sliver)).Any();

        cached.Should().Be(direct, $"position ({x:F3}, {y:F3}) must get the same verdict either way");
      }
    }

    [Fact]
    public void DragQueriesAreFastEnoughToDragAgainst()
    {
      const double Clearance = 0.25;
      var neighbours = BuildSheet();
      var dragged = Part(0);
      var cache = new DragCollisionCache(dragged, neighbours.Select(n => (n, Clearance, Sliver)).ToList());

      cache.AnyTooClose(5, 5); // warm up

      // A drag emits a query per mouse move, and a refused drop runs ~68 in a burst; 200 stands in for
      // "a second of dragging plus the drop".
      const int Queries = 200;
      var sw = Stopwatch.StartNew();
      for (int i = 0; i < Queries; i++)
      {
        cache.AnyTooClose(1 + (i * 0.25), 6.0);
      }

      sw.Stop();
      long cachedMs = sw.ElapsedMilliseconds;

      // The same queries the way the viewer used to run them, faithfully: TooClose(a.PlacedPart,
      // b.PlacedPart, ...) per neighbour â€” and PlacedPart is `Part.Shift(this)`, i.e. a FULL copy of the
      // polygon and its holes, allocated twice per pair.
      var dragPp = new PartPlacement(dragged);
      var neighbourPps = BuildSheetPlacements();
      sw.Restart();
      for (int i = 0; i < Queries; i++)
      {
        dragPp.X = 1 + (i * 0.25);
        dragPp.Y = 6.0;
        foreach (var n in neighbourPps)
        {
          if (PlacementCollision.TooClose(dragPp.PlacedPart, n.PlacedPart, Clearance, Sliver))
          {
            break;
          }
        }
      }

      sw.Stop();
      this.output.WriteLine(
        $"inches: {Queries} drag queries over {neighbours.Count} neighbours: cached {cachedMs} ms, uncached {sw.ElapsedMilliseconds} ms");
      this.output.WriteLine("mm: " + this.MeasureDrag(25.4, Queries));

      cachedMs.Should().BeLessThan(
        2000, "manual mode must stay responsive while dragging (it used to rebuild every polygon per query)");
    }

    /// <summary>Same drag benchmark at an arbitrary unit scale â€” a metric job runs the same shapes with
    /// coordinates ~25x larger, which is where the drag cost used to explode.</summary>
    private string MeasureDrag(double u, int queries)
    {
      double clearance = 0.25 * u;
      var pps = BuildSheetPlacements(u);
      var placed = pps.Select(p => p.PlacedPart).ToList();
      var dragged = Part(0, u);
      var cache = new DragCollisionCache(dragged, placed.Select(n => (n, clearance, Sliver)).ToList());
      cache.AnyTooClose(5 * u, 5 * u);

      var sw = Stopwatch.StartNew();
      for (int i = 0; i < queries; i++)
      {
        cache.AnyTooClose((1 + (i * 0.25)) * u, 6.0 * u);
      }

      sw.Stop();
      long cachedMs = sw.ElapsedMilliseconds;

      var dragPp = new PartPlacement(dragged);
      sw.Restart();
      for (int i = 0; i < queries; i++)
      {
        dragPp.X = (1 + (i * 0.25)) * u;
        dragPp.Y = 6.0 * u;
        foreach (var n in pps)
        {
          if (PlacementCollision.TooClose(dragPp.PlacedPart, n.PlacedPart, clearance, Sliver))
          {
            break;
          }
        }
      }

      sw.Stop();
      return $"{queries} drag queries: cached {cachedMs} ms, uncached {sw.ElapsedMilliseconds} ms";
    }

    /// <summary>26 placed parts in two rows â€” the reference sheet from the user's job.</summary>
    private static List<INfp> BuildSheet()
      => BuildSheetPlacements().Select(pp => pp.PlacedPart).ToList();

    private static List<PartPlacement> BuildSheetPlacements(double u = 1.0)
    {
      var placed = new List<PartPlacement>();
      for (int i = 0; i < 26; i++)
      {
        var part = Part(i, u);
        placed.Add(new PartPlacement(part)
        {
          X = (2 + ((i % 13) * 4.5)) * u,
          Y = (2 + ((i / 13) * 12.0)) * u,
        });
      }

      return placed;
    }

    /// <summary>A concave, holed part with enough vertices to be representative of a real DXF outline.</summary>
    private static INfp Part(int seed) => Part(seed, 1.0);

    private static INfp Part(int seed, double u)
    {
      const int N = 240;
      var pts = new List<SvgPoint>(N);
      for (int i = 0; i < N; i++)
      {
        double t = 2 * Math.PI * i / N;
        double r = 1 + (0.18 * Math.Sin((3 + (seed % 3)) * t)) - (0.08 * Math.Cos(2 * t));
        pts.Add(new SvgPoint(2.0 * u * (1 + (r * Math.Cos(t) / 1.3)), 4.5 * u * (1 + (r * Math.Sin(t) / 1.3))));
      }

      var poly = new NoFitPolygon(pts);
      var hole = new List<SvgPoint>(60);
      for (int i = 0; i < 60; i++)
      {
        double t = 2 * Math.PI * i / 60;
        hole.Add(new SvgPoint((2.0 + (0.6 * Math.Cos(t))) * u, (4.5 + (1.2 * Math.Sin(t))) * u));
      }

      poly.Children.Add(new NoFitPolygon(hole));
      return poly;
    }
  }
}
