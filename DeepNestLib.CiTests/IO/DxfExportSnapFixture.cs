namespace DeepNestLib.CiTests.IO
{
  using System.Linq;
  using DeepNestLib.IO;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// The export snaps all coordinates to the micro-inch grid so that coincident geometry (a
  /// common-line shared edge drawn by two independently transformed placements) is numerically
  /// IDENTICAL — measuring it in CAD reads exactly zero instead of float-transform noise (~1e-8).
  /// </summary>
  public class DxfExportSnapFixture
  {
    [Fact]
    public void SnapMakesNoisyCoincidentEdgesIdentical()
    {
      // The same hypotenuse from two placements, endpoints off by ~1e-8 (measured user case).
      var a = new DxfLine(new DxfPoint(2.25, 5.0, 0), new DxfPoint(9.84, 15.32, 0));
      var b = new DxfLine(new DxfPoint(9.84000001, 15.32000001, 0), new DxfPoint(2.25000001, 5.00000001, 0));

      var snapped = DxfExporter.SnapToGrid(new DxfEntity[] { a, b }).Cast<DxfLine>().ToList();

      snapped[1].P1.Should().Be(snapped[0].P2, "on the micro-inch grid the noise rounds away");
      snapped[1].P2.Should().Be(snapped[0].P1);
    }

    [Fact]
    public void SnappedEdgesStillMergeToOneCut()
    {
      var a = new DxfLine(new DxfPoint(2.25, 5.0, 0), new DxfPoint(9.84, 15.32, 0));
      var b = new DxfLine(new DxfPoint(9.84000001, 15.32000001, 0), new DxfPoint(2.25000001, 5.00000001, 0));

      var snapped = DxfExporter.SnapToGrid(new DxfEntity[] { a, b }).ToList();
      var merged = new DxfLineMerger().MergeLines(snapped).Cast<DxfLine>().ToList();

      merged.Should().HaveCount(1);
      var line = merged[0];
      foreach (double v in new[] { line.P1.X, line.P1.Y, line.P2.X, line.P2.Y })
      {
        System.Math.Round(v, 6).Should().Be(v, "every exported coordinate sits exactly on the grid");
      }
    }

    [Fact]
    public void SnapRoundsArcsAndPolylines()
    {
      var arc = new DxfArc(new DxfPoint(1.0000000004, 2.0000000004, 0), 0.5000000004, 10.0000000004, 90.0000000004);
      var lw = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0.0000000004, Y = 1.0000000004, Bulge = 0.4142135623730951 },
        new DxfLwPolylineVertex { X = 3.0000000004, Y = 4.0000000004 },
      });

      DxfExporter.SnapToGrid(new DxfEntity[] { arc, lw }).ToList();

      arc.Center.X.Should().Be(1.0);
      arc.Radius.Should().Be(0.5);
      arc.StartAngle.Should().Be(10.0);
      lw.Vertices[0].X.Should().Be(0.0);
      lw.Vertices[1].Y.Should().Be(4.0);
      lw.Vertices[0].Bulge.Should().Be(0.4142135623730951, "bulge is a dimensionless ratio and must not be snapped");
    }
  }
}
