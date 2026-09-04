namespace DeepNestLib.Placement
{
  using System;
  using DeepNestLib.IO;

  /// <summary>
  /// An exact source-boundary interval after the placement's mirror, rotation, and translation have
  /// been applied. Only analytic LINE and circular-arc spans can reach this type.
  /// </summary>
  public sealed class DxfResolvedBoundarySpan
  {
    private readonly int worldOrientation;

    internal DxfResolvedBoundarySpan(
      CommonBoundarySpan sourceSpan,
      CommonLineGeometryKind geometryKind,
      CommonLinePoint start,
      CommonLinePoint end,
      CommonLinePoint center,
      double radius,
      double startAngleRadians,
      double sweepRadians,
      int worldOrientation)
    {
      this.SourceSpan = sourceSpan;
      this.GeometryKind = geometryKind;
      this.Start = start;
      this.End = end;
      this.Center = center;
      this.Radius = radius;
      this.StartAngleRadians = startAngleRadians;
      this.SweepRadians = sweepRadians;
      this.worldOrientation = worldOrientation;
    }

    public CommonBoundarySpan SourceSpan { get; }

    public CommonLineGeometryKind GeometryKind { get; }

    public CommonLinePoint Start { get; }

    public CommonLinePoint End { get; }

    public CommonLinePoint Center { get; }

    public double Radius { get; }

    public double StartAngleRadians { get; }

    /// <summary>
    /// Gets the signed world-space sweep. Mirroring changes its sign; rotation and translation do not.
    /// </summary>
    public double SweepRadians { get; }

    public double Length => this.GeometryKind == CommonLineGeometryKind.Line
      ? Distance(this.Start, this.End)
      : Math.Abs(this.SweepRadians) * this.Radius;

    public CommonLinePoint Midpoint => this.PointAt(0.5D);

    public CommonLinePoint TangentAtMidpoint => this.TangentAt(0.5D);

    public CommonLinePoint OutwardNormalAtMidpoint => this.OutwardNormalAt(0.5D);

    /// <summary>Returns an exact point at a normalized parameter on the resolved interval.</summary>
    public CommonLinePoint PointAt(double parameter)
    {
      ValidateUnitParameter(parameter, nameof(parameter));
      if (this.GeometryKind == CommonLineGeometryKind.Line)
      {
        return Interpolate(this.Start, this.End, parameter);
      }

      double angle = this.StartAngleRadians + (this.SweepRadians * parameter);
      return PointOnCircle(this.Center, this.Radius, angle);
    }

    /// <summary>Returns the unit tangent in the interval's directed source-contour order.</summary>
    public CommonLinePoint TangentAt(double parameter)
    {
      ValidateUnitParameter(parameter, nameof(parameter));
      if (this.GeometryKind == CommonLineGeometryKind.Line)
      {
        return Normalize(
          this.End.X - this.Start.X,
          this.End.Y - this.Start.Y,
          "The resolved LINE span has no direction.");
      }

      double angle = this.StartAngleRadians + (this.SweepRadians * parameter);
      double direction = Math.Sign(this.SweepRadians);
      return new CommonLinePoint(
        direction * -Math.Sin(angle),
        direction * Math.Cos(angle));
    }

    /// <summary>
    /// Returns the unit normal pointing out of the placed part. Source outer contours are normalized
    /// counter-clockwise by <see cref="DxfContourCatalog"/>; mirroring reverses that winding.
    /// </summary>
    public CommonLinePoint OutwardNormalAt(double parameter)
    {
      CommonLinePoint tangent = this.TangentAt(parameter);
      return new CommonLinePoint(
        this.worldOrientation * tangent.Y,
        -this.worldOrientation * tangent.X);
    }

    private static void ValidateUnitParameter(double parameter, string parameterName)
    {
      if (!double.IsFinite(parameter) || parameter < 0D || parameter > 1D)
      {
        throw new ArgumentOutOfRangeException(parameterName, "A normalized parameter must be in [0, 1].");
      }
    }

    private static CommonLinePoint Normalize(double x, double y, string error)
    {
      double length = Math.Sqrt((x * x) + (y * y));
      if (!double.IsFinite(length) || length <= 0D)
      {
        throw new CommonLineValidationException(error);
      }

      return new CommonLinePoint(x / length, y / length);
    }

    private static CommonLinePoint Interpolate(
      CommonLinePoint start,
      CommonLinePoint end,
      double parameter)
      => new CommonLinePoint(
        start.X + ((end.X - start.X) * parameter),
        start.Y + ((end.Y - start.Y) * parameter));

    private static CommonLinePoint PointOnCircle(
      CommonLinePoint center,
      double radius,
      double angle)
      => new CommonLinePoint(
        center.X + (radius * Math.Cos(angle)),
        center.Y + (radius * Math.Sin(angle)));

    private static double Distance(CommonLinePoint first, CommonLinePoint second)
    {
      double dx = second.X - first.X;
      double dy = second.Y - first.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }
  }
}
