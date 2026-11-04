namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Linq;
  using DeepNestLib.IO;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// Elliptical arcs: one whose sweep runs through zero was read as a whole ellipse, and a mirrored copy
  /// was exported cutting the other half of the ellipse.
  /// </summary>
  public class DxfEllipseArcFixture
  {
    private static DxfEllipse Arc(double from, double to)
      => new DxfEllipse(new DxfPoint(10, 20, 0), new DxfVector(100, 0, 0), 0.5) { StartParameter = from, EndParameter = to };

    private static (double X, double Y) At(DxfEllipse e, double t)
    {
      // The minor axis is the major one turned a quarter anticlockwise, scaled by the ratio.
      double mx = e.MajorAxis.X, my = e.MajorAxis.Y, r = e.MinorAxisRatio;
      return (e.Center.X + (mx * Math.Cos(t)) - (r * my * Math.Sin(t)), e.Center.Y + (my * Math.Cos(t)) + (r * mx * Math.Sin(t)));
    }

    private static (double X, double Y) Middle(DxfEllipse e)
    {
      double to = e.EndParameter < e.StartParameter ? e.EndParameter + (2 * Math.PI) : e.EndParameter;
      return At(e, (e.StartParameter + to) / 2);
    }

    [Fact]
    public void AnArcRunningThroughZeroIsTheShortPieceNotTheWholeEllipse()
    {
      var raw = DxfParser.ConvertDxfToRawDetail("arc.dxf", new DxfEntity[] { Arc(5.5, 0.8) });

      var points = raw.Outers.SelectMany(o => o.Points).ToList();
      points.Should().NotBeEmpty();
      points.Should().OnlyContain(p => p.X > 10 + 60, "5.5 to 0.8 rad is the quarter around the +X end of the major axis");
    }

    [Fact]
    public void AFullEllipseIsStillWhole()
    {
      var raw = DxfParser.ConvertDxfToRawDetail("ellipse.dxf", new DxfEntity[] { Arc(0, 2 * Math.PI) });

      var points = raw.Outers.SelectMany(o => o.Points).ToList();
      points.Min(p => p.X).Should().BeApproximately(10 - 100, 1);
      points.Max(p => p.X).Should().BeApproximately(10 + 100, 1);
    }

    [Theory]
    [InlineData(0, Math.PI / 2, 0)]
    [InlineData(5.5, 0.8, 0)]
    [InlineData(1, 2.5, 90)]
    public void AMirroredArcIsTheReflectionOfTheOriginal(double from, double to, double rotation)
    {
      var arc = Arc(from, to);
      var (ox, oy) = Middle(arc);

      // The middle of the arc, carried through the same transform as a zero-length line, is where the middle of
      // the mirrored arc has to be.
      var marker = new DxfLine(new DxfPoint(ox, oy, 0), new DxfPoint(ox, oy, 0));
      var moved = DxfExporter.OffsetToNest(new DxfEntity[] { arc, marker }, new DxfPoint(5, 7, 0), rotation, true);
      var mirrored = moved.OfType<DxfEllipse>().Single();
      var expected = moved.OfType<DxfLine>().Single().P1;

      var (mx, my) = Middle(mirrored);
      mx.Should().BeApproximately(expected.X, 1e-6);
      my.Should().BeApproximately(expected.Y, 1e-6);
    }
  }
}
