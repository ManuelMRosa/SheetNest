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
    /// The six-bar row this pair of tests measures: the test2.dxf bar (7 x 36) with the kerf baked into
    /// the footprint the way MapAndCompact feeds it, scattered across a 120 x 60 sheet with NO margin.
    /// Pure Clipper, no engine and no external process, so any difference is the code and not the machine.
    /// </summary>
    private static List<CompactItem> CommonLineRow(out double width, out double footMinX, out double footMinY)
    {
      var outline = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(7, 0), new SvgPoint(7, 36), new SvgPoint(0, 36),
      });
      var tooling = SparrowNestService.ToolingFootprint(outline, null, 0.006, 0);
      width = tooling.MaxX - tooling.MinX;   // 7.006: the bar plus one kerf
      footMinX = tooling.MinX;
      footMinY = tooling.MinY;

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
      return items;
    }

    /// <summary>
    /// Every bar ends up ON the sheet margin. This layout used to leave FOUR of the six sitting 0.001
    /// above it, held there by a side-by-side neighbour: the gap measurement ray-cast from a corner that
    /// exactly grazed the neighbour's corner, read that zero-area touch as a blocker, and the resulting
    /// gap of 0 short-circuited the whole measurement for that axis.
    /// <para>
    /// Asserted PER BAR, never as a total or a mean: with six bars an aggregate is dominated by the two
    /// that were already correct, and it passes at four thousandths of wasted sheet.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryBarInACommonLineRowSitsOnTheSheetMargin()
    {
      var items = CommonLineRow(out _, out _, out double footMinY);

      for (int i = 0; i < items.Count; i++)
      {
        (items[i].Y + footMinY).Should().BeApproximately(0, 1e-5,
          $"bar {i} has nothing under it, so nothing may hold it off the margin");
      }
    }

    /// <summary>
    /// Every seam in the row is CLOSED — consecutive bars exactly one footprint apart, so the shared edge
    /// is coincident and the DXF export emits it as one cut. This replaced a twelve-number snapshot that
    /// had been captured from the previous build and asserted "must not move": those numbers were a
    /// photograph of the defect, and encoded one seam standing open by 0.003353. A snapshot is only ever
    /// worth what the build it came from was worth; the invariant is what actually had to hold.
    /// <para>
    /// Asserted SEAM BY SEAM. Summing them, or checking the row's total extent, lets one open seam be
    /// cancelled by an overlap elsewhere — and an overlap is the worse of the two faults.
    /// </para>
    /// </summary>
    [Fact]
    public void EverySeamInACommonLineRowIsExactlyClosed()
    {
      var items = CommonLineRow(out double width, out double footMinX, out _);

      var left = items.Select(it => it.X + footMinX).OrderBy(x => x).ToArray();
      for (int i = 1; i < left.Length; i++)
      {
        (left[i] - left[i - 1]).Should().BeApproximately(width, 1e-5,
          $"seam {i - 1} must be coincident: anything else exports as two cuts a sliver apart");
      }
    }

    /// <summary>
    /// Reported from the app: a freshly nested Same part job came up with some pieces red. The weld pass
    /// closes a common-cut part the last fraction onto its neighbour, and it measured the gap from the
    /// MOVING part's raw outline while only inflating the neighbour, so a pair that may NOT share was
    /// closed to sB/2 instead of (sA+sB)/2. The same halving that was fixed in the slide, still alive here.
    /// <para>
    /// Only bites at small spacings, which is why it survived: the weld is capped at 0.05 drawing units
    /// and the over-computed gap is sA/2, so the pair has to be asking for 0.1 or less. In inches that is
    /// most shop work.
    /// </para>
    /// </summary>
    [Fact]
    public void TheWeldDoesNotEatANonSharingNeighboursClearance()
    {
      const double Spacing = 0.08;   // half of it (0.04) sits under the weld's 0.05 cap

      // B is a different drawing set to None, so nothing may share a cut with it. A1 and A2 are the same
      // drawing in Same part mode: they weld onto each other, and A1 has B on its left to weld towards.
      var items = new List<CompactItem>
      {
        Square(1, CommonCuttingMode.None, Spacing, shareKey: 9),
        Square(60, CommonCuttingMode.SamePart, Spacing, shareKey: 0),
        Square(120, CommonCuttingMode.SamePart, Spacing, shareKey: 0),
      };

      RasterCompact.Compact(items, 400, 60, 0.5);

      Gap(items[1], items[2]).Should().BeLessThan(1e-3, "the same drawing still welds to a shared cut");
      Gap(items[0], items[1]).Should().BeGreaterThan(Spacing - 1e-3,
        "the weld must not close onto a neighbour it cannot share a cut with");
    }

    /// <summary>
    /// A part cannot be left frozen by a neighbour that is merely TOUCHING it on the side it is moving
    /// AWAY from. Three identical bars that may share a cut, laid out so the weld reaches the rightmost
    /// one first: it closes onto the middle bar, and the middle bar — now touched on its right — used to
    /// measure a gap of exactly 0 to its left and never close its own seam.
    /// <para>
    /// The second row is the CONTROL, the same three bars ordered the other way so the weld never meets
    /// the trap. It passed before this fix as well. If both rows ever go the same colour the test has
    /// stopped discriminating and is not evidence of anything.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(true)]    // the trap: the weld reaches the RIGHTMOST bar first
    [InlineData(false)]   // the control
    public void AMiddleBarClosesItsOwnSeamEvenWhenWeldedFromTheRight(bool rightToLeft)
    {
      const double Width = 10, Height = 40;
      var outline = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(Width, 0), new SvgPoint(Width, Height), new SvgPoint(0, Height),
      });

      // Pack order follows Y, so a Y that falls to the right makes the weld run right-to-left.
      var ys = rightToLeft ? new[] { 5.0, 3.0, 1.0 } : new[] { 1.0, 3.0, 5.0 };
      var items = new List<CompactItem>();
      for (int i = 0; i < 3; i++)
      {
        items.Add(new CompactItem
        {
          Poly = outline,
          X = 1 + (i * 30),
          Y = ys[i],
          Spacing = 0.25,
          Cc = CommonCuttingMode.SamePart,
          ShareKey = 7,
        });
      }

      RasterCompact.Compact(items, 200, 100, 0.5);

      // Each seam on its own. A total, or the row's extent, lets one open seam hide behind an overlap.
      var left = items.Select(it => it.X).OrderBy(x => x).ToArray();
      (left[1] - left[0]).Should().BeApproximately(Width, 1e-5, "the first seam must be coincident");
      (left[2] - left[1]).Should().BeApproximately(Width, 1e-5, "the middle bar must close its own seam too");
    }

    /// <summary>
    /// The acceptance case, and the one shaped like the job this came from: two rows of common-cut bars,
    /// where every part has a neighbour beside it AND one above or below. Both axes have to close at once
    /// — the row-mates must not graze each other into holding the whole upper row off the lower one.
    /// <para>
    /// The arriving heights are deliberately UNEVEN, and the sheet is WIDER than it is tall. Both matter:
    /// pack order runs along the settle axis, so only on a wide sheet does it follow Y and let the weld
    /// reach a bar before the one on its left. Fed in tidy rows, or on a tall sheet, this same layout
    /// passed before the fix as well — pack order then settled every left neighbour first, which is the
    /// one arrangement in which neither defect can appear. A real nest arrives scattered.
    /// </para>
    /// </summary>
    [Fact]
    public void TwoRowsOfCommonCutBarsCloseEverySeamInBothAxes()
    {
      const double Width = 10, Height = 20;
      var outline = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(Width, 0), new SvgPoint(Width, Height), new SvgPoint(0, Height),
      });

      // Compaction only ever slides down and left, so two rows handed in stay two rows: the upper one
      // settles onto the lower instead of being reflowed alongside it.
      var items = new List<CompactItem>();
      foreach (double baseY in new[] { 2.0, 25.0 })
      {
        var at = new[] { (1.0, baseY), (12.0, baseY + 0.3), (23.0, baseY + 0.1) };
        foreach (var (x, y) in at)
        {
          items.Add(new CompactItem
          {
            Poly = outline,
            X = x,
            Y = y,
            Spacing = 0.25,
            Cc = CommonCuttingMode.SamePart,
            ShareKey = 7,
          });
        }
      }

      RasterCompact.Compact(items, 200, 100, 0.5);

      var rows = items.GroupBy(it => Math.Round(it.Y, 3)).OrderBy(g => g.Key).ToArray();
      rows.Should().HaveCount(2, "the upper row settles onto the lower, it is not reflowed beside it");

      foreach (var row in rows)
      {
        var left = row.Select(it => it.X).OrderBy(x => x).ToArray();
        for (int i = 1; i < left.Length; i++)
        {
          (left[i] - left[i - 1]).Should().BeApproximately(Width, 1e-5, $"seam {i - 1} across the row");
        }
      }

      (rows[1].Key - rows[0].Key).Should().BeApproximately(Height, 1e-5,
        "the upper row must come to rest ON the lower one, not a graze above it");
    }

    /// <summary>
    /// A GUARD, not a reproducer: it passed before this change too, and its job is to catch the obvious
    /// way to over-fix — closing every seam by letting a common-cut part eat the clearance of a neighbour
    /// it may not share a cut with. The left part is a different drawing set to None, so A1 has to stop
    /// at the full clearance while still closing onto A2.
    /// </summary>
    [Fact]
    public void ASeamStillClosesWhenOneSideIsPinnedByASpacedNeighbour()
    {
      var items = new List<CompactItem>
      {
        Square(1, CommonCuttingMode.None, 0.5),
        Square(60, CommonCuttingMode.SamePart, 0.5, 7),
        Square(120, CommonCuttingMode.SamePart, 0.5, 7),
      };

      RasterCompact.Compact(items, 200, 200, 0.5);

      Gap(items[1], items[2]).Should().BeLessThan(1e-5, "the pair that may share a cut closes to contact");
      Gap(items[0], items[1]).Should().BeGreaterThan(0.5 - 1e-5,
        "and the part that may NOT share still gets every thou of its clearance");
    }

    private static CompactItem Square(double x, CommonCuttingMode cc, double spacing, int shareKey = 0)
    {
      var outline = new NoFitPolygon(new[]
      {
        new SvgPoint(0, 0), new SvgPoint(20, 0), new SvgPoint(20, 20), new SvgPoint(0, 20),
      });

      return new CompactItem { Poly = outline, X = x, Y = 1, Spacing = spacing, Cc = cc, ShareKey = shareKey };
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
