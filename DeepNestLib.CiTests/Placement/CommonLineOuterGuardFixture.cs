namespace DeepNestLib.CiTests.Placement
{
  using System;
  using System.Collections.Generic;
  using DeepNestLib.Placement;
  using FluentAssertions;
  using Xunit;

  public class CommonLineOuterGuardFixture
  {
    private const double Tolerance = 0.0001D;

    [Fact]
    public void PartialLineOverlapWithCutOuterIsRejected()
    {
      CommonCenterCurve common = Line(2D, 0D, 8D, 0D);

      Action action = () => CommonLineGeometryResolver.ValidateCommonCurvesAgainstOuter(
        new[] { common },
        new[] { Rectangle(0D, 0D, 10D, 10D) },
        Tolerance);

      action.Should().Throw<CommonLineValidationException>()
        .WithMessage("*overlaps or crosses CUT_OUTER*");
    }

    [Fact]
    public void LinesCrossingCutOuterAwayFromEndpointsAreRejected()
    {
      CommonCenterCurve common = Line(-2D, 5D, 12D, 5D);

      Action action = () => CommonLineGeometryResolver.ValidateCommonCurvesAgainstOuter(
        new[] { common },
        new[] { Rectangle(0D, 0D, 10D, 10D) },
        Tolerance);

      action.Should().Throw<CommonLineValidationException>()
        .WithMessage("*overlaps or crosses CUT_OUTER*");
    }

    [Fact]
    public void LineCrossingCircularCutOuterAwayFromEndpointsIsRejected()
    {
      CommonCenterCurve common = Line(0D, 4D, 0D, 6D);

      Action action = () => CommonLineGeometryResolver.ValidateCommonCurvesAgainstOuter(
        new[] { common },
        new[] { UpperArcBoundary() },
        Tolerance);

      action.Should().Throw<CommonLineValidationException>()
        .WithMessage("*overlaps or crosses CUT_OUTER*");
    }

    [Fact]
    public void CoincidentPartialArcOverlapWithCutOuterIsRejected()
    {
      CommonCenterCurve common = Arc(
        new CommonLinePoint(0D, 0D),
        5D,
        Math.PI / 4D,
        Math.PI / 2D);

      Action action = () => CommonLineGeometryResolver.ValidateCommonCurvesAgainstOuter(
        new[] { common },
        new[] { UpperArcBoundary() },
        Tolerance);

      action.Should().Throw<CommonLineValidationException>()
        .WithMessage("*overlaps or crosses CUT_OUTER*");
    }

    [Fact]
    public void IntersectingCircularArcsAwayFromEndpointsAreRejected()
    {
      CommonCenterCurve common = Arc(
        new CommonLinePoint(4D, 0D),
        5D,
        0D,
        2D * Math.PI);

      Action action = () => CommonLineGeometryResolver.ValidateCommonCurvesAgainstOuter(
        new[] { common },
        new[] { CircleBoundary(new CommonLinePoint(0D, 0D), 5D) },
        Tolerance);

      action.Should().Throw<CommonLineValidationException>()
        .WithMessage("*overlaps or crosses CUT_OUTER*");
    }

    [Fact]
    public void SharedLineEndpointsAreTheOnlyAllowedOuterContact()
    {
      CommonCenterCurve common = Line(-5D, 0D, 0D, 0D);

      Action action = () => CommonLineGeometryResolver.ValidateCommonCurvesAgainstOuter(
        new[] { common },
        new[] { Rectangle(0D, 0D, 10D, 10D) },
        Tolerance);

      action.Should().NotThrow();
    }

    [Fact]
    public void SharedLineAndArcEndpointsAreAllowed()
    {
      CommonCenterCurve common = Line(-8D, 0D, -5D, 0D);

      Action action = () => CommonLineGeometryResolver.ValidateCommonCurvesAgainstOuter(
        new[] { common },
        new[] { UpperArcBoundary() },
        Tolerance);

      action.Should().NotThrow();
    }

    [Fact]
    public void SharedArcEndpointsAreAllowed()
    {
      CommonCenterCurve common = Arc(
        new CommonLinePoint(10D, 0D),
        5D,
        Math.PI,
        -Math.PI / 2D);

      Action action = () => CommonLineGeometryResolver.ValidateCommonCurvesAgainstOuter(
        new[] { common },
        new[] { CircleBoundary(new CommonLinePoint(0D, 0D), 5D) },
        Tolerance);

      action.Should().NotThrow();
    }

    [Theory]
    [InlineData(0.0001D, 0.000049D, 0.000051D)]
    [InlineData(0.05D, 0.049D, 0.000051D)]
    [InlineData(1D, 0.5D, 1.5D)]
    public void AdjacentQuantizationCellsCanonicalizeByRealDistance(
      double tolerance,
      double firstX,
      double secondX)
    {
      var quantizer = new CommonLineGeometryResolver.PointQuantizer(tolerance);

      CommonLinePoint first = quantizer.Canonical(new CommonLinePoint(firstX, 2D));
      CommonLinePoint second = quantizer.Canonical(new CommonLinePoint(secondX, 2D));

      second.Should().BeSameAs(first);
    }

    [Fact]
    public void SameQuantizationCellDoesNotMergePointsFartherThanTolerance()
    {
      var quantizer = new CommonLineGeometryResolver.PointQuantizer(Tolerance);
      double delta = 0.49D * Tolerance;

      CommonLinePoint first = quantizer.Canonical(new CommonLinePoint(delta, delta));
      CommonLinePoint second = quantizer.Canonical(new CommonLinePoint(-delta, -delta));

      second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void EquidistantCanonicalNeighborSelectionIsDeterministic()
    {
      var forward = new CommonLineGeometryResolver.PointQuantizer(Tolerance);
      forward.Canonical(new CommonLinePoint(0D, 0D));
      forward.Canonical(new CommonLinePoint(0.00018D, 0D));
      CommonLinePoint forwardChoice = forward.Canonical(new CommonLinePoint(0.00009D, 0D));

      var reverse = new CommonLineGeometryResolver.PointQuantizer(Tolerance);
      reverse.Canonical(new CommonLinePoint(0.00018D, 0D));
      reverse.Canonical(new CommonLinePoint(0D, 0D));
      CommonLinePoint reverseChoice = reverse.Canonical(new CommonLinePoint(0.00009D, 0D));

      reverseChoice.X.Should().Be(forwardChoice.X);
      reverseChoice.Y.Should().Be(forwardChoice.Y);
    }

    private static CommonCenterCurve Line(double x1, double y1, double x2, double y2)
      => new CommonCenterCurve(
        CommonLineGeometryKind.Line,
        new CommonLinePoint(x1, y1),
        new CommonLinePoint(x2, y2));

    private static CommonCenterCurve Arc(
      CommonLinePoint center,
      double radius,
      double startAngle,
      double sweep)
      => new CommonCenterCurve(
        CommonLineGeometryKind.CircularArc,
        new CommonLinePoint(
          center.X + (radius * Math.Cos(startAngle)),
          center.Y + (radius * Math.Sin(startAngle))),
        new CommonLinePoint(
          center.X + (radius * Math.Cos(startAngle + sweep)),
          center.Y + (radius * Math.Sin(startAngle + sweep))),
        center,
        radius,
        startAngle,
        sweep);

    private static CommonLinePath Rectangle(double x, double y, double width, double height)
      => new CommonLinePath(
        new[]
        {
          new CommonLinePoint(x, y),
          new CommonLinePoint(x + width, y),
          new CommonLinePoint(x + width, y + height),
          new CommonLinePoint(x, y + height),
        },
        isClosed: true);

    private static CommonLinePath UpperArcBoundary()
      => new CommonLinePath(
        new[]
        {
          new CommonLinePoint(-5D, 0D),
          new CommonLinePoint(5D, 0D),
          new CommonLinePoint(5D, -10D),
          new CommonLinePoint(-5D, -10D),
        },
        isClosed: true,
        bulges: new[] { -1D, 0D, 0D, 0D });

    private static CommonLinePath CircleBoundary(CommonLinePoint center, double radius)
    {
      double quarterBulge = Math.Tan(Math.PI / 8D);
      IReadOnlyList<CommonLinePoint> points = new[]
      {
        new CommonLinePoint(center.X + radius, center.Y),
        new CommonLinePoint(center.X, center.Y + radius),
        new CommonLinePoint(center.X - radius, center.Y),
        new CommonLinePoint(center.X, center.Y - radius),
      };
      return new CommonLinePath(
        points,
        isClosed: true,
        bulges: new[] { quarterBulge, quarterBulge, quarterBulge, quarterBulge });
    }
  }
}
