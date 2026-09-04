namespace DeepNestLib.Placement
{
  using System;
  using DeepNestLib.IO;

  /// <summary>
  /// Resolves fingerprinted DXF contour intervals without tessellating curves. It is deliberately
  /// independent from the cluster resolver so the nester, exporter, and validator can share the same
  /// exact source-to-world transform and compatibility checks.
  /// </summary>
  public static class DxfCommonLineSpanResolver
  {
    private const double MinimumAngularTolerance = 1e-10D;

    /// <summary>Creates a stable fingerprinted reference to one eligible outside-contour interval.</summary>
    public static CommonBoundarySpan CreateSpan(
      DxfContourCatalog catalog,
      int contourIndex,
      int primitiveIndex,
      double startParameter = 0D,
      double endParameter = 1D)
    {
      DxfSourcePrimitive primitive = GetPrimitive(
        catalog,
        contourIndex,
        primitiveIndex,
        startParameter,
        endParameter,
        out DxfSourceContour contour);
      ValidateEligiblePrimitive(primitive);
      return new CommonBoundarySpan(
        contour.Index,
        primitive.Index,
        startParameter,
        endParameter,
        catalog.SourceFingerprint,
        contour.Fingerprint);
    }

    /// <summary>Resolves a fingerprinted source interval into exact world-space geometry.</summary>
    public static DxfResolvedBoundarySpan Resolve(
      DxfContourCatalog catalog,
      CommonBoundarySpan span,
      IPartPlacement placement)
    {
      if (span == null)
      {
        throw new ArgumentNullException(nameof(span));
      }

      ValidatePlacement(placement);
      ValidateFingerprints(catalog, span);
      DxfSourcePrimitive primitive = GetPrimitive(
        catalog,
        span.ContourIndex,
        span.PrimitiveIndex,
        span.StartParameter,
        span.EndParameter,
        out _);
      ValidateEligiblePrimitive(primitive);

      double sourceFraction = span.EndParameter - span.StartParameter;
      int worldOrientation = placement.IsMirrored ? -1 : 1;
      if (primitive.GeometryKind == CommonLineGeometryKind.Line)
      {
        CommonLinePoint localStart = Interpolate(
          primitive.Start,
          primitive.End,
          span.StartParameter);
        CommonLinePoint localEnd = Interpolate(
          primitive.Start,
          primitive.End,
          span.EndParameter);
        CommonLinePoint start = TransformPoint(placement, localStart);
        CommonLinePoint end = TransformPoint(placement, localEnd);
        if (Distance(start, end) <= 0D)
        {
          throw Invalid("The selected LINE interval is degenerate.");
        }

        return new DxfResolvedBoundarySpan(
          span,
          CommonLineGeometryKind.Line,
          start,
          end,
          null,
          0D,
          0D,
          0D,
          worldOrientation);
      }

      if (!double.IsFinite(primitive.Radius) || primitive.Radius <= 0D
        || !double.IsFinite(primitive.StartAngleRadians)
        || !double.IsFinite(primitive.SweepRadians)
        || Math.Abs(primitive.SweepRadians * sourceFraction) <= 0D)
      {
        throw Invalid("The selected circular-arc interval is degenerate.");
      }

      double localStartAngle = primitive.StartAngleRadians
        + (primitive.SweepRadians * span.StartParameter);
      double localSweep = primitive.SweepRadians * sourceFraction;
      CommonLinePoint localArcStart = PointOnCircle(
        primitive.Center,
        primitive.Radius,
        localStartAngle);
      CommonLinePoint localArcEnd = PointOnCircle(
        primitive.Center,
        primitive.Radius,
        localStartAngle + localSweep);
      CommonLinePoint center = TransformPoint(placement, primitive.Center);
      CommonLinePoint arcStart = TransformPoint(placement, localArcStart);
      CommonLinePoint arcEnd = TransformPoint(placement, localArcEnd);
      double worldStartAngle = Math.Atan2(arcStart.Y - center.Y, arcStart.X - center.X);
      double worldSweep = placement.IsMirrored ? -localSweep : localSweep;

      return new DxfResolvedBoundarySpan(
        span,
        CommonLineGeometryKind.CircularArc,
        arcStart,
        arcEnd,
        center,
        primitive.Radius,
        worldStartAngle,
        worldSweep,
        worldOrientation);
    }

    public static bool TryResolve(
      DxfContourCatalog catalog,
      CommonBoundarySpan span,
      IPartPlacement placement,
      out DxfResolvedBoundarySpan resolved,
      out string error)
    {
      try
      {
        resolved = Resolve(catalog, span, placement);
        error = null;
        return true;
      }
      catch (CommonLineValidationException ex)
      {
        resolved = null;
        error = ex.Message;
        return false;
      }
    }

    /// <summary>
    /// Resolves two source intervals and produces their exact midpoint cutting curve when their
    /// outward faces, directed extents, and requested physical separation are compatible.
    /// </summary>
    public static CommonCenterCurve CreateCenterCurve(
      DxfContourCatalog catalogA,
      CommonBoundarySpan spanA,
      IPartPlacement placementA,
      DxfContourCatalog catalogB,
      CommonBoundarySpan spanB,
      IPartPlacement placementB,
      double commonLineGap,
      double geometryTolerance)
      => CreateCenterCurve(
        Resolve(catalogA, spanA, placementA),
        Resolve(catalogB, spanB, placementB),
        commonLineGap,
        geometryTolerance);

    /// <summary>Produces the exact midpoint cutting curve for two already-resolved intervals.</summary>
    public static CommonCenterCurve CreateCenterCurve(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      double commonLineGap,
      double geometryTolerance)
      => CreateCenterCurve(
        first,
        second,
        commonLineGap,
        geometryTolerance,
        out _);

    /// <summary>
    /// Produces an exact midpoint curve and reports whether the second source interval has to be
    /// traversed in reverse to follow the first. The cluster resolver uses the same validated
    /// correspondence for its endpoint connectors.
    /// </summary>
    internal static CommonCenterCurve CreateCenterCurve(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      double commonLineGap,
      double geometryTolerance,
      out bool reverseSecondParameter)
    {
      if (first == null)
      {
        throw new ArgumentNullException(nameof(first));
      }

      if (second == null)
      {
        throw new ArgumentNullException(nameof(second));
      }

      ValidateSettings(commonLineGap, geometryTolerance);
      if (first.GeometryKind != second.GeometryKind)
      {
        throw Invalid("Common boundary spans must have the same analytic geometry kind.");
      }

      ValidateOppositeFaces(first, second, geometryTolerance);
      if (first.GeometryKind == CommonLineGeometryKind.Line)
      {
        reverseSecondParameter = true;
        return CreateLineCenter(first, second, commonLineGap, geometryTolerance);
      }

      if (first.GeometryKind == CommonLineGeometryKind.CircularArc)
      {
        return CreateArcCenter(
          first,
          second,
          commonLineGap,
          geometryTolerance,
          out reverseSecondParameter);
      }

      throw Invalid("Only exact LINE and circular-arc spans can form a common cut.");
    }

    public static bool TryCreateCenterCurve(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      double commonLineGap,
      double geometryTolerance,
      out CommonCenterCurve centerCurve,
      out string error)
    {
      try
      {
        centerCurve = CreateCenterCurve(first, second, commonLineGap, geometryTolerance);
        error = null;
        return true;
      }
      catch (CommonLineValidationException ex)
      {
        centerCurve = null;
        error = ex.Message;
        return false;
      }
    }

    private static CommonCenterCurve CreateLineCenter(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      double gap,
      double tolerance)
    {
      if (Math.Abs(first.Length - second.Length) > tolerance)
      {
        throw Invalid("Common LINE intervals do not have matching usable lengths.");
      }

      CommonLinePoint firstTangent = first.TangentAtMidpoint;
      CommonLinePoint secondTangent = second.TangentAtMidpoint;
      double characteristicLength = Math.Max(first.Length, second.Length);
      if (Math.Abs(Cross(firstTangent, secondTangent)) * characteristicLength > tolerance
        || Dot(firstTangent, secondTangent) >= 0D)
      {
        throw Invalid("Common LINE intervals are not parallel and oppositely directed.");
      }

      ValidateFacingSeparation(first, 0D, second, 1D, gap, tolerance);
      ValidateFacingSeparation(first, 1D, second, 0D, gap, tolerance);
      return new CommonCenterCurve(
        CommonLineGeometryKind.Line,
        Midpoint(first.Start, second.End),
        Midpoint(first.End, second.Start));
    }

    private static CommonCenterCurve CreateArcCenter(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      double gap,
      double tolerance,
      out bool reverseSecondParameter)
    {
      if (Distance(first.Center, second.Center) > tolerance)
      {
        throw Invalid("Common circular arcs are not concentric within GeometryTolerance.");
      }

      if (Math.Abs(Math.Abs(first.Radius - second.Radius) - gap) > tolerance)
      {
        throw Invalid("Common circular arcs are not separated by CommonLineGap.");
      }

      double centerRadius = (first.Radius + second.Radius) / 2D;
      if (Math.Abs(Math.Abs(first.SweepRadians) - Math.Abs(second.SweepRadians)) * centerRadius
        > tolerance)
      {
        throw Invalid("Common circular arcs do not cover matching angular extents.");
      }

      ArcParameterCorrespondence correspondence = ResolveArcCorrespondence(
        first,
        second,
        gap,
        tolerance);
      ValidateArcCorrespondence(first, second, correspondence, gap, tolerance);
      reverseSecondParameter = correspondence == ArcParameterCorrespondence.Reversed;

      CommonLinePoint center = Midpoint(first.Center, second.Center);
      CommonLinePoint firstRadial = Normalize(
        first.Start.X - first.Center.X,
        first.Start.Y - first.Center.Y,
        "A common circular arc has an invalid start radius.");
      double startAngle = Math.Atan2(firstRadial.Y, firstRadial.X);
      CommonLinePoint start = PointOnCircle(center, centerRadius, startAngle);
      CommonLinePoint end = PointOnCircle(
        center,
        centerRadius,
        startAngle + first.SweepRadians);
      return new CommonCenterCurve(
        CommonLineGeometryKind.CircularArc,
        start,
        end,
        center,
        centerRadius,
        startAngle,
        first.SweepRadians);
    }

    private static ArcParameterCorrespondence ResolveArcCorrespondence(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      double gap,
      double tolerance)
    {
      double sameResidual = Math.Max(
        GapResidual(first.Start, second.Start, gap),
        GapResidual(first.End, second.End, gap));
      double reversedResidual = Math.Max(
        GapResidual(first.Start, second.End, gap),
        GapResidual(first.End, second.Start, gap));
      bool sameEndpointsMatch = sameResidual <= tolerance;
      bool reversedEndpointsMatch = reversedResidual <= tolerance;

      if (!sameEndpointsMatch && !reversedEndpointsMatch)
      {
        throw Invalid(
          "Common circular arc endpoints do not identify a same- or reversed-parameter correspondence.");
      }

      // Endpoint distances establish the candidate correspondence. Validate the full analytic
      // direction with quarter points as well: start/end/midpoint alone are ambiguous for a full
      // circle, especially after exactly one placement is mirrored.
      ArcParameterCorrespondence preferred = sameResidual <= reversedResidual
        ? ArcParameterCorrespondence.Same
        : ArcParameterCorrespondence.Reversed;
      ArcParameterCorrespondence alternate = preferred == ArcParameterCorrespondence.Same
        ? ArcParameterCorrespondence.Reversed
        : ArcParameterCorrespondence.Same;
      if (IsEndpointCandidate(preferred, sameEndpointsMatch, reversedEndpointsMatch)
        && IsArcCorrespondenceValid(first, second, preferred, gap, tolerance))
      {
        return preferred;
      }

      if (IsEndpointCandidate(alternate, sameEndpointsMatch, reversedEndpointsMatch)
        && IsArcCorrespondenceValid(first, second, alternate, gap, tolerance))
      {
        return alternate;
      }

      throw Invalid(
        "Common circular arcs do not maintain opposite outward faces and CommonLineGap over their matched extents.");
    }

    private static bool IsEndpointCandidate(
      ArcParameterCorrespondence correspondence,
      bool sameEndpointsMatch,
      bool reversedEndpointsMatch)
      => correspondence == ArcParameterCorrespondence.Same
        ? sameEndpointsMatch
        : reversedEndpointsMatch;

    private static bool IsArcCorrespondenceValid(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      ArcParameterCorrespondence correspondence,
      double gap,
      double tolerance)
    {
      try
      {
        ValidateArcCorrespondence(first, second, correspondence, gap, tolerance);
        return true;
      }
      catch (CommonLineValidationException)
      {
        return false;
      }
    }

    private static void ValidateArcCorrespondence(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      ArcParameterCorrespondence correspondence,
      double gap,
      double tolerance)
    {
      for (int index = 0; index <= 4; index++)
      {
        double firstParameter = index / 4D;
        double secondParameter = correspondence == ArcParameterCorrespondence.Same
          ? firstParameter
          : 1D - firstParameter;
        ValidateFacingSeparation(
          first,
          firstParameter,
          second,
          secondParameter,
          gap,
          tolerance);
      }
    }

    private static double GapResidual(
      CommonLinePoint first,
      CommonLinePoint second,
      double gap)
      => Math.Abs(Distance(first, second) - gap);

    private static void ValidateFacingSeparation(
      DxfResolvedBoundarySpan first,
      double firstParameter,
      DxfResolvedBoundarySpan second,
      double secondParameter,
      double gap,
      double tolerance)
    {
      CommonLinePoint firstPoint = first.PointAt(firstParameter);
      CommonLinePoint secondPoint = second.PointAt(secondParameter);
      CommonLinePoint firstNormal = first.OutwardNormalAt(firstParameter);
      CommonLinePoint secondNormal = second.OutwardNormalAt(secondParameter);
      double dx = secondPoint.X - firstPoint.X;
      double dy = secondPoint.Y - firstPoint.Y;
      double distance = Math.Sqrt((dx * dx) + (dy * dy));
      if (Math.Abs(distance - gap) > tolerance)
      {
        throw Invalid("Common boundary spans are not separated by CommonLineGap.");
      }

      double alongFirstNormal = (dx * firstNormal.X) + (dy * firstNormal.Y);
      double alongSecondNormal = (-dx * secondNormal.X) + (-dy * secondNormal.Y);
      if (Math.Abs(alongFirstNormal - gap) > tolerance
        || Math.Abs(alongSecondNormal - gap) > tolerance)
      {
        throw Invalid("The gap does not lie between the two outward-facing boundaries.");
      }
    }

    private static void ValidateOppositeFaces(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      double tolerance)
    {
      CommonLinePoint firstNormal = first.OutwardNormalAtMidpoint;
      CommonLinePoint secondNormal = second.OutwardNormalAtMidpoint;
      double characteristicLength = Math.Max(
        Math.Max(first.Length, second.Length),
        tolerance);
      double normalTolerance = Math.Max(
        MinimumAngularTolerance,
        Math.Min(0.01D, tolerance / characteristicLength));
      double normalDifference = Distance(
        firstNormal,
        new CommonLinePoint(-secondNormal.X, -secondNormal.Y));
      if (normalDifference > normalTolerance)
      {
        throw Invalid("Common boundary spans do not have opposite outward normals.");
      }
    }

    private static void ValidateFingerprints(DxfContourCatalog catalog, CommonBoundarySpan span)
    {
      if (catalog == null)
      {
        throw new ArgumentNullException(nameof(catalog));
      }

      if (string.IsNullOrWhiteSpace(span.SourceFingerprint)
        || !string.Equals(span.SourceFingerprint, catalog.SourceFingerprint, StringComparison.Ordinal))
      {
        throw Invalid("The source fingerprint for a common boundary span is missing or stale.");
      }

      if (span.ContourIndex < 0 || span.ContourIndex >= catalog.Contours.Count)
      {
        throw Invalid("A common boundary span references a missing source contour.");
      }

      DxfSourceContour contour = catalog.Contours[span.ContourIndex];
      if (string.IsNullOrWhiteSpace(span.ContourFingerprint)
        || !string.Equals(span.ContourFingerprint, contour.Fingerprint, StringComparison.Ordinal))
      {
        throw Invalid("The contour fingerprint for a common boundary span is missing or stale.");
      }
    }

    private static DxfSourcePrimitive GetPrimitive(
      DxfContourCatalog catalog,
      int contourIndex,
      int primitiveIndex,
      double startParameter,
      double endParameter,
      out DxfSourceContour contour)
    {
      if (catalog == null)
      {
        throw new ArgumentNullException(nameof(catalog));
      }

      ValidateInterval(startParameter, endParameter);
      if (contourIndex < 0 || contourIndex >= catalog.Contours.Count)
      {
        throw Invalid("A common boundary span references a missing source contour.");
      }

      contour = catalog.Contours[contourIndex];
      if (contour.IsChild || !contour.IsClosed)
      {
        throw Invalid("Common boundary spans must belong to a closed outside contour.");
      }

      if (primitiveIndex < 0 || primitiveIndex >= contour.Primitives.Count)
      {
        throw Invalid("A common boundary span references a missing source primitive.");
      }

      return contour.Primitives[primitiveIndex];
    }

    private static void ValidateEligiblePrimitive(DxfSourcePrimitive primitive)
    {
      if (primitive.GeometryKind != CommonLineGeometryKind.Line
        && primitive.GeometryKind != CommonLineGeometryKind.CircularArc)
      {
        throw Invalid(
          $"Exact common cutting does not support {primitive.GeometryKind} source primitives.");
      }
    }

    private static void ValidateInterval(double startParameter, double endParameter)
    {
      if (!double.IsFinite(startParameter)
        || !double.IsFinite(endParameter)
        || startParameter < 0D
        || endParameter > 1D
        || startParameter >= endParameter)
      {
        throw Invalid("A common boundary interval must be finite and satisfy 0 <= start < end <= 1.");
      }
    }

    private static void ValidatePlacement(IPartPlacement placement)
    {
      if (placement == null)
      {
        throw new ArgumentNullException(nameof(placement));
      }

      if (!double.IsFinite(placement.X)
        || !double.IsFinite(placement.Y)
        || !double.IsFinite(placement.Rotation))
      {
        throw Invalid("A common boundary placement has a non-finite transform.");
      }
    }

    private static void ValidateSettings(double gap, double tolerance)
    {
      if (!double.IsFinite(tolerance) || tolerance <= 0D)
      {
        throw Invalid("GeometryTolerance must be a finite positive distance.");
      }

      if (!double.IsFinite(gap) || gap <= tolerance)
      {
        throw Invalid("Common Line Gap must be finite and larger than GeometryTolerance.");
      }
    }

    private static CommonLinePoint TransformPoint(
      IPartPlacement placement,
      CommonLinePoint point)
    {
      double x = placement.IsMirrored ? -point.X : point.X;
      double angle = placement.Rotation * Math.PI / 180D;
      double cosine = Math.Cos(angle);
      double sine = Math.Sin(angle);
      return new CommonLinePoint(
        (x * cosine) - (point.Y * sine) + placement.X,
        (x * sine) + (point.Y * cosine) + placement.Y);
    }

    private static CommonLinePoint Interpolate(
      CommonLinePoint start,
      CommonLinePoint end,
      double parameter)
      => new CommonLinePoint(
        start.X + ((end.X - start.X) * parameter),
        start.Y + ((end.Y - start.Y) * parameter));

    private static CommonLinePoint Midpoint(CommonLinePoint first, CommonLinePoint second)
      => new CommonLinePoint((first.X + second.X) / 2D, (first.Y + second.Y) / 2D);

    private static CommonLinePoint Normalize(double x, double y, string error)
    {
      double length = Math.Sqrt((x * x) + (y * y));
      if (!double.IsFinite(length) || length <= 0D)
      {
        throw Invalid(error);
      }

      return new CommonLinePoint(x / length, y / length);
    }

    private static CommonLinePoint PointOnCircle(
      CommonLinePoint center,
      double radius,
      double angle)
      => new CommonLinePoint(
        center.X + (radius * Math.Cos(angle)),
        center.Y + (radius * Math.Sin(angle)));

    private static double Dot(CommonLinePoint first, CommonLinePoint second)
      => (first.X * second.X) + (first.Y * second.Y);

    private static double Cross(CommonLinePoint first, CommonLinePoint second)
      => (first.X * second.Y) - (first.Y * second.X);

    private static double Distance(CommonLinePoint first, CommonLinePoint second)
    {
      double dx = second.X - first.X;
      double dy = second.Y - first.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static CommonLineValidationException Invalid(string message)
      => new CommonLineValidationException(message);

    private enum ArcParameterCorrespondence
    {
      Same,
      Reversed,
    }
  }
}
