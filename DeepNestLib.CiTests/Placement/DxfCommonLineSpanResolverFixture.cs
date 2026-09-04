namespace DeepNestLib.CiTests.Placement
{
  using System;
  using System.Linq;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;
  using Xunit;

  public class DxfCommonLineSpanResolverFixture
  {
    private const double Gap = 0.01D;
    private const double Tolerance = 0.0001D;

    [Fact]
    public void PartialLineUsesMirrorThenRotationThenTranslationAndKeepsOutwardNormal()
    {
      DxfContourCatalog catalog = RectangleCatalog(8D, 4D);
      DxfSourceContour contour = catalog.Contours.Single();
      DxfSourcePrimitive source = contour.Primitives
        .First(primitive => primitive.GeometryKind == CommonLineGeometryKind.Line);
      CommonBoundarySpan span = DxfCommonLineSpanResolver.CreateSpan(
        catalog,
        contour.Index,
        source.Index,
        0.25D,
        0.75D);
      var placement = Placement(rotation: 90D, x: 7D, y: -4D, mirrored: true);

      DxfResolvedBoundarySpan resolved = DxfCommonLineSpanResolver.Resolve(
        catalog,
        span,
        placement);

      CommonLinePoint expectedStart = Transform(
        placement,
        Interpolate(source.Start, source.End, 0.25D));
      CommonLinePoint expectedEnd = Transform(
        placement,
        Interpolate(source.Start, source.End, 0.75D));
      AssertPoint(resolved.Start, expectedStart);
      AssertPoint(resolved.End, expectedEnd);
      resolved.Length.Should().BeApproximately(Distance(expectedStart, expectedEnd), 1e-12D);
      Dot(resolved.TangentAtMidpoint, resolved.OutwardNormalAtMidpoint)
        .Should().BeApproximately(0D, 1e-12D);
      VectorLength(resolved.OutwardNormalAtMidpoint).Should().BeApproximately(1D, 1e-12D);
    }

    [Fact]
    public void MirrorReversesAnExactArcSweepWithoutChangingRadius()
    {
      DxfContourCatalog catalog = LowerHalfDiskCatalog(5D);
      DxfSourceContour contour = catalog.Contours.Single();
      DxfSourcePrimitive source = contour.Primitives.Single(primitive =>
        primitive.GeometryKind == CommonLineGeometryKind.CircularArc);
      CommonBoundarySpan span = DxfCommonLineSpanResolver.CreateSpan(
        catalog,
        contour.Index,
        source.Index,
        0.2D,
        0.8D);
      var placement = Placement(rotation: 37D, x: 11D, y: -3D, mirrored: true);

      DxfResolvedBoundarySpan resolved = DxfCommonLineSpanResolver.Resolve(
        catalog,
        span,
        placement);

      resolved.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      resolved.Radius.Should().BeApproximately(source.Radius, 1e-12D);
      resolved.SweepRadians.Should().BeApproximately(
        -source.SweepRadians * 0.6D,
        1e-12D);
      AssertPoint(resolved.Center, Transform(placement, source.Center));
      AssertPoint(resolved.Start, resolved.PointAt(0D));
      AssertPoint(resolved.End, resolved.PointAt(1D));
    }

    [Fact]
    public void OppositeConcentricArcsProduceOneExactMidRadiusCurve()
    {
      DxfContourCatalog innerCatalog = LowerHalfDiskCatalog(5D);
      DxfContourCatalog outerCatalog = RegionAboveConcaveArcCatalog(5D + Gap, 10D);
      DxfSourceContour innerContour = innerCatalog.Contours.Single();
      DxfSourceContour outerContour = outerCatalog.Contours.Single();
      DxfSourcePrimitive innerArc = innerContour.Primitives.Single(primitive =>
        primitive.GeometryKind == CommonLineGeometryKind.CircularArc);
      DxfSourcePrimitive outerArc = outerContour.Primitives.Single(primitive =>
        primitive.GeometryKind == CommonLineGeometryKind.CircularArc);
      CommonBoundarySpan innerSpan = DxfCommonLineSpanResolver.CreateSpan(
        innerCatalog,
        innerContour.Index,
        innerArc.Index,
        0D,
        1D);
      CommonBoundarySpan outerSpan = DxfCommonLineSpanResolver.CreateSpan(
        outerCatalog,
        outerContour.Index,
        outerArc.Index,
        0D,
        1D);

      CommonCenterCurve center = DxfCommonLineSpanResolver.CreateCenterCurve(
        innerCatalog,
        innerSpan,
        Placement(),
        outerCatalog,
        outerSpan,
        Placement(),
        Gap,
        Tolerance);

      center.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      center.Center.X.Should().BeApproximately(0D, Tolerance);
      center.Center.Y.Should().BeApproximately(0D, Tolerance);
      center.Radius.Should().BeApproximately(5.005D, Tolerance);
      Math.Abs(center.SweepRadians).Should().BeApproximately(Math.PI, 1e-12D);
      Distance(center.Start, center.Center).Should().BeApproximately(5.005D, Tolerance);
      Distance(center.End, center.Center).Should().BeApproximately(5.005D, Tolerance);
    }

    [Fact]
    public void ExactlyOneMirroredCompatibleArcUsesSameParameterCorrespondence()
    {
      DxfContourCatalog innerCatalog = LowerHalfDiskCatalog(5D);
      DxfContourCatalog outerCatalog = RegionAboveConcaveArcCatalog(5D + Gap, 10D);
      DxfSourceContour innerContour = innerCatalog.Contours.Single();
      DxfSourceContour outerContour = outerCatalog.Contours.Single();
      DxfSourcePrimitive innerArc = innerContour.Primitives.Single(primitive =>
        primitive.GeometryKind == CommonLineGeometryKind.CircularArc);
      DxfSourcePrimitive outerArc = outerContour.Primitives.Single(primitive =>
        primitive.GeometryKind == CommonLineGeometryKind.CircularArc);
      CommonBoundarySpan innerSpan = DxfCommonLineSpanResolver.CreateSpan(
        innerCatalog,
        innerContour.Index,
        innerArc.Index);
      CommonBoundarySpan outerSpan = DxfCommonLineSpanResolver.CreateSpan(
        outerCatalog,
        outerContour.Index,
        outerArc.Index);
      DxfResolvedBoundarySpan mirroredInner = DxfCommonLineSpanResolver.Resolve(
        innerCatalog,
        innerSpan,
        Placement(mirrored: true));
      DxfResolvedBoundarySpan outer = DxfCommonLineSpanResolver.Resolve(
        outerCatalog,
        outerSpan,
        Placement());

      Math.Sign(mirroredInner.SweepRadians).Should().Be(Math.Sign(outer.SweepRadians),
        "one mirror reverses the parameter winding without changing physical compatibility");
      CommonCenterCurve center = DxfCommonLineSpanResolver.CreateCenterCurve(
        mirroredInner,
        outer,
        Gap,
        Tolerance);

      center.GeometryKind.Should().Be(CommonLineGeometryKind.CircularArc);
      center.Radius.Should().BeApproximately(5D + (Gap / 2D), Tolerance);
      center.SweepRadians.Should().BeApproximately(mirroredInner.SweepRadians, 1e-12D);
      Distance(center.Start, center.Center).Should().BeApproximately(5.005D, Tolerance);
      Distance(center.End, center.Center).Should().BeApproximately(5.005D, Tolerance);
    }

    [Fact]
    public void ExactlyOneMirroredSameFacingArcStillFailsClosed()
    {
      DxfContourCatalog firstCatalog = LowerHalfDiskCatalog(5D);
      DxfContourCatalog secondCatalog = LowerHalfDiskCatalog(5D + Gap);
      DxfSourceContour firstContour = firstCatalog.Contours.Single();
      DxfSourceContour secondContour = secondCatalog.Contours.Single();
      CommonBoundarySpan firstSpan = DxfCommonLineSpanResolver.CreateSpan(
        firstCatalog,
        firstContour.Index,
        firstContour.Primitives.Single(primitive =>
          primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index);
      CommonBoundarySpan secondSpan = DxfCommonLineSpanResolver.CreateSpan(
        secondCatalog,
        secondContour.Index,
        secondContour.Primitives.Single(primitive =>
          primitive.GeometryKind == CommonLineGeometryKind.CircularArc).Index);
      DxfResolvedBoundarySpan mirroredFirst = DxfCommonLineSpanResolver.Resolve(
        firstCatalog,
        firstSpan,
        Placement(mirrored: true));
      DxfResolvedBoundarySpan second = DxfCommonLineSpanResolver.Resolve(
        secondCatalog,
        secondSpan,
        Placement());

      bool success = DxfCommonLineSpanResolver.TryCreateCenterCurve(
        mirroredFirst,
        second,
        Gap,
        Tolerance,
        out CommonCenterCurve curve,
        out string error);

      success.Should().BeFalse();
      curve.Should().BeNull();
      error.Should().Contain("opposite");
    }

    [Fact]
    public void SameFacingArcsAreRejectedRatherThanSampledOrMerged()
    {
      DxfContourCatalog firstCatalog = LowerHalfDiskCatalog(5D);
      DxfContourCatalog secondCatalog = LowerHalfDiskCatalog(5D + Gap);
      DxfResolvedBoundarySpan first = ResolveOnlyArc(firstCatalog);
      DxfResolvedBoundarySpan second = ResolveOnlyArc(secondCatalog);

      bool success = DxfCommonLineSpanResolver.TryCreateCenterCurve(
        first,
        second,
        Gap,
        Tolerance,
        out CommonCenterCurve curve,
        out string error);

      success.Should().BeFalse();
      curve.Should().BeNull();
      error.Should().Contain("opposite");
    }

    [Fact]
    public void FingerprintsAreMandatoryAndStaleSourceIsRejected()
    {
      DxfContourCatalog catalog = RectangleCatalog(8D, 4D);
      DxfSourceContour contour = catalog.Contours.Single();
      DxfSourcePrimitive source = contour.Primitives.First();
      var missing = new CommonBoundarySpan(contour.Index, source.Index, 0D, 1D);
      var stale = new CommonBoundarySpan(
        contour.Index,
        source.Index,
        0D,
        1D,
        "STALE-SOURCE",
        contour.Fingerprint);

      Action resolveMissing = () => DxfCommonLineSpanResolver.Resolve(catalog, missing, Placement());
      Action resolveStale = () => DxfCommonLineSpanResolver.Resolve(catalog, stale, Placement());

      resolveMissing.Should().Throw<CommonLineValidationException>()
        .WithMessage("*fingerprint*missing or stale*");
      resolveStale.Should().Throw<CommonLineValidationException>()
        .WithMessage("*fingerprint*missing or stale*");
    }

    [Fact]
    public void EllipseCannotBecomeAnApproximateCommonBoundarySpan()
    {
      var ellipse = new DxfEllipse(
        new DxfPoint(0D, 0D, 0D),
        new DxfVector(5D, 0D, 0D),
        0.5D)
      {
        StartParameter = 0D,
        EndParameter = 2D * Math.PI,
      };
      DxfContourCatalog catalog = DxfContourCatalog.Create("ellipse.dxf", new[] { ellipse });
      DxfSourceContour contour = catalog.Contours.Single();

      Action create = () => DxfCommonLineSpanResolver.CreateSpan(
        catalog,
        contour.Index,
        contour.Primitives.Single().Index);

      create.Should().Throw<CommonLineValidationException>()
        .WithMessage("*does not support Ellipse*");
    }

    [Theory]
    [InlineData(-0.1D, 0.5D)]
    [InlineData(0.5D, 0.5D)]
    [InlineData(0.7D, 0.6D)]
    [InlineData(0.5D, 1.1D)]
    public void InvalidNormalizedIntervalsFailClosed(double start, double end)
    {
      DxfContourCatalog catalog = RectangleCatalog(8D, 4D);
      DxfSourceContour contour = catalog.Contours.Single();

      Action create = () => DxfCommonLineSpanResolver.CreateSpan(
        catalog,
        contour.Index,
        contour.Primitives.First().Index,
        start,
        end);

      create.Should().Throw<CommonLineValidationException>()
        .WithMessage("*0 <= start < end <= 1*");
    }

    private static DxfResolvedBoundarySpan ResolveOnlyArc(DxfContourCatalog catalog)
    {
      DxfSourceContour contour = catalog.Contours.Single();
      DxfSourcePrimitive arc = contour.Primitives.Single(primitive =>
        primitive.GeometryKind == CommonLineGeometryKind.CircularArc);
      CommonBoundarySpan span = DxfCommonLineSpanResolver.CreateSpan(
        catalog,
        contour.Index,
        arc.Index);
      return DxfCommonLineSpanResolver.Resolve(catalog, span, Placement());
    }

    private static DxfContourCatalog RectangleCatalog(double width, double height)
      => DxfContourCatalog.Create("rectangle.dxf", new[]
      {
        new DxfLwPolyline(new[]
        {
          new DxfLwPolylineVertex { X = 0D, Y = 0D },
          new DxfLwPolylineVertex { X = width, Y = 0D },
          new DxfLwPolylineVertex { X = width, Y = height },
          new DxfLwPolylineVertex { X = 0D, Y = height },
        })
        { IsClosed = true },
      });

    private static DxfContourCatalog LowerHalfDiskCatalog(double radius)
      => DxfContourCatalog.Create("inner-arc.dxf", new[]
      {
        new DxfLwPolyline(new[]
        {
          new DxfLwPolylineVertex { X = -radius, Y = 0D },
          new DxfLwPolylineVertex { X = radius, Y = 0D, Bulge = 1D },
        })
        { IsClosed = true },
      });

    private static DxfContourCatalog RegionAboveConcaveArcCatalog(double radius, double top)
      => DxfContourCatalog.Create("outer-arc.dxf", new[]
      {
        new DxfLwPolyline(new[]
        {
          new DxfLwPolylineVertex { X = -radius, Y = 0D, Bulge = -1D },
          new DxfLwPolylineVertex { X = radius, Y = 0D },
          new DxfLwPolylineVertex { X = radius, Y = top },
          new DxfLwPolylineVertex { X = -radius, Y = top },
        })
        { IsClosed = true },
      });

    private static PartPlacement Placement(
      double rotation = 0D,
      double x = 0D,
      double y = 0D,
      bool mirrored = false)
      => new PartPlacement(new NoFitPolygon())
      {
        Rotation = rotation,
        X = x,
        Y = y,
        IsMirrored = mirrored,
      };

    private static CommonLinePoint Transform(IPartPlacement placement, CommonLinePoint point)
    {
      double x = placement.IsMirrored ? -point.X : point.X;
      double radians = placement.Rotation * Math.PI / 180D;
      return new CommonLinePoint(
        (x * Math.Cos(radians)) - (point.Y * Math.Sin(radians)) + placement.X,
        (x * Math.Sin(radians)) + (point.Y * Math.Cos(radians)) + placement.Y);
    }

    private static CommonLinePoint Interpolate(
      CommonLinePoint start,
      CommonLinePoint end,
      double parameter)
      => new CommonLinePoint(
        start.X + ((end.X - start.X) * parameter),
        start.Y + ((end.Y - start.Y) * parameter));

    private static void AssertPoint(CommonLinePoint actual, CommonLinePoint expected)
    {
      actual.X.Should().BeApproximately(expected.X, 1e-10D);
      actual.Y.Should().BeApproximately(expected.Y, 1e-10D);
    }

    private static double Dot(CommonLinePoint first, CommonLinePoint second)
      => (first.X * second.X) + (first.Y * second.Y);

    private static double VectorLength(CommonLinePoint point)
      => Math.Sqrt((point.X * point.X) + (point.Y * point.Y));

    private static double Distance(CommonLinePoint first, CommonLinePoint second)
    {
      double dx = second.X - first.X;
      double dy = second.Y - first.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }
  }
}
