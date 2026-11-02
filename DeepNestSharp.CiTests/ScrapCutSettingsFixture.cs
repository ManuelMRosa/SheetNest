namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Text.Json;
  using System.Threading.Tasks;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>The scrap cut settings as the session keeps them, and what the export hands the DXF writer.</summary>
  public class ScrapCutSettingsFixture
  {
    private const int W = 120;
    private const int H = 60;

    /// <summary>A session saved before scrap cuts existed opens with them off and every number at its default.</summary>
    [Theory]
    [InlineData(false, 40, 30, 0.02, 0.1969)]
    [InlineData(true, 1000, 750, 0.5, 5)]
    public void ASessionThatNeverSavedThemHasThemOffWithTheDefaults(bool unitsMm, double spacingX, double spacingY, double gap, double overlap)
    {
      var old = JsonSerializer.Deserialize<SessionState>("{\"AutosaveMinutes\":7}")!;

      old.ScrapCutsEnabled.Should().BeNull();
      Resolve(old, unitsMm).Should().BeEquivalentTo(new ScrapCutOptions
      {
        Type = ScrapCutType.Both,
        SpacingX = spacingX,
        SpacingY = spacingY,
        PartGap = gap,
        EdgeOverlap = overlap,
      });
    }

    /// <summary>What was saved comes back, an explicit 0 included (no lines that way), whatever the units.</summary>
    [Fact]
    public void SavedSettingsAreReadBack()
    {
      var saved = new SessionState
      {
        ScrapCutsEnabled = true,
        ScrapCutType = (int)ScrapCutType.Horizontal,
        ScrapSpacingX = 0,
        ScrapSpacingY = 24,
        ScrapPartGap = 0.03,
        ScrapEdgeOverlap = 0,
      };

      var read = JsonSerializer.Deserialize<SessionState>(JsonSerializer.Serialize(saved))!;

      read.ScrapCutsEnabled.Should().BeTrue();
      Resolve(read, unitsMm: true).Should().BeEquivalentTo(new ScrapCutOptions
      {
        Type = ScrapCutType.Horizontal,
        SpacingX = 0,
        SpacingY = 24,
        PartGap = 0.03,
        EdgeOverlap = 0,
      });
    }

    [Fact]
    public void AHandEditedTypeOutOfRangeIsBoth()
    {
      ScrapCutOptions.Resolve(7, -1, -1, -1, -1, false).Type.Should().Be(ScrapCutType.Both);
    }

    /// <summary>Every sheet gets its scrap cuts; only the sheet that carries the offcut gets its cut, and there
    /// the scrap cuts stop on it.</summary>
    [Fact]
    public void EverySheetGetsScrapCutsAndOnlyTheOffcutSheetItsCut()
    {
      var sheet = Packed();
      var offcut = new OffcutOptions { Direction = OffcutDirection.End, Spacing = 0 };
      var scrap = new ScrapCutOptions { Type = ScrapCutType.Vertical, SpacingX = 40, PartGap = 0.02, EdgeOverlap = 0 };

      var plain = ScrapCutGeometry.ExportLines(sheet, false, offcut, scrap);
      var last = ScrapCutGeometry.ExportLines(sheet, true, offcut, scrap);

      // The part fills x 0..20 at the bottom; End frees everything right of x = 80.
      plain.Select(l => l.X1).Should().Equal(40, 80);
      last.Should().BeEquivalentTo(new[]
      {
        new OffcutLine { X1 = 80, Y1 = 0, X2 = 80, Y2 = H },
        new OffcutLine { X1 = 40, Y1 = 0, X2 = 40, Y2 = H },
      }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void WithBothOffTheExportGetsNoLines()
    {
      ScrapCutGeometry.ExportLines(Packed(), true, null, null).Should().BeNull();
      ScrapCutGeometry.ExportLines(Packed(), true, new OffcutOptions { Direction = OffcutDirection.End }, null)
        .Should().BeEquivalentTo(OffcutGeometry.BuildLines(Packed(), new OffcutOptions { Direction = OffcutDirection.End }), "with scrap cuts off the export is what it always was");
    }

    /// <summary>Written by the real exporter, the scrap cuts come out as lines next to the offcut cut, on the
    /// same layer as it.</summary>
    [Fact]
    public async Task TheScrapCutsAreWrittenOnTheOffcutCutsLayer()
    {
      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      try
      {
        string path = Path.Combine(dir, "scrap.dxf");
        var cuts = ScrapCutGeometry.ExportLines(
          Packed(),
          true,
          new OffcutOptions { Direction = OffcutDirection.End, Spacing = 0 },
          new ScrapCutOptions { Type = ScrapCutType.Vertical, SpacingX = 40, PartGap = 0.02, EdgeOverlap = 0 });

        await new DxfExporter().Export(path, Packed(), false, false, cuts);

        var lines = DxfFile.Load(path).Entities.OfType<DxfLine>().ToList();
        var offcutLine = lines.Single(l => l.P1.X == 80 && l.P2.X == 80);
        var scrapLine = lines.Single(l => l.P1.X == 40 && l.P2.X == 40);
        scrapLine.Layer.Should().Be(offcutLine.Layer);
        Math.Max(scrapLine.P1.Y, scrapLine.P2.Y).Should().Be(H);
      }
      finally
      {
        try
        {
          Directory.Delete(dir, true);
        }
        catch (IOException)
        {
          // Leftover temp files must never fail the suite.
        }
      }
    }

    private static ScrapCutOptions Resolve(SessionState session, bool unitsMm)
      => ScrapCutOptions.Resolve(session.ScrapCutType, session.ScrapSpacingX, session.ScrapSpacingY, session.ScrapPartGap, session.ScrapEdgeOverlap, unitsMm);

    /// <summary>One invented 20 x 10 part in the bottom-left corner and another at the top whose right edge
    /// is at x = 80, so End frees the strip beyond it.</summary>
    private static ISheetPlacement Packed()
    {
      var placements = new List<IPartPlacement>
      {
        new PartPlacement(new NoFitPolygon(new[] { new SvgPoint(0, 0), new SvgPoint(20, 0), new SvgPoint(20, 10), new SvgPoint(0, 10) })),
        new PartPlacement(new NoFitPolygon(new[] { new SvgPoint(60, 50), new SvgPoint(80, 50), new SvgPoint(80, 60), new SvgPoint(60, 60) })),
      };
      return new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(1, W, H), placements, 0);
    }
  }
}
