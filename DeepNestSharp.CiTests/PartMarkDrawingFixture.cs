namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Windows;
  using DeepNestSharp.Ui;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// What a part carries engraved has to be drawn where it is going to be cut. Nothing a part turns
  /// into on the way to the screen has room for a mark, so this is read back out of the part's file and
  /// placed the same way the part was.
  /// </summary>
  public class PartMarkDrawingFixture : IDisposable
  {
    private const int Yellow = 2;
    private const int White = 7;

    private readonly string tempDir;

    public PartMarkDrawingFixture()
    {
      this.tempDir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
      try
      {
        Directory.Delete(this.tempDir, true);
      }
      catch (IOException)
      {
        // Leftover temp files must never fail the suite.
      }
    }

    [Fact]
    public void TheEngravingIsDrawnOnThePartWhereItWasPlaced()
    {
      string path = this.WritePart(marked: true);

      IReadOnlyList<IReadOnlyList<Point>> strokes = PartMarkDrawing.Strokes(path, 5, 7, 0, false);

      strokes.Should().NotBeEmpty("the part has A1-1 engraved in the middle of it");
      var all = strokes.SelectMany(s => s).ToList();
      all.Min(p => p.X).Should().BeGreaterThan(14, "the mark sits at 10 on a part placed at 5");
      all.Max(p => p.X).Should().BeLessThan(22);
      all.Min(p => p.Y).Should().BeGreaterThan(15, "and at 10 on a part placed at 7");
      all.Max(p => p.Y).Should().BeLessThan(19);
    }

    /// <summary>The screen has to show what will be cut, so a turned-over part shows its marking turned
    /// over with it: on the other side of the part, still the right way up.</summary>
    [Fact]
    public void AMirroredPartsEngravingFollowsItOver()
    {
      string path = this.WritePart(marked: true);

      var upright = PartMarkDrawing.Strokes(path, 0, 0, 0, false).SelectMany(s => s).ToList();
      var turned = PartMarkDrawing.Strokes(path, 0, 0, 0, true).SelectMany(s => s).ToList();

      turned.Should().NotBeEmpty();
      turned.Max(p => p.X).Should().BeLessThan(0, "the part was reflected about the origin and the mark went with it");
      turned.Min(p => p.Y).Should().BeApproximately(upright.Min(p => p.Y), 0.5, "the letters are not turned over");
      (turned.Max(p => p.Y) - turned.Min(p => p.Y)).Should().BeApproximately(
        upright.Max(p => p.Y) - upright.Min(p => p.Y),
        0.5,
        "they are the same letters, the same way up");
    }

    /// <summary>
    /// A mark the operator centred hangs from its SECOND point; a CAD program leaves the first at
    /// nothing. Drawn from the first, a centred part number lands in the corner of the sheet while the
    /// exporter writes it in the middle of the part, and the screen stops telling the truth about what
    /// will be cut.
    /// </summary>
    [Fact]
    public void ACentredMarkIsDrawnWhereItHangsFrom()
    {
      string path = this.WritePart(marked: true, centred: true);

      var drawn = PartMarkDrawing.Strokes(path, 0, 0, 0, false).SelectMany(s => s).ToList();

      drawn.Should().NotBeEmpty();
      double midX = (drawn.Min(p => p.X) + drawn.Max(p => p.X)) / 2.0;
      double midY = (drawn.Min(p => p.Y) + drawn.Max(p => p.Y)) / 2.0;
      midX.Should().BeApproximately(10, 1.0, "the mark is centred on the middle of the part");
      midY.Should().BeApproximately(10, 1.0);
    }

    [Fact]
    public void APartWithNothingEngravedDrawsNothing()
    {
      string path = this.WritePart(marked: false);

      PartMarkDrawing.Strokes(path, 0, 0, 0, false).Should().BeEmpty();
      PartMarkDrawing.Placed(path, 0, 0, 0, false).Should().BeNull();
    }

    /// <summary>A 20 x 20 part with A1-1 engraved in the middle of it.</summary>
    private string WritePart(bool marked, bool centred = false)
    {
      string path = Path.Combine(this.tempDir, (marked ? (centred ? "centred" : "marked") : "bare") + ".dxf");
      var dxf = new DxfFile();
      dxf.Entities.Add(new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(0, 0, 0)),
        new DxfVertex(new DxfPoint(20, 0, 0)),
        new DxfVertex(new DxfPoint(20, 20, 0)),
        new DxfVertex(new DxfPoint(0, 20, 0)),
        new DxfVertex(new DxfPoint(0, 0, 0)),
      })
      { IsClosed = true, Color = DxfColor.FromIndex(White) });

      if (marked)
      {
        var mark = new DxfText(new DxfPoint(10, 10, 0), 2D, "A1-1") { Color = DxfColor.FromIndex(Yellow) };
        if (centred)
        {
          mark.Location = new DxfPoint(0, 0, 0);
          mark.SecondAlignmentPoint = new DxfPoint(10, 10, 0);
          mark.HorizontalTextJustification = DxfHorizontalTextJustification.Center;
          mark.VerticalTextJustification = DxfVerticalTextJustification.Middle;
        }

        dxf.Entities.Add(mark);
      }

      dxf.Save(path);
      return path;
    }
  }
}
