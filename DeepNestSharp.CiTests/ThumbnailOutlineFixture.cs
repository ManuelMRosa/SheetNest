namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Windows.Media;
  using DeepNestLib;
  using DeepNestLib.Placement;
  using DeepNestSharp.Ui;
  using DeepNestSharp.Ui.Converters;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// The parts list and the Nests panel draw their thumbnails the way the printed report draws a nest:
  /// outlines only, nothing filled. A part's colour is still shown in the list, as a small square beside
  /// the thumbnail rather than painted into it.
  /// </summary>
  public class ThumbnailOutlineFixture
  {
    [Fact]
    public void ANestThumbnailDrawsTheSheetAndEveryPartAsOutlines()
    {
      var ring = Square("ring.dxf", 10);
      ring.Children.Add(Square("hole", 4, offset: 3));
      var placements = new List<IPartPlacement>
      {
        new PartPlacement(Square("a.dxf", 10)) { X = 0, Y = 0, Source = 0 },
        new PartPlacement(ring) { X = 20, Y = 0, Source = 1 },
      };
      var sheet = new SheetPlacement(PlacementTypeEnum.Gravity, Sheet.NewSheet(1, 100, 50), placements, 0, false);

      var image = (DrawingImage)NestCard.DrawThumbnail(sheet);

      var drawings = ((DrawingGroup)image.Drawing).Children.OfType<GeometryDrawing>().ToList();
      drawings.Should().HaveCount(3, "the sheet and the two parts, the ring's hole drawn inside its own outline");
      drawings.Should().OnlyContain(d => d.Brush == null, "outlines only, like the report");
      drawings.Should().OnlyContain(d => d.Pen != null, "an outline with no pen would draw nothing at all");
    }

    /// <summary>
    /// Both halves on purpose. The list asks for outlines; the Edit Part dialog asks for nothing and keeps
    /// the filled preview it has always had. Both come out of one converter with one cache, so the test
    /// also proves an outline asked for first is not handed back to the dialog, or the other way round.
    /// </summary>
    [Fact]
    public void ThePartListThumbnailIsAnOutlineWhileTheEditPartDialogStillFillsIt()
    {
      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      string path = Path.Combine(dir, "plate.dxf");
      try
      {
        WriteRectangle(path, 10, 20);
        var converter = new PartPreviewConverter();

        var outline = (DrawingImage)converter.Convert(path, typeof(ImageSource), "Outline", CultureInfo.InvariantCulture);
        var filled = (DrawingImage)converter.Convert(path, typeof(ImageSource), null, CultureInfo.InvariantCulture);

        ((GeometryDrawing)outline.Drawing).Brush.Should().BeNull("the parts list draws outlines, like the report");
        ((GeometryDrawing)filled.Drawing).Brush.Should().NotBeNull("the Edit Part dialog keeps its filled preview");
      }
      finally
      {
        try
        {
          Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
          // A load still reading the file keeps it open for a moment; a leftover temp file is harmless.
        }
      }
    }

    private static INfp Square(string name, double side, double offset = 0)
      => new NoFitPolygon(new List<SvgPoint>
      {
        new SvgPoint(offset, offset),
        new SvgPoint(offset + side, offset),
        new SvgPoint(offset + side, offset + side),
        new SvgPoint(offset, offset + side),
      })
      { Name = name };

    /// <summary>A closed LWPOLYLINE rectangle, the shape the real loader reads.</summary>
    private static void WriteRectangle(string path, double w, double h)
    {
      var file = new IxMilia.Dxf.DxfFile();
      file.Header.Version = IxMilia.Dxf.DxfAcadVersion.R2000; // LWPOLYLINE needs R13+ or it is dropped on save
      file.Entities.Add(new IxMilia.Dxf.Entities.DxfLwPolyline(new[]
      {
        new IxMilia.Dxf.Entities.DxfLwPolylineVertex { X = 0, Y = 0 },
        new IxMilia.Dxf.Entities.DxfLwPolylineVertex { X = w, Y = 0 },
        new IxMilia.Dxf.Entities.DxfLwPolylineVertex { X = w, Y = h },
        new IxMilia.Dxf.Entities.DxfLwPolylineVertex { X = 0, Y = h },
      })
      {
        IsClosed = true,
      });
      file.Save(path);
    }
  }
}
