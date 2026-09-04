namespace DeepNestLib.CiTests.IO
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  public class DxfContourCatalogFixture
  {
    [Fact]
    public void ExplodedLineAndArcBecomeOneDeterministicExactContour()
    {
      DxfEntity[] forward =
      {
        new DxfLine(new DxfPoint(0D, 0D, 0D), new DxfPoint(10D, 0D, 0D)),
        new DxfArc(new DxfPoint(5D, 0D, 0D), 5D, 0D, 180D),
      };
      DxfEntity[] reordered =
      {
        new DxfArc(new DxfPoint(5D, 0D, 0D), 5D, 0D, 180D),
        new DxfLine(new DxfPoint(0D, 0D, 0D), new DxfPoint(10D, 0D, 0D)),
      };

      DxfContourCatalog first = DxfContourCatalog.Create("d-shape-a.dxf", forward);
      DxfContourCatalog second = DxfContourCatalog.Create("d-shape-b.dxf", reordered);

      first.SourceFingerprint.Should().Be(second.SourceFingerprint);
      DxfSourceContour contour = first.Contours.Should().ContainSingle().Subject;
      contour.IsChild.Should().BeFalse();
      contour.IsClosed.Should().BeTrue();
      contour.Primitives.Should().HaveCount(2);
      contour.Primitives.Select(item => item.Index).Should().Equal(0, 1);
      contour.Primitives.Should().ContainSingle(item => item.GeometryKind == CommonLineGeometryKind.Line);
      DxfSourcePrimitive arc = contour.Primitives.Should()
        .ContainSingle(item => item.GeometryKind == CommonLineGeometryKind.CircularArc).Subject;
      arc.Radius.Should().BeApproximately(5D, 1e-12D);
      Math.Abs(arc.SweepRadians).Should().BeApproximately(Math.PI, 1e-12D);
    }

    [Fact]
    public void LightweightPolylineBulgeRemainsAnAnalyticCircularArc()
    {
      var polyline = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D, Bulge = Math.Tan(Math.PI / 8D) },
        new DxfLwPolylineVertex { X = 10D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 10D },
        new DxfLwPolylineVertex { X = 0D, Y = 10D },
      })
      { IsClosed = true };

      DxfSourceContour contour = DxfContourCatalog.Create("bulge.dxf", new[] { polyline })
        .Contours.Should().ContainSingle().Subject;

      contour.IsClosed.Should().BeTrue();
      contour.Primitives.Should().HaveCount(4);
      DxfSourcePrimitive arc = contour.Primitives.Should()
        .ContainSingle(item => item.GeometryKind == CommonLineGeometryKind.CircularArc).Subject;
      Math.Abs(arc.SweepRadians).Should().BeApproximately(Math.PI / 2D, 1e-12D);
      arc.Radius.Should().BeApproximately(5D * Math.Sqrt(2D), 1e-12D);
    }

    [Fact]
    public void CircleIsFourExactConnectedQuarterArcs()
    {
      var circle = new DxfCircle(new DxfPoint(3D, 4D, 0D), 7D);

      DxfSourceContour contour = DxfContourCatalog.Create("circle.dxf", new[] { circle })
        .Contours.Should().ContainSingle().Subject;

      contour.IsClosed.Should().BeTrue();
      contour.Primitives.Should().HaveCount(4);
      contour.Primitives.Should().OnlyContain(item =>
        item.GeometryKind == CommonLineGeometryKind.CircularArc
        && Math.Abs(item.Radius - 7D) <= 1e-12D
        && Math.Abs(item.SweepRadians - (Math.PI / 2D)) <= 1e-12D);
      for (int i = 0; i < contour.Primitives.Count; i++)
      {
        DxfSourcePrimitive current = contour.Primitives[i];
        DxfSourcePrimitive next = contour.Primitives[(i + 1) % contour.Primitives.Count];
        current.End.X.Should().BeApproximately(next.Start.X, 1e-12D);
        current.End.Y.Should().BeApproximately(next.Start.Y, 1e-12D);
      }
    }

    [Fact]
    public void EllipseMetadataIsPreservedWithoutCreatingSampledSeamPrimitives()
    {
      var ellipse = new DxfEllipse(
        new DxfPoint(2D, 3D, 0D),
        new DxfVector(10D, 4D, 0D),
        0.4D)
      {
        StartParameter = 0.25D,
        EndParameter = 1.75D,
      };

      DxfSourcePrimitive primitive = DxfContourCatalog.Create("ellipse.dxf", new[] { ellipse })
        .Contours.Should().ContainSingle().Subject.Primitives.Should().ContainSingle().Subject;

      primitive.GeometryKind.Should().Be(CommonLineGeometryKind.Ellipse);
      primitive.EllipseMetadata.Should().NotBeNull();
      primitive.EllipseMetadata.Center.X.Should().Be(2D);
      primitive.EllipseMetadata.MajorAxisX.Should().Be(10D);
      primitive.EllipseMetadata.MajorAxisY.Should().Be(4D);
      primitive.EllipseMetadata.MinorAxisRatio.Should().Be(0.4D);
      primitive.EllipseMetadata.StartParameter.Should().Be(0.25D);
      primitive.EllipseMetadata.EndParameter.Should().Be(1.75D);
      primitive.SplineMetadata.Should().BeNull();
    }

    [Fact]
    public void SplineMetadataAndCatalogCollectionsAreImmutable()
    {
      var spline = new DxfSpline { DegreeOfCurve = 1, IsClosed = true };
      foreach (DxfPoint point in new[]
      {
        new DxfPoint(0D, 0D, 0D),
        new DxfPoint(10D, 0D, 0D),
        new DxfPoint(10D, 10D, 0D),
        new DxfPoint(0D, 10D, 0D),
        new DxfPoint(0D, 0D, 0D),
      })
      {
        spline.ControlPoints.Add(new DxfControlPoint(point, 1D));
      }

      foreach (double knot in new[] { 0D, 0D, 1D, 2D, 3D, 4D, 4D })
      {
        spline.KnotValues.Add(knot);
      }

      DxfContourCatalog catalog = DxfContourCatalog.Create("spline.dxf", new[] { spline });
      DxfSourcePrimitive primitive = catalog.Contours.Should()
        .ContainSingle().Subject.Primitives.Should().ContainSingle().Subject;

      primitive.GeometryKind.Should().Be(CommonLineGeometryKind.Spline);
      primitive.SplineMetadata.Should().NotBeNull();
      primitive.SplineMetadata.DegreeOfCurve.Should().Be(1);
      primitive.SplineMetadata.IsClosed.Should().BeTrue();
      primitive.SplineMetadata.KnotValues.Should().Equal(0D, 0D, 1D, 2D, 3D, 4D, 4D);
      primitive.SplineMetadata.ControlPoints.Should().HaveCount(5);
      Action mutateContours = () => ((IList<DxfSourceContour>)catalog.Contours).Add(catalog.Contours[0]);
      Action mutateKnots = () => ((IList<double>)primitive.SplineMetadata.KnotValues).Add(5D);
      mutateContours.Should().Throw<NotSupportedException>();
      mutateKnots.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void ElevatedGeometryIsRejectedBeforeACatalogFingerprintCanBeProduced()
    {
      var elevated = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 0D },
        new DxfLwPolylineVertex { X = 10D, Y = 10D },
        new DxfLwPolylineVertex { X = 0D, Y = 10D },
      })
      {
        IsClosed = true,
        Elevation = 0.25D,
      };

      Action create = () => DxfContourCatalog.Create("elevated.dxf", new[] { elevated });

      create.Should().Throw<InvalidDataException>()
        .WithMessage("*world XY plane*");
    }

    [Fact]
    public void ReversedArcOcsIsRejectedInsteadOfBeingSilentlyMirrored()
    {
      var arc = new DxfArc(new DxfPoint(5D, 0D, 0D), 5D, 0D, 180D)
      {
        Normal = new DxfVector(0D, 0D, -1D),
      };

      Action create = () => DxfContourCatalog.Create("reversed-ocs.dxf", new[] { arc });

      create.Should().Throw<InvalidDataException>()
        .WithMessage("*non-default OCS geometry*");
    }

    [Fact]
    public void NonZeroLineZIsRejectedInsteadOfBeingDroppedFromTheFingerprint()
    {
      var line = new DxfLine(
        new DxfPoint(0D, 0D, 0D),
        new DxfPoint(10D, 0D, 0.01D));

      Action create = () => DxfContourCatalog.Create("line-z.dxf", new[] { line });

      create.Should().Throw<InvalidDataException>()
        .WithMessage("*LINE end point Z*world XY plane*");
    }

    [Fact]
    public void LoadUsesPhysicallyEquivalentEndpointToleranceForInchesAndMillimeters()
    {
      string inchPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
      string metricPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
      try
      {
        SaveDShape(
          inchPath,
          scale: 1D,
          endpointGap: CommonLineDefaults.DefaultGeometryToleranceInches / 2D,
          DxfUnits.Inches,
          DxfDrawingUnits.English);
        SaveDShape(
          metricPath,
          scale: CommonLineDefaults.InchesToMillimeters,
          endpointGap: CommonLineDefaults.DefaultGeometryToleranceMillimeters / 2D,
          DxfUnits.Millimeters,
          DxfDrawingUnits.Metric);

        DxfSourceContour inch = DxfContourCatalog.Load(inchPath)
          .Contours.Should().ContainSingle().Subject;
        DxfSourceContour metric = DxfContourCatalog.Load(metricPath)
          .Contours.Should().ContainSingle().Subject;

        inch.IsClosed.Should().BeTrue();
        metric.IsClosed.Should().BeTrue();
        inch.Primitives.Should().HaveCount(2);
        metric.Primitives.Should().HaveCount(2);
      }
      finally
      {
        File.Delete(inchPath);
        File.Delete(metricPath);
      }
    }

    [Fact]
    public void LoadUsesActiveMetricToleranceForUnitlessDxfAndCachesItSeparately()
    {
      string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
      try
      {
        SaveDShape(
          path,
          scale: CommonLineDefaults.InchesToMillimeters,
          endpointGap: CommonLineDefaults.DefaultGeometryToleranceMillimeters / 2D,
          DxfUnits.Unitless,
          DxfDrawingUnits.English);

        // The unitless header deliberately carries the wrong coarse hint. The active metric project
        // tolerance, not $MEASUREMENT, is authoritative for joining source contour endpoints.
        DxfContourCatalog strict = DxfContourCatalog.Load(
          path,
          CommonLineDefaults.DefaultGeometryToleranceInches);
        DxfContourCatalog metric = DxfContourCatalog.Load(
          path,
          CommonLineDefaults.DefaultGeometryToleranceMillimeters);

        strict.Should().NotBeSameAs(metric);
        strict.Contours.Should().ContainSingle().Subject.IsClosed.Should().BeFalse();
        DxfSourceContour joined = metric.Contours.Should().ContainSingle().Subject;
        joined.IsClosed.Should().BeTrue();
        joined.Primitives.Should().HaveCount(2);

        DxfContourCatalog.Load(
          path,
          CommonLineDefaults.DefaultGeometryToleranceInches).Should().BeSameAs(strict);
        DxfContourCatalog.Load(
          path,
          CommonLineDefaults.DefaultGeometryToleranceMillimeters).Should().BeSameAs(metric);
      }
      finally
      {
        File.Delete(path);
      }
    }

    [Fact]
    public void LoadInvalidatesCacheWhenContentChangesButLengthAndTimestampDoNot()
    {
      string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".dxf");
      try
      {
        byte[] firstBytes = CircleDxfBytes(7D);
        byte[] secondBytes = CircleDxfBytes(8D);
        int equalLength = Math.Max(firstBytes.Length, secondBytes.Length);
        firstBytes = PadTextDxf(firstBytes, equalLength);
        secondBytes = PadTextDxf(secondBytes, equalLength);
        firstBytes.Length.Should().Be(secondBytes.Length);

        File.WriteAllBytes(path, firstBytes);
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        DateTime unchangedTimestamp = File.GetLastWriteTimeUtc(path);
        DxfContourCatalog first = DxfContourCatalog.Load(path);

        File.WriteAllBytes(path, secondBytes);
        File.SetLastWriteTimeUtc(path, unchangedTimestamp);
        new FileInfo(path).Length.Should().Be(firstBytes.Length);
        File.GetLastWriteTimeUtc(path).Should().Be(unchangedTimestamp);
        DxfContourCatalog second = DxfContourCatalog.Load(path);

        second.Should().NotBeSameAs(first);
        second.SourceFingerprint.Should().NotBe(first.SourceFingerprint);
        second.Contours.Should().ContainSingle().Subject.Primitives
          .Should().OnlyContain(primitive => Math.Abs(primitive.Radius - 8D) <= 1e-12D);
      }
      finally
      {
        File.Delete(path);
      }
    }

    [Fact]
    public void ArcAndCircleRejectNonFiniteOrNonPositiveGeometry()
    {
      var nonFiniteArc = new DxfArc(
        new DxfPoint(5D, 0D, 0D),
        5D,
        double.MaxValue,
        -double.MaxValue);
      var nonFiniteCircle = new DxfCircle(new DxfPoint(0D, 0D, 0D), double.NaN);
      var nonPositiveCircle = new DxfCircle(new DxfPoint(0D, 0D, 0D), 0D);

      Action createArc = () => DxfContourCatalog.Create("invalid-arc.dxf", new[] { nonFiniteArc });
      Action createNonFiniteCircle = () => DxfContourCatalog.Create(
        "non-finite-circle.dxf",
        new[] { nonFiniteCircle });
      Action createNonPositiveCircle = () => DxfContourCatalog.Create(
        "non-positive-circle.dxf",
        new[] { nonPositiveCircle });

      createArc.Should().Throw<InvalidDataException>().WithMessage("*ARC angular sweep*finite*");
      createNonFiniteCircle.Should().Throw<InvalidDataException>().WithMessage("*CIRCLE radius*finite*");
      createNonPositiveCircle.Should().Throw<InvalidDataException>().WithMessage("*CIRCLE radius*positive*");
    }

    [Fact]
    public void PolylineBulgesMustBeFinite()
    {
      var lightweight = new DxfLwPolyline(new[]
      {
        new DxfLwPolylineVertex { X = 0D, Y = 0D, Bulge = double.NaN },
        new DxfLwPolylineVertex { X = 10D, Y = 0D },
        new DxfLwPolylineVertex { X = 0D, Y = 10D },
      })
      { IsClosed = true };
      var legacy = new DxfPolyline(new[]
      {
        new DxfVertex(new DxfPoint(0D, 0D, 0D)) { Bulge = double.PositiveInfinity },
        new DxfVertex(new DxfPoint(10D, 0D, 0D)),
        new DxfVertex(new DxfPoint(0D, 10D, 0D)),
      })
      { IsClosed = true };

      Action createLightweight = () => DxfContourCatalog.Create(
        "invalid-lw-bulge.dxf",
        new DxfEntity[] { lightweight });
      Action createLegacy = () => DxfContourCatalog.Create(
        "invalid-poly-bulge.dxf",
        new DxfEntity[] { legacy });

      createLightweight.Should().Throw<InvalidDataException>()
        .WithMessage("*LWPOLYLINE vertex bulge*finite*");
      createLegacy.Should().Throw<InvalidDataException>()
        .WithMessage("*POLYLINE vertex bulge*finite*");
    }

    [Fact]
    public void EllipseRejectsUnsafeParametersAndAxisRanges()
    {
      var nonFiniteParameter = new DxfEllipse(
        new DxfPoint(0D, 0D, 0D),
        new DxfVector(10D, 0D, 0D),
        0.5D)
      {
        StartParameter = double.MaxValue,
        EndParameter = -double.MaxValue,
      };
      var invalidRatio = new DxfEllipse(
        new DxfPoint(0D, 0D, 0D),
        new DxfVector(10D, 0D, 0D),
        double.NaN);
      var zeroMajorAxis = new DxfEllipse(
        new DxfPoint(0D, 0D, 0D),
        new DxfVector(0D, 0D, 0D),
        0.5D);

      Action createParameter = () => DxfContourCatalog.Create(
        "invalid-ellipse-parameters.dxf",
        new DxfEntity[] { nonFiniteParameter });
      Action createRatio = () => DxfContourCatalog.Create(
        "invalid-ellipse-ratio.dxf",
        new DxfEntity[] { invalidRatio });
      Action createAxis = () => DxfContourCatalog.Create(
        "invalid-ellipse-axis.dxf",
        new DxfEntity[] { zeroMajorAxis });

      createParameter.Should().Throw<InvalidDataException>()
        .WithMessage("*ELLIPSE parameter sweep*finite*");
      createRatio.Should().Throw<InvalidDataException>()
        .WithMessage("*ELLIPSE minor-axis ratio*finite*");
      createAxis.Should().Throw<InvalidDataException>()
        .WithMessage("*ELLIPSE major axis*positive length*");
    }

    [Fact]
    public void SplineKnotsAndWeightsMustBeFinite()
    {
      var nonFiniteWeight = new DxfSpline { DegreeOfCurve = 1, IsClosed = true };
      nonFiniteWeight.ControlPoints.Add(new DxfControlPoint(new DxfPoint(0D, 0D, 0D), double.NaN));
      nonFiniteWeight.ControlPoints.Add(new DxfControlPoint(new DxfPoint(1D, 0D, 0D), 1D));
      nonFiniteWeight.ControlPoints.Add(new DxfControlPoint(new DxfPoint(0D, 0D, 0D), 1D));
      nonFiniteWeight.KnotValues.Add(0D);
      nonFiniteWeight.KnotValues.Add(0D);
      nonFiniteWeight.KnotValues.Add(1D);
      nonFiniteWeight.KnotValues.Add(2D);
      nonFiniteWeight.KnotValues.Add(2D);

      var nonFiniteKnot = new DxfSpline { DegreeOfCurve = 1, IsClosed = true };
      nonFiniteKnot.ControlPoints.Add(new DxfControlPoint(new DxfPoint(0D, 0D, 0D), 1D));
      nonFiniteKnot.ControlPoints.Add(new DxfControlPoint(new DxfPoint(1D, 0D, 0D), 1D));
      nonFiniteKnot.ControlPoints.Add(new DxfControlPoint(new DxfPoint(0D, 0D, 0D), 1D));
      nonFiniteKnot.KnotValues.Add(0D);
      nonFiniteKnot.KnotValues.Add(0D);
      nonFiniteKnot.KnotValues.Add(double.PositiveInfinity);
      nonFiniteKnot.KnotValues.Add(2D);
      nonFiniteKnot.KnotValues.Add(2D);

      Action createWeight = () => DxfContourCatalog.Create(
        "invalid-spline-weight.dxf",
        new DxfEntity[] { nonFiniteWeight });
      Action createKnot = () => DxfContourCatalog.Create(
        "invalid-spline-knot.dxf",
        new DxfEntity[] { nonFiniteKnot });

      createWeight.Should().Throw<InvalidDataException>()
        .WithMessage("*SPLINE control-point weight*finite*");
      createKnot.Should().Throw<InvalidDataException>()
        .WithMessage("*SPLINE knot*finite*");
    }

    private static void SaveDShape(
      string path,
      double scale,
      double endpointGap,
      DxfUnits units,
      DxfDrawingUnits drawingUnits)
    {
      var file = new DxfFile();
      file.Header.Version = DxfAcadVersion.R2000;
      file.Header.DefaultDrawingUnits = units;
      file.Header.DrawingUnits = drawingUnits;
      file.Entities.Add(new DxfLine(
        new DxfPoint(0D, 0D, 0D),
        new DxfPoint((10D * scale) + endpointGap, 0D, 0D)));
      file.Entities.Add(new DxfArc(
        new DxfPoint(5D * scale, 0D, 0D),
        5D * scale,
        0D,
        180D));
      file.Save(path, true);
    }

    private static byte[] CircleDxfBytes(double radius)
    {
      var file = new DxfFile();
      file.Header.Version = DxfAcadVersion.R2000;
      file.Header.DefaultDrawingUnits = DxfUnits.Inches;
      file.Entities.Add(new DxfCircle(new DxfPoint(3D, 4D, 0D), radius));
      using var stream = new MemoryStream();
      file.Save(stream, true);
      return stream.ToArray();
    }

    private static byte[] PadTextDxf(byte[] source, int length)
    {
      if (source.Length > length)
      {
        throw new ArgumentOutOfRangeException(nameof(length));
      }

      // Raw bytes after EOF form another (invalid) DXF code line. Put deterministic padding in a legal
      // group-999 comment instead, immediately before EOF, so both revisions remain parseable even when
      // other tests have advanced IxMilia's generated handle widths.
      byte[] eof = { 32, 32, 48, 13, 10, 69, 79, 70, 13, 10 }; // "  0\r\nEOF\r\n"
      byte[] prefix = { 57, 57, 57, 13, 10, 67, 65, 67, 72, 69, 95, 80, 65, 68, 68, 73, 78, 71, 58 };
      byte[] suffix = { 13, 10 };
      int eofIndex = -1;
      for (int i = source.Length - eof.Length; i >= 0 && eofIndex < 0; i--)
      {
        bool match = true;
        for (int j = 0; j < eof.Length; j++)
        {
          if (source[i + j] != eof[j])
          {
            match = false;
            break;
          }
        }

        if (match)
        {
          eofIndex = i;
        }
      }

      eofIndex.Should().BeGreaterThanOrEqualTo(0);
      int padding = length - source.Length;
      var result = new byte[source.Length + prefix.Length + padding + suffix.Length];
      Array.Copy(source, 0, result, 0, eofIndex);
      Array.Copy(prefix, 0, result, eofIndex, prefix.Length);
      for (int i = 0; i < padding; i++)
      {
        result[eofIndex + prefix.Length + i] = (byte)'X';
      }

      int suffixIndex = eofIndex + prefix.Length + padding;
      Array.Copy(suffix, 0, result, suffixIndex, suffix.Length);
      Array.Copy(
        source,
        eofIndex,
        result,
        suffixIndex + suffix.Length,
        source.Length - eofIndex);
      return result;
    }
  }
}
