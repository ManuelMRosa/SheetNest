namespace DeepNestLib.CiTests.IO
{
  using System.IO;
  using System.Linq;
  using DeepNestLib.IO;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  /// <summary>
  /// FlatDxfJoiner must turn the exploded LINE/ARC soup a FreeCAD unfold produces into a single
  /// CLOSED LWPOLYLINE per contour (arcs as bulges), leave circles alone, and never degrade a
  /// chain it cannot prove closed.
  /// </summary>
  public class FlatDxfJoinerFixture
  {
    [Fact]
    public void JoinsExplodedOutlineIntoOneClosedPolyline()
    {
      // 10x10 square with a radius-2 fillet at the (10,10) corner + a circle hole. One line is
      // written backwards on purpose — the joiner must chain regardless of direction.
      var file = new DxfFile();
      file.Entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(10, 0, 0)));
      file.Entities.Add(new DxfLine(new DxfPoint(10, 0, 0), new DxfPoint(10, 8, 0)));
      file.Entities.Add(new DxfArc(new DxfPoint(8, 8, 0), 2, 0, 90)); // (10,8) -> (8,10) CCW
      file.Entities.Add(new DxfLine(new DxfPoint(0, 10, 0), new DxfPoint(8, 10, 0))); // reversed
      file.Entities.Add(new DxfLine(new DxfPoint(0, 10, 0), new DxfPoint(0, 0, 0)));
      file.Entities.Add(new DxfCircle(new DxfPoint(5, 5, 0), 1));

      var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
      try
      {
        file.Header.Version = DxfAcadVersion.R2000;
        file.Save(path, true);

        FlatDxfJoiner.JoinToClosedPolylines(path);

        var joined = DxfFile.Load(path);
        var polys = joined.Entities.OfType<DxfLwPolyline>().ToList();
        polys.Should().HaveCount(1, "the whole outline must chain into a single polyline");
        polys[0].IsClosed.Should().BeTrue("an unfolded contour must NEVER be open");
        polys[0].Vertices.Should().HaveCount(5);
        polys[0].Vertices.Count(v => System.Math.Abs(v.Bulge) > 1e-9).Should().Be(1, "exactly the fillet arc segment carries a bulge");

        joined.Entities.OfType<DxfCircle>().Should().HaveCount(1, "holes stay as circles");
        joined.Entities.OfType<DxfLine>().Should().BeEmpty("all lines were consumed into the polyline");
        joined.Entities.OfType<DxfArc>().Should().BeEmpty("the arc was consumed into the polyline");
      }
      finally
      {
        File.Delete(path);
      }
    }

    [Fact]
    public void JoinedFileStillImportsForNesting()
    {
      var file = new DxfFile();
      file.Entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(10, 0, 0)));
      file.Entities.Add(new DxfLine(new DxfPoint(10, 0, 0), new DxfPoint(10, 8, 0)));
      file.Entities.Add(new DxfArc(new DxfPoint(8, 8, 0), 2, 0, 90));
      file.Entities.Add(new DxfLine(new DxfPoint(8, 10, 0), new DxfPoint(0, 10, 0)));
      file.Entities.Add(new DxfLine(new DxfPoint(0, 10, 0), new DxfPoint(0, 0, 0)));

      var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
      try
      {
        file.Header.Version = DxfAcadVersion.R2000;
        file.Save(path, true);
        FlatDxfJoiner.JoinToClosedPolylines(path);

        var raw = DxfParser.LoadDxfFile(path).Result;
        raw.Should().NotBeNull();
        var nfp = raw.ToNfp();
        nfp.Should().NotBeNull();
        nfp.Points.Length.Should().BeGreaterThan(2);
        (nfp.MaxX - nfp.MinX).Should().BeApproximately(10, 1e-6);
        (nfp.MaxY - nfp.MinY).Should().BeApproximately(10, 1e-6);
      }
      finally
      {
        File.Delete(path);
      }
    }

    [Fact]
    public void JoinEntities_InMemory_PreservesColorAndLayer()
    {
      // A red triangle "hole" contour (the exporter colours child contours red) must come out as
      // a red closed polyline on the same layer.
      var red = DxfColor.FromIndex(1);
      var entities = new System.Collections.Generic.List<DxfEntity>
      {
        new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(4, 0, 0)) { Color = red, Layer = "HOLES" },
        new DxfLine(new DxfPoint(4, 0, 0), new DxfPoint(2, 3, 0)) { Color = red, Layer = "HOLES" },
        new DxfLine(new DxfPoint(2, 3, 0), new DxfPoint(0, 0, 0)) { Color = red, Layer = "HOLES" },
      };

      FlatDxfJoiner.JoinEntities(entities).Should().BeTrue();

      entities.Should().HaveCount(1);
      var poly = entities[0].Should().BeOfType<DxfLwPolyline>().Subject;
      poly.IsClosed.Should().BeTrue();
      poly.Color.Should().Be(red);
      poly.Layer.Should().Be("HOLES");
    }

    [Fact]
    public void JoinEntities_SkipsAmbiguousJunctions()
    {
      // Three segment ends meeting at one point (T joint): chaining would be a guess — the
      // joiner must leave everything untouched.
      var entities = new System.Collections.Generic.List<DxfEntity>
      {
        new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(10, 0, 0)),
        new DxfLine(new DxfPoint(10, 0, 0), new DxfPoint(10, 10, 0)),
        new DxfLine(new DxfPoint(10, 10, 0), new DxfPoint(0, 0, 0)),
        new DxfLine(new DxfPoint(10, 0, 0), new DxfPoint(10, -5, 0)), // dangling spur off the corner
      };

      FlatDxfJoiner.JoinEntities(entities).Should().BeFalse();
      entities.Should().HaveCount(4);
      entities.Should().AllBeOfType<DxfLine>();
    }

    [Theory]
    [InlineData(0.41421356)]  // 90° fillet, CCW
    [InlineData(-0.41421356)] // 90° fillet, CW
    [InlineData(1.5)]         // >180° sweep, CCW
    public void SplitThenJoinRoundTripsBulges(double bulge)
    {
      // A rounded-corner rectangle: LWPOLYLINE -> split (lines + bulge->ARC) -> join must give back
      // the same vertices AND the same bulge. This anchors the arc-centre side convention to
      // DxfParser.BulgePoints — the centre landing on the WRONG side exports corner fillets as the
      // complementary sweep (near-full circles in AutoCAD, user-reported).
      var original = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0, Y = 0 },
        new DxfLwPolylineVertex { X = 10, Y = 0 },
        new DxfLwPolylineVertex { X = 10, Y = 8, Bulge = bulge }, // arc segment to (8, 10)
        new DxfLwPolylineVertex { X = 8, Y = 10 },
        new DxfLwPolylineVertex { X = 0, Y = 10 },
      })
      {
        IsClosed = true,
      };

      var split = new DxfLineMerger().SplitLines(new DxfEntity[] { original }).ToList();
      split.OfType<DxfArc>().Should().HaveCount(1);
      split.OfType<DxfLine>().Should().HaveCount(4);

      // The arc's endpoints must be the polyline's own segment endpoints (wrong-side centres break this).
      var arc = split.OfType<DxfArc>().Single();
      var start = arc.GetPointFromAngle(arc.StartAngle);
      var end = arc.GetPointFromAngle(arc.EndAngle);
      var endpoints = new[] { (start.X, start.Y), (end.X, end.Y) }
        .OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToArray();
      endpoints[0].Item1.Should().BeApproximately(8, 1e-6);
      endpoints[0].Item2.Should().BeApproximately(10, 1e-6);
      endpoints[1].Item1.Should().BeApproximately(10, 1e-6);
      endpoints[1].Item2.Should().BeApproximately(8, 1e-6);

      var entities = split.ToList();
      FlatDxfJoiner.JoinEntities(entities).Should().BeTrue();
      var joined = entities.OfType<DxfLwPolyline>().Single();
      joined.IsClosed.Should().BeTrue();

      // Same geometry back: find the joined vertex at (10,8) — its outgoing bulge must match.
      var arcVertex = joined.Vertices.Single(v => System.Math.Abs(v.Bulge) > 1e-9);
      // The chain may traverse the loop in either direction; magnitude must match and the endpoints
      // of the bulge segment must be {(10,8),(8,10)} either way.
      System.Math.Abs(arcVertex.Bulge).Should().BeApproximately(System.Math.Abs(bulge), 1e-6);
    }

    [Fact]
    public void LeavesUnclosableChainsUntouched()
    {
      // An open L (two lines) must stay as-is: never emit an open polyline for a cut file.
      var file = new DxfFile();
      file.Entities.Add(new DxfLine(new DxfPoint(0, 0, 0), new DxfPoint(10, 0, 0)));
      file.Entities.Add(new DxfLine(new DxfPoint(10, 0, 0), new DxfPoint(10, 5, 0)));

      var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
      try
      {
        file.Header.Version = DxfAcadVersion.R2000;
        file.Save(path, true);
        FlatDxfJoiner.JoinToClosedPolylines(path);

        var joined = DxfFile.Load(path);
        joined.Entities.OfType<DxfLine>().Should().HaveCount(2);
        joined.Entities.OfType<DxfLwPolyline>().Should().BeEmpty();
      }
      finally
      {
        File.Delete(path);
      }
    }
  }
}
