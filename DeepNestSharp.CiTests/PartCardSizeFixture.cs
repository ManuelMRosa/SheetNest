namespace DeepNestSharp.CiTests
{
  using System;
  using System.Diagnostics;
  using System.IO;
  using System.Threading;
  using DeepNestLib.NestProject;
  using DeepNestSharp.Domain.Models;
  using FluentAssertions;
  using Xunit;

  /// <summary>
  /// A part card prints the part's size, and the size comes from geometry that loads in the background.
  /// The card reads the size before anything else has asked for that geometry, so reading it has to start
  /// the load. Otherwise the card says "Size:" with nothing after it for as long as the job is open.
  /// </summary>
  public class PartCardSizeFixture
  {
    [Fact]
    public void ReadingTheSizeLoadsThePartSoTheSizeArrives()
    {
      string dir = Path.Combine(Path.GetTempPath(), "SheetNestTests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(dir);
      string path = Path.Combine(dir, "plate.dxf");
      try
      {
        WriteRectangle(path, 10, 20);
        var part = new ObservableDetailLoadInfo(new DetailLoadInfo { Path = path });

        // Nothing else touches the geometry here, so these reads are the only thing that can start the load.
        string size = part.SizeLabel;
        var clock = Stopwatch.StartNew();
        while (string.IsNullOrEmpty(size) && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
          Thread.Sleep(100);
          size = part.SizeLabel;
        }

        size.Should().Be("10.00 × 20.00", "the size arrives once the part has loaded, with no tooltip having asked first");
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
