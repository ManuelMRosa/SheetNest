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
  /// With more than one size of stock, the sheet a part depends on must not be cut with a part that had
  /// somewhere else to go. The batch was picked in the order the operator happened to add the rows, so
  /// the same job nested differently depending on which drawing was imported first.
  /// <para>Reported by an operator: two parts came back uncut with two sheets of another size untouched,
  /// and the same thirteen parts placed in full when the rows were listed in another order.</para>
  /// <para>Set SPARROW_EXE to run; otherwise no-ops.</para>
  /// </summary>
  [Collection(SparrowEngineCollection.Name)]
  public class ScarceStockSizeFixture
  {
    private readonly ITestOutputHelper output;

    public ScarceStockSizeFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    [Fact]
    public void ThePartWithNowhereElseToGoClaimsTheSheetBeforeThePartThatHasAChoice()
    {
      string exe = SparrowExe.Resolve();
      if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
      {
        this.output.WriteLine("SCARCE: SPARROW_EXE not set, skipping.");
        return;
      }

      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        // Two sizes of nearly the same area but a different shape: 10x20 is 200 and 8x26 is 208. The
        // narrow one is the plentiful size and the SMALLER one is the scarce one, which is what makes
        // this a trap: filling most of itself is how the sheet size is chosen, and the small sheet wins
        // that comparison for a part that would have been just as happy on the big one.
        var stock = new List<(int, int, int)> { (10, 20, 2), (8, 26, 2) };

        // 7.9 x 17.9 fits only the 10x20 (the 8x26 leaves 7 of usable width). 5.9 x 17.9 fits both, and
        // it uses the 10x20 better than the 8x26 (0.528 against 0.508), so on utilization alone it takes
        // the sheet the other part cannot do without. It is declared FIRST so plain row order favours it.
        var parts = new List<RasterPartInfo>
        {
          new RasterPartInfo { Path = WriteRectangle(dir, "anywhere", 5.9, 17.9), Quantity = 2 },
          new RasterPartInfo { Path = WriteRectangle(dir, "onlyhere", 7.9, 17.9), Quantity = 2 },
        };

        var result = SparrowNestService.Nest(parts, stock, 4, 1.0, 0.5, 5, exe, out string err);

        result.Should().NotBeNull($"the job must nest (err={err})");
        var placements = result.UsedSheets.SelectMany(sheet => sheet.PartPlacements).ToList();
        this.output.WriteLine(FormattableString.Invariant(
          $"SCARCE: sheets={result.UsedSheets.Count} placed={placements.Count}/4 unplaced={result.UnplacedParts.Count()}"));
        foreach (var sheet in result.UsedSheets)
        {
          string names = string.Join(", ", sheet.PartPlacements.Select(p => Path.GetFileNameWithoutExtension(p.Part?.Name ?? "?")));
          this.output.WriteLine(FormattableString.Invariant(
            $"SCARCE: {Math.Round(sheet.Sheet.WidthCalculated)}x{Math.Round(sheet.Sheet.HeightCalculated)} -> {names}"));
        }

        placements.Count(p => p.Source == 1).Should().Be(2, "both copies of the part that fits only the 10x20 must be cut");
        placements.Count.Should().Be(4, "four sheets hold four parts once the scarce size goes to the part that needs it");
        result.UnplacedParts.Count().Should().Be(0, "there is a sheet for every part");
      }
      finally
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
  }
}
