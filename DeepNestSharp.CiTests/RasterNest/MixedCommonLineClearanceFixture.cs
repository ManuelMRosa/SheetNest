namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using DeepNestLib;
  using DeepNestLib.NestProject;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// Compaction has to judge each PAIR: two parts that may share a cut close to contact, and everything
  /// else keeps (spacingA + spacingB) / 2. Before common cutting had modes, "shares a cut" and "asks for
  /// no clearance" were the same test (Spacing &lt;= 0 on both sides), and a job that mixed the two paid
  /// for it.
  /// </summary>
  public class MixedCommonLineClearanceFixture
  {
    /// <summary>
    /// The one that was wrong. A common-cut part offered NOTHING to a spaced neighbour, because its own
    /// shell was never inflated (its spacing had been forced to 0 upstream) and the pair was not a
    /// shared-cut pair either, so it closed to sB/2.
    /// <para>
    /// MEASURED against the pre-fix build with this exact geometry: the neighbour asked for 0.5" and got
    /// <b>0.2536"</b>. On a real job that is a part cut a quarter inch closer than the operator asked.
    /// </para>
    /// </summary>
    [Fact]
    public void ACommonLinePartKeepsItsSpacedNeighbourAtTheFullClearance()
    {
      var items = new List<CompactItem>
      {
        Square(1, CommonCuttingMode.Unrestricted, 0.5),
        Square(60, CommonCuttingMode.Unrestricted, 0.5),
        Square(120, CommonCuttingMode.None, 0.5),
      };

      RasterCompact.Compact(items, 200, 200, 0.5);

      Gap(items[0], items[1]).Should().BeLessThan(1e-3,
        "two parts that may share a cut still close to contact");
      Gap(items[1], items[2]).Should().BeGreaterThan(0.5 - 1e-3,
        "the spaced part asked for 0.5 and a common-cut neighbour does not get to halve it");
    }

    /// <summary>
    /// The control for the fix above: inflating a common-cut part's shell must not stop it closing onto
    /// its own kind. Passed before the fix too, which is the point — it is what catches "fixed the
    /// clearance, broke the feature".
    /// </summary>
    [Fact]
    public void ACommonLinePartStillClosesOntoItsOwnKind()
    {
      var items = new List<CompactItem>
      {
        Square(1, CommonCuttingMode.Unrestricted, 0.5),
        Square(60, CommonCuttingMode.Unrestricted, 0.5),
      };

      RasterCompact.Compact(items, 200, 200, 0.5);

      Gap(items[0], items[1]).Should().BeLessThan(1e-3);
    }

    /// <summary>
    /// NO REGRESSION. A job where every part is common-cut is what the feature has always been used for,
    /// and it has to come out of compaction in exactly the same place as before.
    /// <para>
    /// These twelve numbers were captured by running this same layout against the pre-fix build, where a
    /// common-cut part arrived with Spacing forced to 0. Here it arrives with its real 0.25 and the mode
    /// instead. Same numbers, so the pairing rule replaced the spacing test without moving anything. Pure
    /// Clipper, no engine, no external process: any difference is this change, not the machine.
    /// </para>
    /// </summary>
    [Fact]
    public void AnAllCommonLineLayoutIsUnchanged()
    {
      var expected = new[]
      {
        (0.003000, 0.003000),
        (14.018353, 0.004000),
        (28.030352, 0.004000),
        (35.036354, 0.004000),
        (7.009000, 0.003000),
        (21.024353, 0.004000),
      };

      // The test2.dxf bar (7 x 36) with the kerf baked into the footprint, the way MapAndCompact feeds it.
      var outline = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(7, 0), new SvgPoint(7, 36), new SvgPoint(0, 36),
      });
      var tooling = SparrowNestService.ToolingFootprint(outline, null, 0.006, 0);

      var at = new[] { (2.0, 1.0), (31.0, 3.0), (58.0, 2.0), (84.0, 5.0), (17.0, 4.0), (70.0, 1.0) };
      var items = new List<CompactItem>();
      foreach (var (x, y) in at)
      {
        items.Add(new CompactItem
        {
          Poly = tooling,
          X = x,
          Y = y,
          Spacing = 0.25,                            // its real spacing now, no longer forced to 0
          Cc = CommonCuttingMode.Unrestricted,
        });
      }

      RasterCompact.Compact(items, 120, 60, 0);

      for (int i = 0; i < items.Count; i++)
      {
        items[i].X.Should().BeApproximately(expected[i].Item1, 1e-6, $"part {i} X must not move");
        items[i].Y.Should().BeApproximately(expected[i].Item2, 1e-6, $"part {i} Y must not move");
      }
    }

    private static CompactItem Square(double x, CommonCuttingMode cc, double spacing)
    {
      var outline = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(20, 0), new SvgPoint(20, 20), new SvgPoint(0, 20),
      });

      return new CompactItem { Poly = outline, X = x, Y = 1, Spacing = spacing, Cc = cc };
    }

    /// <summary>Shortest distance between two 20-wide square outlines at their compacted positions.</summary>
    private static double Gap(CompactItem a, CompactItem b)
    {
      const double Side = 20;
      double gapX = Math.Max(0, Math.Max(a.X - (b.X + Side), b.X - (a.X + Side)));
      double gapY = Math.Max(0, Math.Max(a.Y - (b.Y + Side), b.Y - (a.Y + Side)));
      return gapX > 0 && gapY > 0 ? Math.Sqrt((gapX * gapX) + (gapY * gapY)) : Math.Max(gapX, gapY);
    }
  }
}
