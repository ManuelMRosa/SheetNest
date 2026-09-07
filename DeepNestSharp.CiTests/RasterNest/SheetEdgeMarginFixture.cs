namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// The sheet edge margin is measured against the PART, never against the clearance shell the part
  /// carries for its neighbours. The compactor says so in as many words (RasterCompact: "the sheet
  /// margin applies to the PART, not to its clearance shell") and the acceptance rule the editor paints
  /// red with agrees (PlacementAcceptance.IsOutsideUsableArea). The strip handed to the engine used to
  /// disagree, and it is the one that decides whether a part is placed at all.
  /// <para>Reported by an operator: thirteen parts, thirteen sheets, all of them cut by hand the week
  /// before, and the nest left seven parts uncut with seven sheets untouched.</para>
  /// <para>Set SPARROW_EXE to run; otherwise no-ops.</para>
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class SheetEdgeMarginFixture
  {
    private readonly ITestOutputHelper output;

    public SheetEdgeMarginFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    /// <summary>
    /// A part that fills the usable sheet must be placed. The engine packs the part GROWN by half the
    /// spacing on every side, so reserving the whole margin from the strip on top of that charged the
    /// sheet edge the spacing a second time: an 18.9 square keeps 0.5 from the edge by the rule, and was
    /// refused for wanting 0.5 + 0.5 of it.
    /// </summary>
    [Fact]
    public void APartThatFillsTheUsableSheetIsNotRefusedForTheSpacingItKeepsFromItsNeighbours()
    {
      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("EDGE: SPARROW_EXE not set, skipping.");
        return;
      }

      const double Margin = 0.5;
      const double Spacing = 1.0;
      const int Sheet = 20;

      // 18.9 leaves 0.05 inside the usable 19.0, so nothing here rests on an exact-equality pack. Grown
      // by half the spacing it is 19.9, which is what the old strip of 19.0 turned away.
      const double Side = 18.9;

      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        var parts = new List<RasterPartInfo> { new RasterPartInfo { Path = WriteSquare(dir, "big", Side), Quantity = 1 } };
        var stock = new List<(int, int, int)> { (Sheet, Sheet, 1) };

        var result = SparrowNestService.Nest(parts, stock, 4, Spacing, Margin, 5, exe, out string err);

        result.Should().NotBeNull($"a part that fits the usable sheet must be nested (err={err})");
        var placements = result.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
        this.output.WriteLine(FormattableString.Invariant(
          $"EDGE: placed={placements.Count}/1 unplaced={result.UnplacedParts.Count()}"));
        placements.Count.Should().Be(1, "the square fits inside the sheet's usable area");

        // And it must respect the margin it was given: the point is that the edge is charged ONCE, not
        // that the edge stopped being enforced.
        foreach (var placement in placements)
        {
          placement.MinX.Should().BeGreaterThanOrEqualTo(Margin - 0.01);
          placement.MinY.Should().BeGreaterThanOrEqualTo(Margin - 0.01);
          placement.MaxX.Should().BeLessThanOrEqualTo(Sheet - Margin + 0.01);
          placement.MaxY.Should().BeLessThanOrEqualTo(Sheet - Margin + 0.01);
        }
      }
      finally
      {
        TryDelete(dir);
      }
    }

    /// <summary>
    /// One part that fits no sheet in the stock must cost only itself. The batch was picked on area
    /// alone, so an oversize part at the head of the list filled the batch of EVERY candidate size, all
    /// of them packed nothing, and the sheet loop stopped with parts that fit and sheets to cut them on.
    /// </summary>
    [Fact]
    public void AnOversizePartDoesNotStrandTheRestOfTheJob()
    {
      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("STRAND: SPARROW_EXE not set, skipping.");
        return;
      }

      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        // The bar is 60 square inches against a 144 inch sheet, so three of them pass the batch's area
        // cap of 1.5 sheetfuls and leave no room for the squares, but 30 inches fits no 12 inch sheet
        // in any orientation. The squares are declared SECOND so plain list order reaches them last.
        var parts = new List<RasterPartInfo>
        {
          new RasterPartInfo { Path = WriteRectangle(dir, "bar", 30, 2), Quantity = 3 },
          new RasterPartInfo { Path = WriteSquare(dir, "square", 9), Quantity = 4 },
        };
        var stock = new List<(int, int, int)> { (12, 12, 4) };

        var result = SparrowNestService.Nest(parts, stock, 4, 1.0, 0.5, 5, exe, out string err);

        result.Should().NotBeNull($"the squares must still be nested (err={err})");
        var placements = result.UsedSheets.SelectMany(s => s.PartPlacements).ToList();
        this.output.WriteLine(FormattableString.Invariant(
          $"STRAND: sheets={result.UsedSheets.Count} placed={placements.Count} unplaced={result.UnplacedParts.Count()}"));

        placements.Count(p => p.Source == 1).Should().Be(4, "every square fits a 12x12 sheet and there are four of them");
        placements.Count(p => p.Source == 0).Should().Be(0, "a 30 inch bar fits no 12 inch sheet");
        result.UnplacedParts.Count().Should().Be(3, "only the three bars are left over");
      }
      finally
      {
        TryDelete(dir);
      }
    }

    private static string WriteSquare(string dir, string name, double side) => WriteRectangle(dir, name, side, side);

    private static string WriteRectangle(string dir, string name, double width, double height)
    {
      var file = new IxMilia.Dxf.DxfFile();
      var pts = new[]
      {
        new IxMilia.Dxf.DxfPoint(0, 0, 0), new IxMilia.Dxf.DxfPoint(width, 0, 0),
        new IxMilia.Dxf.DxfPoint(width, height, 0), new IxMilia.Dxf.DxfPoint(0, height, 0),
        new IxMilia.Dxf.DxfPoint(0, 0, 0),
      };
      file.Entities.Add(new IxMilia.Dxf.Entities.DxfPolyline(pts.Select(p => new IxMilia.Dxf.Entities.DxfVertex(p))) { IsClosed = true });
      string path = Path.Combine(dir, name + ".dxf");
      file.Save(path);
      return path;
    }

    private static void TryDelete(string dir)
    {
      try
      {
        Directory.Delete(dir, true);
      }
      catch (IOException)
      {
      }
    }
  }
}
