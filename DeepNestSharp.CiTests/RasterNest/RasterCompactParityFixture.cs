namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// The compaction in C++ (sheetnest_core.dll) against the C# it was translated from, on the same input. The
  /// tolerance is zero: every position has to come back identical to the last bit, because a nest is hashed and
  /// compared at 1e-6 and the rest of the job builds on these positions. The parts are invented, built from a
  /// fixed seed, and cover what the real calls send: plain spaced parts (the first compaction), shared cuts and
  /// rigid groups (the second), holes, curves, inches and millimetres, a wide and a tall sheet, and parts that
  /// start closer than their spacing.
  /// </summary>
  public class RasterCompactParityFixture
  {
    private readonly ITestOutputHelper output;

    public RasterCompactParityFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    public static IEnumerable<object[]> Cases() => CaseNames.Select(name => new object[] { name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void TheNativeCompactionLandsEveryPartWhereTheCSharpOneDoes(string name)
    {
      var managed = Build(name);
      var native = Build(name);
      double[] startX = managed.Items.Select(it => it.X).ToArray();
      double[] startY = managed.Items.Select(it => it.Y).ToArray();

      RasterCompact.CompactManaged(managed.Items, managed.SheetW, managed.SheetH, managed.Margin, managed.Groups, default, managed.Gap);
      bool ran = NativeCompact.TryCompactNative(native.Items, native.SheetW, native.SheetH, native.Margin, native.Groups, default, native.Gap);

      ran.Should().BeTrue("sheetnest_core.dll ships next to the tests and must accept this input");
      int moved = Enumerable.Range(0, managed.Items.Count).Count(i => managed.Items[i].X != startX[i] || managed.Items[i].Y != startY[i]);
      this.output.WriteLine($"{name}: {managed.Items.Count} parts, {moved} moved");
      moved.Should().BeGreaterThan(0, "a case where nothing moves would compare nothing");
      for (int i = 0; i < managed.Items.Count; i++)
      {
        BitConverter.DoubleToInt64Bits(native.Items[i].X).Should().Be(
          BitConverter.DoubleToInt64Bits(managed.Items[i].X),
          $"part {i} X: C# {managed.Items[i].X:R}, C++ {native.Items[i].X:R}");
        BitConverter.DoubleToInt64Bits(native.Items[i].Y).Should().Be(
          BitConverter.DoubleToInt64Bits(managed.Items[i].Y),
          $"part {i} Y: C# {managed.Items[i].Y:R}, C++ {native.Items[i].Y:R}");
      }
    }

    /// <summary>A cancel before the native compaction starts throws, as the C# one does, and moves nothing.</summary>
    [Fact]
    public void ACancelThrowsAndMovesNothing()
    {
      var job = Build("plain-inches");
      double[] startX = job.Items.Select(it => it.X).ToArray();
      using var cancel = new System.Threading.CancellationTokenSource();
      cancel.Cancel();

      Action run = () => NativeCompact.TryCompactNative(job.Items, job.SheetW, job.SheetH, job.Margin, job.Groups, cancel.Token, job.Gap);

      run.Should().Throw<OperationCanceledException>();
      job.Items.Select(it => it.X).Should().Equal(startX);
    }

    private static readonly string[] CaseNames =
    {
      "plain-inches", "plain-mm", "tall-sheet", "crowded", "frames-and-holes", "shared-cuts", "same-part", "rigid-modules",
      "rounding-tie", "aligned-rows",
    };

    private sealed class Job
    {
      public List<CompactItem> Items { get; } = new List<CompactItem>();

      public double SheetW { get; set; } = 120;

      public double SheetH { get; set; } = 60;

      public double Margin { get; set; } = 0.25;

      public double Gap { get; set; } = 0.01;

      public int[] Groups { get; set; }
    }

    private static Job Build(string name)
    {
      var random = new Random(name.Aggregate(17, (h, c) => (h * 31) + c));
      var job = new Job();
      switch (name)
      {
        case "plain-inches":
          Scatter(job, random, 36, 1.0, 0.25, 1.0);
          break;
        case "plain-mm":
          job.SheetW = 3048;
          job.SheetH = 1524;
          job.Margin = 6.35;
          Scatter(job, random, 36, 25.4, 6.35, 1.0);
          break;
        case "tall-sheet":
          job.SheetW = 60;
          job.SheetH = 120;
          Scatter(job, random, 30, 1.0, 0.25, 1.0);
          break;
        case "crowded":
          // Cells barely bigger than the parts: neighbours start inside each other's spacing.
          Scatter(job, random, 40, 1.0, 0.5, 0.9);
          break;
        case "frames-and-holes":
          Frames(job, random);
          break;
        case "shared-cuts":
          Rows(job, random, sameKey: false);
          SelectEveryCompatiblePair(job);
          break;
        case "same-part":
          Rows(job, random, sameKey: true);
          SelectEveryCompatiblePair(job);
          break;
        case "rigid-modules":
          Modules(job, random);
          break;
        case "rounding-tie":
          RoundingTie(job);
          break;
        case "aligned-rows":
          AlignedRows(job, random);
          break;
        default:
          throw new ArgumentException(name);
      }

      return job;
    }

    /// <summary>Mixed shapes on a jittered grid, as a search leaves them: gaps to close, a few too close.</summary>
    private static void Scatter(Job job, Random random, int count, double unit, double spacing, double cellFactor)
    {
      int columns = (int)Math.Ceiling(Math.Sqrt(count * job.SheetW / job.SheetH));
      double cell = 11 * unit * cellFactor;
      for (int k = 0; k < count; k++)
      {
        INfp shape = RandomShape(random, unit);
        double x = job.Margin + ((k % columns) * cell) + (random.NextDouble() * 1.5 * unit) - shape.MinX;
        double y = job.Margin + ((k / columns) * cell) + (random.NextDouble() * 1.5 * unit) - shape.MinY;
        job.Items.Add(new CompactItem { Poly = shape, X = x, Y = y, Spacing = spacing });
      }
    }

    /// <summary>Frames with a window and small squares around them, some of which slide into a window.</summary>
    private static void Frames(Job job, Random random)
    {
      for (int k = 0; k < 4; k++)
      {
        var frame = Rectangle(24, 16);
        frame.Children.Add(Rectangle(16, 8).Shift(4, 4));
        job.Items.Add(new CompactItem { Poly = frame, X = 2 + (k * 28), Y = 2 + (random.NextDouble() * 3), Spacing = 0.25 });
      }

      for (int k = 0; k < 16; k++)
      {
        job.Items.Add(new CompactItem
        {
          Poly = Rectangle(4, 4),
          X = 3 + ((k % 8) * 14) + (random.NextDouble() * 2),
          Y = 24 + ((k / 8) * 8) + (random.NextDouble() * 2),
          Spacing = 0.25,
        });
      }
    }

    /// <summary>Rows of rectangles that may share cuts, with a spaced part between some of them.</summary>
    private static void Rows(Job job, Random random, bool sameKey)
    {
      for (int row = 0; row < 3; row++)
      {
        for (int k = 0; k < 6; k++)
        {
          bool spaced = (row + k) % 5 == 4;
          job.Items.Add(new CompactItem
          {
            Poly = Rectangle(7, 4 + row),
            X = 1 + (k * 8.2) + (random.NextDouble() * 0.4),
            Y = 1 + (row * 9) + (random.NextDouble() * 0.4),
            Spacing = 0.25,
            Cc = spaced ? CommonCuttingMode.None : (sameKey ? CommonCuttingMode.SamePart : CommonCuttingMode.Unrestricted),
            ShareKey = sameKey ? row % 2 : 0,
            CommonCuttingDomain = "direct",
          });
        }
      }
    }

    /// <summary>Pairs welded at the common-line gap that move as one body, as the second compaction gets them.</summary>
    private static void Modules(Job job, Random random)
    {
      var groups = new List<int>();
      for (int k = 0; k < 10; k++)
      {
        double x = 1 + ((k % 5) * 16) + (random.NextDouble() * 1.5);
        double y = 1 + ((k / 5) * 14) + (random.NextDouble() * 1.5);
        int a = job.Items.Count;
        job.Items.Add(new CompactItem { Poly = Triangle(10, 8), X = x, Y = y, Spacing = 0.25, Cc = CommonCuttingMode.Unrestricted, CommonCuttingDomain = "direct" });
        job.Items.Add(new CompactItem { Poly = Triangle(10, 8).Rotate(180), X = x + 10 + job.Gap, Y = y + 8, Spacing = 0.25, Cc = CommonCuttingMode.Unrestricted, CommonCuttingDomain = "direct" });
        groups.Add(k + 1);
        groups.Add(k + 1);
        job.Items[a].PlacementId = a;
        job.Items[a + 1].PlacementId = a + 1;
        job.Items[a].CommonEdgePeers = new HashSet<int> { a + 1 };
        job.Items[a + 1].CommonEdgePeers = new HashSet<int> { a };
      }

      job.Groups = groups.ToArray();
    }

    /// <summary>
    /// Where the rounding rule shows. The weld measures how far a part may close along X by rounding to whole
    /// integer units where its vertex meets a neighbour's edge. Here the neighbour's right edge leans one unit
    /// (1e-6) over its two inches of height and the part's left point sits exactly halfway up it, so the edge
    /// is met at x.5 units: .NET rounds that to the even unit, rounding half away from zero would not, and the
    /// part would land 1e-6 further. A gap of zero keeps the outlines as they are, so nothing smooths the tie.
    /// </summary>
    private static void RoundingTie(Job job)
    {
      job.Margin = 0;
      job.Gap = 0;
      var neighbour = Polygon((0, 0), (10.000001, 0), (10, 2), (0, 2));
      var part = Polygon((0.5, 0), (5, 0), (5, 2), (0.5, 2), (0, 1));
      job.Items.Add(new CompactItem { Poly = neighbour, X = 0, Y = 0, Spacing = 0.25, Cc = CommonCuttingMode.Unrestricted, CommonCuttingDomain = "direct", PlacementId = 0, CommonEdgePeers = new HashSet<int> { 1 } });
      job.Items.Add(new CompactItem { Poly = part, X = 10.02, Y = 0, Spacing = 0.25, Cc = CommonCuttingMode.Unrestricted, CommonCuttingDomain = "direct", PlacementId = 1, CommonEdgePeers = new HashSet<int> { 0 } });
    }

    /// <summary>
    /// Where the order shows: parts settle bottom row first and, within a row, by X. Every part of a row
    /// stands at exactly the same height, so the second key decides, and a few pairs share the same corner too,
    /// so only a stable sort keeps them in input order.
    /// </summary>
    private static void AlignedRows(Job job, Random random)
    {
      for (int row = 0; row < 3; row++)
      {
        double x = 1;
        for (int k = 0; k < 10; k++)
        {
          double w = 3 + random.Next(4);
          job.Items.Add(new CompactItem { Poly = Rectangle(w, 4 + (row % 2)), X = x, Y = 1 + (row * 7), Spacing = 0.25 });
          if (k % 4 == 1)
          {
            // Another part on the same corner: the search can hand over parts that start overlapping.
            job.Items.Add(new CompactItem { Poly = Triangle(w, 3), X = x, Y = 1 + (row * 7), Spacing = 0.25 });
          }

          x += w + 0.5 + random.Next(3);
        }
      }
    }

    private static void SelectEveryCompatiblePair(Job job)
    {
      for (int i = 0; i < job.Items.Count; i++)
      {
        job.Items[i].PlacementId = i;
        job.Items[i].CommonEdgePeers = new HashSet<int>();
      }

      for (int i = 0; i < job.Items.Count; i++)
      {
        for (int j = i + 1; j < job.Items.Count; j++)
        {
          if (RasterCompact.MayShare(job.Items[i], job.Items[j]))
          {
            job.Items[i].CommonEdgePeers.Add(j);
            job.Items[j].CommonEdgePeers.Add(i);
          }
        }
      }
    }

    private static INfp RandomShape(Random random, double unit)
    {
      double w = (3 + (random.NextDouble() * 6)) * unit;
      double h = (2 + (random.NextDouble() * 5)) * unit;
      INfp shape = random.Next(5) switch
      {
        0 => Rectangle(w, h),
        1 => Ell(w, h, Math.Min(w, h) / 3),
        2 => NotchedU(w, h),
        3 => Ring(Math.Min(w, h) / 2),
        _ => Triangle(w, h),
      };
      double angle = random.Next(8) * 45.0 + (random.NextDouble() < 0.3 ? random.NextDouble() * 30 : 0);
      return angle == 0 ? shape : shape.Rotate(angle);
    }

    private static NoFitPolygon Polygon(params (double X, double Y)[] points)
      => new NoFitPolygon(points.Select(p => new SvgPoint(p.X, p.Y)).ToArray());

    private static INfp Rectangle(double w, double h) => Polygon((0, 0), (w, 0), (w, h), (0, h));

    private static INfp Triangle(double w, double h) => Polygon((0, 0), (w, 0), (0, h));

    private static INfp Ell(double w, double h, double t) => Polygon((0, 0), (w, 0), (w, t), (t, t), (t, h), (0, h));

    private static INfp NotchedU(double w, double h)
      => Polygon((0, 0), (w, 0), (w, h), (w * 0.7, h), (w * 0.7, h * 0.35), (w * 0.3, h * 0.35), (w * 0.3, h), (0, h));

    /// <summary>A disc of 40 points with a round hole of half its radius.</summary>
    private static INfp Ring(double r)
    {
      var disc = Polygon(Enumerable.Range(0, 40).Select(k => (r + (r * Math.Cos(k * Math.PI / 20)), r + (r * Math.Sin(k * Math.PI / 20)))).ToArray());
      disc.Children.Add(Polygon(Enumerable.Range(0, 24).Select(k => (r + (r / 2 * Math.Cos(k * Math.PI / 12)), r + (r / 2 * Math.Sin(k * Math.PI / 12)))).ToArray()));
      return disc;
    }
  }
}
