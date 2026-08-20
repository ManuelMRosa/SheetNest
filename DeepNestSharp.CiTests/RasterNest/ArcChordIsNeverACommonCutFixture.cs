namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.NestProject;
  using DeepNestLib.Placement;
  using DeepNestSharp.RasterNest;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// A curve reaches the nester as straight chords, and a chord lies INSIDE its arc. Pulling a
  /// neighbour up to one kerf from a chord therefore parks it closer than one kerf from the real
  /// curve, and the cut eats the difference. Nothing catches it downstream either: every later check
  /// measures the same polygon, which has the same chord in it.
  /// </summary>
  public class ArcChordIsNeverACommonCutFixture
  {
    private const double Kerf = 0.3;
    private const double Radius = 13.139;

    private readonly ITestOutputHelper output;

    public ArcChordIsNeverACommonCutFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    /// <summary>
    /// The arc runs 39 to 141 degrees, which with the parser's 6 degree ceiling on arc steps samples
    /// 39, 45 ... 87, 93 ... So the chord from 87 to 93 straddles the top dead centre symmetrically and
    /// comes out EXACTLY horizontal: an axis-aligned face, long enough to qualify, that no draughtsman
    /// ever drew.
    /// </summary>
    [Fact]
    public void ABarIsNotWeldedOntoTheChordOfAnArc()
    {
      var arcPart = ArcTopped();
      var pts = arcPart.Points;

      // Premise, asserted rather than assumed: if the tessellation ever changes, this fixture stops
      // reproducing the case and has to be rebuilt rather than quietly passing.
      int flat = -1;
      for (int i = 0; i < pts.Length; i++)
      {
        var a = pts[i];
        var b = pts[(i + 1) % pts.Length];
        if (Math.Abs(a.Y - b.Y) < 1e-9 && Math.Abs(a.X - b.X) > 2 * Kerf && a.Y > Radius * 0.9)
        {
          flat = i;
          break;
        }
      }

      flat.Should().BeGreaterOrEqualTo(0, "the fixture needs a horizontal chord near the top of the arc to reproduce this");
      double chordY = pts[flat].Y;
      this.output.WriteLine(FormattableString.Invariant(
        $"chord at y={chordY:0.####}, true arc apex y={Radius:0.####}, so the curve bulges {Radius - chordY:0.####} past it"));

      // A bar parked one-and-a-bit kerfs above that chord, overlapping it well.
      double barBottom = chordY + 0.35;
      var bar = new[]
      {
        new SvgPoint(-0.5, barBottom), new SvgPoint(0.5, barBottom),
        new SvgPoint(0.5, barBottom + 4), new SvgPoint(-0.5, barBottom + 4),
      };

      var a0 = new PartPlacement(arcPart) { X = 0, Y = 0, Rotation = 0, Source = 0 };
      var b0 = new PartPlacement(new NoFitPolygon(bar)) { X = 0, Y = 0, Rotation = 0, Source = 1 };

      SparrowNestService.SnapCommonLineEdges(
        new List<IPartPlacement> { a0, b0 },
        new Dictionary<int, double> { { 0, Kerf }, { 1, Kerf } },
        new Dictionary<int, INfp>
        {
          { 0, SparrowNestService.ToolingFootprint(arcPart, null, Kerf, 0) },
          { 1, SparrowNestService.ToolingFootprint(new NoFitPolygon(bar), null, Kerf, 0) },
        },
        new Dictionary<int, (CommonCuttingMode Cc, int ShareKey)>
        {
          { 0, (CommonCuttingMode.Unrestricted, 0) },
          { 1, (CommonCuttingMode.Unrestricted, 1) },
        });

      b0.Y.Should().Be(0, "a chord is not a face, so there is nothing here to share a cut with");
      b0.X.Should().Be(0);
    }

    /// <summary>
    /// The control, and the half that makes the test worth anything: the SAME part, same distance, but
    /// the neighbour offered a real straight edge instead. That one still welds.
    /// </summary>
    [Fact]
    public void ARealStraightEdgeOfTheSamePartStillWelds()
    {
      var arcPart = ArcTopped();

      // Its right-hand side is a genuine LINE, running up from y = 0 at x = +10.212.
      double sideX = arcPart.Points.Max(p => p.X);
      var bar = new[]
      {
        new SvgPoint(sideX + 0.35, 1), new SvgPoint(sideX + 3.35, 1),
        new SvgPoint(sideX + 3.35, 6), new SvgPoint(sideX + 0.35, 6),
      };

      var a0 = new PartPlacement(arcPart) { X = 0, Y = 0, Rotation = 0, Source = 0 };
      var b0 = new PartPlacement(new NoFitPolygon(bar)) { X = 0, Y = 0, Rotation = 0, Source = 1 };

      SparrowNestService.SnapCommonLineEdges(
        new List<IPartPlacement> { a0, b0 },
        new Dictionary<int, double> { { 0, Kerf }, { 1, Kerf } },
        new Dictionary<int, INfp>
        {
          { 0, SparrowNestService.ToolingFootprint(arcPart, null, Kerf, 0) },
          { 1, SparrowNestService.ToolingFootprint(new NoFitPolygon(bar), null, Kerf, 0) },
        },
        new Dictionary<int, (CommonCuttingMode Cc, int ShareKey)>
        {
          { 0, (CommonCuttingMode.Unrestricted, 0) },
          { 1, (CommonCuttingMode.Unrestricted, 1) },
        });

      (b0.PlacedPart.MinX - sideX).Should().BeApproximately(Kerf, 1e-6,
        "the side of the part is a real edge and still shares its cut");
    }

    /// <summary>A D-shape: an arc across the top, three real lines closing it underneath.</summary>
    private static INfp ArcTopped()
    {
      double x = Radius * Math.Cos(39 * Math.PI / 180.0);
      double y = Radius * Math.Sin(39 * Math.PI / 180.0);

      var entities = new List<DxfEntity>
      {
        new DxfArc(new DxfPoint(0, 0, 0), Radius, 39, 141),
        new DxfLine(new DxfPoint(-x, y, 0), new DxfPoint(-x, 0, 0)),
        new DxfLine(new DxfPoint(-x, 0, 0), new DxfPoint(x, 0, 0)),
        new DxfLine(new DxfPoint(x, 0, 0), new DxfPoint(x, y, 0)),
      };

      return DxfParser.ConvertDxfToRawDetail("arc-topped.dxf", entities).ToNfp();
    }
  }
}
