namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib.IO;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// A curve reaches the nester as a run of straight chords, and until now nothing said so: a chord was
  /// indistinguishable from an edge the drawing really has. It matters because a chord lies INSIDE its
  /// arc, so anything that treats one as a real face gives away the bulge.
  /// </summary>
  public class CurvedSegmentMarkingFixture
  {
    /// <summary>
    /// Every entity type the parser tessellates has to mark what it produced. This is the test that
    /// catches a curve type added later without marking it: add the case, forget the flag, go red.
    /// </summary>
    [Theory]
    [InlineData("arc")]
    [InlineData("circle")]
    [InlineData("ellipse")]
    [InlineData("spline")]
    [InlineData("lwpolyline-bulge")]
    [InlineData("polyline-bulge")]
    public void EverySortOfCurveMarksTheChordsItWasCutInto(string kind)
    {
      var (flags, straightEntities) = Parse(kind);

      flags.Should().NotBeNull("the parser has to record provenance");
      flags.Count(c => c).Should().BeGreaterThan(2, $"a tessellated {kind} is several chords");
      flags.Count(c => !c).Should().Be(straightEntities, "only the straight edges of the shape stay unmarked");
    }

    /// <summary>
    /// The negative controls. A shape with no curve in it must not have its edges marked, or the flag
    /// would be useless: everything would look like a chord and nothing could ever share a cut.
    /// </summary>
    [Theory]
    [InlineData("lines")]
    [InlineData("lwpolyline-straight")]
    public void StraightGeometryIsNotMarked(string kind)
    {
      var (flags, straightEntities) = Parse(kind);

      flags.Count(c => !c).Should().Be(straightEntities, "every real edge is unmarked");
      flags.Count(c => c).Should().BeLessOrEqualTo(1, "at most the invented closing segment is marked");
    }

    /// <summary>
    /// The segment the chainer invents to close a contour is nobody's edge: it can span a gap of up to
    /// the closing threshold. It is marked so it can never be mistaken for a face worth sharing.
    /// </summary>
    [Fact]
    public void TheInventedClosingSegmentIsMarked()
    {
      var (flags, _) = Parse("lines");
      flags.Last().Should().BeTrue();
    }

    private static (IReadOnlyList<bool> Flags, int StraightEntities) Parse(string kind)
    {
      var entities = new List<DxfEntity>();
      int straight;

      switch (kind)
      {
        case "arc":
          // A half-round-ended shape: an arc over the top, closed by three straight lines.
          entities.Add(new DxfArc(new DxfPoint(0, 0, 0), 10, 0, 180));
          entities.Add(new DxfLine(new DxfPoint(-10, 0, 0), new DxfPoint(-10, -10, 0)));
          entities.Add(new DxfLine(new DxfPoint(-10, -10, 0), new DxfPoint(10, -10, 0)));
          entities.Add(new DxfLine(new DxfPoint(10, -10, 0), new DxfPoint(10, 0, 0)));
          straight = 3;
          break;

        case "circle":
          entities.Add(new DxfCircle(new DxfPoint(0, 0, 0), 10));
          straight = 0;
          break;

        case "ellipse":
          entities.Add(new DxfEllipse(new DxfPoint(0, 0, 0), new DxfVector(10, 0, 0), 0.5));
          straight = 0;
          break;

        case "spline":
          entities.Add(ClosedSpline());
          straight = 0;
          break;

        case "lwpolyline-bulge":
          entities.Add(BulgedLwPolyline());
          straight = 3;
          break;

        case "polyline-bulge":
          entities.Add(BulgedPolyline());
          straight = 3;
          break;

        case "lines":
          entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(10, 0, 0)));
          entities.Add(new DxfLine(new DxfPoint(10, 0, 0), new DxfPoint(10, 10, 0)));
          entities.Add(new DxfLine(new DxfPoint(10, 10, 0), new DxfPoint(0, 10, 0)));
          entities.Add(new DxfLine(new DxfPoint(0, 10, 0), new DxfPoint(0, 0, 0)));
          straight = 4;
          break;

        case "lwpolyline-straight":
          {
            var p = new DxfLwPolyline(new[]
            {
              new DxfLwPolylineVertex { X = 0, Y = 0 },
              new DxfLwPolylineVertex { X = 10, Y = 0 },
              new DxfLwPolylineVertex { X = 10, Y = 10 },
              new DxfLwPolylineVertex { X = 0, Y = 10 },
            })
            { IsClosed = true };
            entities.Add(p);
            straight = 4;
            break;
          }

        default:
          throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
      }

      var raw = DxfParser.ConvertDxfToRawDetail($"{kind}.dxf", entities);
      var contour = raw.Outers.OrderByDescending(o => Math.Abs(Geometry.GeometryUtil.PolygonArea(o.Points))).First();
      return (contour.CurvedSegments, straight);
    }

    /// <summary>A square with one side bulged into an arc.</summary>
    private static DxfLwPolyline BulgedLwPolyline()
    {
      return new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0, Y = 0, Bulge = 0.6 },   // this side becomes an arc
        new DxfLwPolylineVertex { X = 10, Y = 0 },
        new DxfLwPolylineVertex { X = 10, Y = 10 },
        new DxfLwPolylineVertex { X = 0, Y = 10 },
      })
      { IsClosed = true };
    }

    private static DxfPolyline BulgedPolyline()
    {
      var poly = new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(0, 0, 0)) { Bulge = 0.6 },
        new DxfVertex(new DxfPoint(10, 0, 0)),
        new DxfVertex(new DxfPoint(10, 10, 0)),
        new DxfVertex(new DxfPoint(0, 10, 0)),
      })
      { IsClosed = true };
      return poly;
    }

    /// <summary>A closed-ish spline loop, enough for the parser to tessellate.</summary>
    private static DxfSpline ClosedSpline()
    {
      var spline = new DxfSpline();
      spline.DegreeOfCurve = 3;
      foreach (var p in new[]
      {
        new DxfPoint(0, 0, 0), new DxfPoint(10, -6, 0), new DxfPoint(20, 0, 0),
        new DxfPoint(20, 10, 0), new DxfPoint(10, 16, 0), new DxfPoint(0, 10, 0), new DxfPoint(0, 0, 0),
      })
      {
        spline.ControlPoints.Add(new DxfControlPoint(p));
      }

      int n = spline.ControlPoints.Count;
      for (int i = 0; i < n + spline.DegreeOfCurve + 1; i++)
      {
        spline.KnotValues.Add(Math.Min(Math.Max(i - spline.DegreeOfCurve, 0), n - spline.DegreeOfCurve));
      }

      return spline;
    }
  }
}
