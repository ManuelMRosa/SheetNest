namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Threading.Tasks;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// What a part carries engraved on it has to reach the sheet. The mark is not a contour, so it is not
  /// in the geometry the nest was measured from: it comes from the part's own file, and it has to land
  /// where the part landed, turned the way the part was turned, and still be readable.
  /// </summary>
  public class DxfExportPartMarkFixture : IDisposable
  {
    private const int Yellow = 2;
    private const int White = 7;

    private readonly string tempDir;

    public DxfExportPartMarkFixture()
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

    /// <summary>The whole point: the engraving reaches the sheet, on top of the part it belongs to.
    /// </summary>
    [Fact]
    public async Task AMarkIsWrittenOntoTheSheetWithItsPart()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement(mirrored: false);

      await new DxfExporter().Export(outPath, sheetPlacement, false, false);

      var marks = DxfFile.Load(outPath).Entities.OfType<DxfText>().ToList();
      marks.Should().ContainSingle();
      marks[0].Value.Should().Be("A1-1");
      marks[0].Location.X.Should().BeApproximately(7, 1e-6, "the mark sits 5 into a part placed at 2");
      marks[0].Location.Y.Should().BeApproximately(5.5, 1e-6);
    }

    /// <summary>
    /// A part turned over so it packs better must not come out with its number engraved backwards. The
    /// mark still lands on the part; what does not follow the part is the letters.
    /// </summary>
    [Fact]
    public async Task AMirroredPartsMarkStaysReadable()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement(mirrored: true);

      await new DxfExporter().Export(outPath, sheetPlacement, false, false);

      var marks = DxfFile.Load(outPath).Entities.OfType<DxfText>().ToList();
      marks.Should().ContainSingle();
      marks[0].Rotation.Should().BeApproximately(0, 1e-6, "reflected text would read backwards along its line");
      marks[0].HorizontalTextJustification.Should().Be(
        DxfHorizontalTextJustification.Right,
        "it hangs from its other end so the same words stay in the same place");

      // BOTH points, because which one a reader honours depends on the justification, and turning the
      // part over changed it. Checking only the one that reads well in a test leaves the other pointing
      // at the sheet origin, and that is the one the machine would follow.
      marks[0].Location.X.Should().BeApproximately(-3, 1e-6, "the mark follows the part it is on");
      marks[0].Location.Y.Should().BeApproximately(5.5, 1e-6);
      marks[0].SecondAlignmentPoint.X.Should().BeApproximately(-3, 1e-6, "this is the point a right-justified text hangs from");
      marks[0].SecondAlignmentPoint.Y.Should().BeApproximately(5.5, 1e-6);
    }

    /// <summary>
    /// A mark the operator centred on the part hangs from its SECOND point, not its first, so that is
    /// the one that has to end up on the part. The first is left at nothing in a drawing like that, and
    /// moving it alone would put the marking at the corner of the sheet.
    /// </summary>
    [Fact]
    public async Task ACentredMarkIsPlacedByThePointItHangsFrom()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement(mirrored: false, centred: true);

      await new DxfExporter().Export(outPath, sheetPlacement, false, false);

      DxfText mark = DxfFile.Load(outPath).Entities.OfType<DxfText>().Single();
      mark.SecondAlignmentPoint.X.Should().BeApproximately(7, 1e-6, "the mark sits 5 into a part placed at 2");
      mark.SecondAlignmentPoint.Y.Should().BeApproximately(5.5, 1e-6);
    }

    /// <summary>A shop that colours its layers leaves every mark saying "whatever my layer is". Writing
    /// the mark without its layer would lose the colour that makes it a mark.</summary>
    [Fact]
    public async Task TheLayerAMarkWasDrawnOnIsDeclaredInTheOutput()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement(mirrored: false, markLayer: "ENGRAVE");

      await new DxfExporter().Export(outPath, sheetPlacement, false, false);

      DxfFile written = DxfFile.Load(outPath);
      written.Entities.OfType<DxfText>().Single().Layer.Should().Be("ENGRAVE");
      DxfLayer declared = written.Layers.SingleOrDefault(l => l.Name == "ENGRAVE");
      declared.Should().NotBeNull("the output has to say what colour that layer is");
      DxfColorRoles.IsYellowish(DxfColorRoles.FromIndex(declared.Color)).Should().BeTrue();
    }

    /// <summary>A part with nothing engraved on it exports exactly as it did before any of this.
    /// </summary>
    [Fact]
    public async Task APartWithNothingEngravedWritesNothingExtra()
    {
      var (marked, markedPath) = this.BuildPlacement(mirrored: false);
      var (bare, barePath) = this.BuildPlacement(mirrored: false, withMark: false);

      await new DxfExporter().Export(markedPath, marked, false, false);
      await new DxfExporter().Export(barePath, bare, false, false);

      DxfFile.Load(barePath).Entities.OfType<DxfText>().Should().BeEmpty();
      DxfFile.Load(barePath).Entities.Count.Should().Be(DxfFile.Load(markedPath).Entities.Count - 1);
    }

    /// <summary>
    /// A yellow centre point is an ordinary thing to find in a part, and the transform has no way to move
    /// one. Left to reach it, it refuses the entity and the whole sheet goes with it: no export at all,
    /// for a dot.
    /// </summary>
    [Fact]
    public async Task AMarkThatCannotBeMovedDoesNotTakeTheSheetWithIt()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement(mirrored: false, strayPoint: true);

      await new DxfExporter().Export(outPath, sheetPlacement, false, false);

      DxfFile written = DxfFile.Load(outPath);
      written.Entities.OfType<DxfText>().Should().ContainSingle("the engraving still comes out");
      written.Entities.Should().Contain(e => e is DxfLwPolyline || e is DxfPolyline || e is DxfLine,
        "and so does the part");
    }

    /// <summary>
    /// A block of text says which way it reads TWICE: in its angle and in its X-axis direction vector,
    /// and a reader that finds both follows the vector. Turning the part without turning the vector
    /// leaves the mark lying the way it was drawn, off the part and sometimes off the sheet. Checking
    /// the angle alone passes while that happens, because the angle is what the screen reads.
    /// </summary>
    [Fact]
    public async Task ABlockMarkOnATurnedPartPointsTheWayThePartWasTurned()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement(mirrored: false, mtext: true, rotation: 90);

      await new DxfExporter().Export(outPath, sheetPlacement, false, false);

      DxfMText mark = DxfFile.Load(outPath).Entities.OfType<DxfMText>().Single();
      mark.XAxisDirection.X.Should().BeApproximately(0, 1e-6, "a quarter turn leaves the words running up the sheet");
      mark.XAxisDirection.Y.Should().BeApproximately(1, 1e-6);
      mark.InsertionPoint.X.Should().BeApproximately(-0.5, 1e-6, "and the mark still lands with its part");
      mark.InsertionPoint.Y.Should().BeApproximately(8, 1e-6);
    }

    /// <summary>
    /// Turning a part over reflects the line its mark sits on, and at 45 degrees that leaves the words
    /// running away from the reader, so they are turned the other half turn to face back. The direction
    /// vector has to carry that half turn too, or the mark comes out pointing down the reflected line.
    /// </summary>
    [Fact]
    public async Task AMirroredBlockMarkPointsTheWayItReads()
    {
      var (sheetPlacement, outPath) = this.BuildPlacement(mirrored: true, mtext: true, rotation: 45);

      await new DxfExporter().Export(outPath, sheetPlacement, false, false);

      DxfMText mark = DxfFile.Load(outPath).Entities.OfType<DxfMText>().Single();
      double readable = Math.Sqrt(2) / 2;
      mark.XAxisDirection.X.Should().BeApproximately(readable, 1e-6, "45 degrees, not the 225 the reflected line points at");
      mark.XAxisDirection.Y.Should().BeApproximately(readable, 1e-6);
      mark.AttachmentPoint.Should().Be(
        DxfAttachmentPoint.TopRight,
        "it hangs from its other end so the same words stay in the same place");
    }

    /// <summary>One 10 × 5 rectangle with A1-1 engraved in it, placed on a 96 × 48 sheet, backed by a
    /// real DXF on disk: the exporter reloads originals by <c>Part.Name</c>.</summary>
    private (ISheetPlacement SheetPlacement, string OutPath) BuildPlacement(
      bool mirrored,
      string markLayer = null,
      bool withMark = true,
      bool centred = false,
      bool strayPoint = false,
      bool mtext = false,
      double rotation = 0)
    {
      string name = withMark
        ? (markLayer ?? (centred ? "centred" : strayPoint ? "stray" : mtext ? "block" : "marked"))
        : "bare";
      string partPath = Path.Combine(this.tempDir, name + ".dxf");
      var dxf = new DxfFile();
      var outline = new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(0, 0, 0)),
        new DxfVertex(new DxfPoint(10, 0, 0)),
        new DxfVertex(new DxfPoint(10, 5, 0)),
        new DxfVertex(new DxfPoint(0, 5, 0)),
        new DxfVertex(new DxfPoint(0, 0, 0)),
      })
      { IsClosed = true, Color = DxfColor.FromIndex(White) };
      dxf.Entities.Add(outline);

      if (withMark)
      {
        DxfEntity mark;
        if (mtext)
        {
          // MTEXT did not exist before R13, so a part file left at the default version drops the mark
          // on the way to disk and the test would be measuring an empty drawing.
          dxf.Header.Version = DxfAcadVersion.R2000;
          mark = new DxfMText
          {
            InsertionPoint = new DxfPoint(5, 2.5, 0),
            InitialTextHeight = 0.5,
            Text = "A1-1",
          };
        }
        else
        {
          var text = new DxfText(new DxfPoint(5, 2.5, 0), 0.5, "A1-1");
          if (centred)
          {
            // Centred text keeps its real anchor in the second point and leaves the first at nothing,
            // which is exactly how a CAD program writes it.
            text.Location = new DxfPoint(0, 0, 0);
            text.SecondAlignmentPoint = new DxfPoint(5, 2.5, 0);
            text.HorizontalTextJustification = DxfHorizontalTextJustification.Center;
          }

          mark = text;
        }

        if (markLayer == null)
        {
          mark.Color = DxfColor.FromIndex(Yellow);
        }
        else
        {
          dxf.Layers.Add(new DxfLayer(markLayer, DxfColor.FromIndex(Yellow)));
          mark.Layer = markLayer;
        }

        dxf.Entities.Add(mark);
      }

      if (strayPoint)
      {
        dxf.Entities.Add(new DxfModelPoint(new DxfPoint(2, 2, 0)) { Color = DxfColor.FromIndex(Yellow) });
      }

      dxf.Save(partPath);

      var det = new NestExecutionHelper().LoadRawDetail(new FileInfo(partPath));
      det.TryConvertToNfp(0, out INfp nfp).Should().BeTrue();

      // A real nest turns the PART and copies that angle onto the placement (PartPlacementWorker:351),
      // and the export reads the part's angle, not the placement's. Setting only the placement would
      // leave the part flat and the test measuring a nest that never turned anything.
      if (rotation != 0)
      {
        nfp = nfp.Rotate(rotation);
      }

      var sheet = Sheet.NewSheet(1, 96, 48);
      var placements = new List<IPartPlacement>
      {
        new PartPlacement(nfp) { X = 2, Y = 3, Rotation = rotation, Id = 0, Source = 0, IsMirrored = mirrored },
      };

      var sheetPlacement = new SheetPlacement(PlacementTypeEnum.BoundingBox, sheet, placements, 0, SvgNest.Config.ClipperScale);
      return (sheetPlacement, Path.Combine(this.tempDir, name + "-out.dxf"));
    }
  }
}
