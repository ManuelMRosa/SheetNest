namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using DeepNestLib.IO;

  /// <summary>
  /// A deterministic exact-arc placement proposal. Applying <see cref="TranslationX"/> and
  /// <see cref="TranslationY"/> to Part B makes the two recorded source spans a valid common seam.
  /// The proposal itself never mutates either placement.
  /// </summary>
  public sealed class DxfCommonLineArcCandidate
  {
    internal DxfCommonLineArcCandidate(
      CommonBoundarySpan spanA,
      CommonBoundarySpan spanB,
      double translationX,
      double translationY,
      double sharedLength,
      CommonCenterCurve centerCurve)
    {
      this.SpanA = spanA;
      this.SpanB = spanB;
      this.TranslationX = translationX;
      this.TranslationY = translationY;
      this.SharedLength = sharedLength;
      this.CenterCurve = centerCurve;
    }

    public CommonBoundarySpan SpanA { get; }

    public CommonBoundarySpan SpanB { get; }

    public double TranslationX { get; }

    public double TranslationY { get; }

    public double SharedLength { get; }

    public CommonCenterCurve CenterCurve { get; }

    public double Travel => Math.Sqrt(
      (this.TranslationX * this.TranslationX) + (this.TranslationY * this.TranslationY));
  }

  /// <summary>
  /// Finds exact concentric circular-arc matches for the placements' current orientations. Translation
  /// is the only proposed motion; ELLIPSE/SPLINE and tessellated chords never become candidates.
  /// </summary>
  public static class DxfCommonLineArcCandidateFinder
  {
    public static IReadOnlyList<DxfCommonLineArcCandidate> Find(
      IPartPlacement partA,
      IPartPlacement partB,
      double commonLineGap,
      double geometryTolerance)
      => Find(partA, partB, commonLineGap, geometryTolerance, null, null);

    /// <summary>
    /// Reuses immutable source catalogs for a nesting search. Final acceptance still revalidates the
    /// source fingerprints; enumerating thousands of poses does not need to reread the same drawings.
    /// </summary>
    public static IReadOnlyList<DxfCommonLineArcCandidate> Find(
      IPartPlacement partA,
      IPartPlacement partB,
      double commonLineGap,
      double geometryTolerance,
      DxfContourCatalog catalogA,
      DxfContourCatalog catalogB)
    {
      if (partA == null)
      {
        throw new ArgumentNullException(nameof(partA));
      }

      if (partB == null)
      {
        throw new ArgumentNullException(nameof(partB));
      }

      if (!double.IsFinite(commonLineGap)
        || !double.IsFinite(geometryTolerance)
        || geometryTolerance <= 0D
        || commonLineGap <= geometryTolerance)
      {
        throw new CommonLineValidationException(
          "Common Line Gap must be finite and larger than GeometryTolerance.");
      }

      if ((catalogA == null && !TryLoad(partA, geometryTolerance, out catalogA))
        || (catalogB == null && !TryLoad(partB, geometryTolerance, out catalogB)))
      {
        return Array.Empty<DxfCommonLineArcCandidate>();
      }

      var candidates = new List<DxfCommonLineArcCandidate>();
      foreach ((CommonBoundarySpan spanA, DxfResolvedBoundarySpan resolvedA) in Arcs(catalogA, partA))
      {
        foreach ((CommonBoundarySpan spanB, DxfResolvedBoundarySpan resolvedB) in Arcs(catalogB, partB))
        {
          if (Math.Abs(Math.Abs(resolvedA.Radius - resolvedB.Radius) - commonLineGap)
              > geometryTolerance)
          {
            continue;
          }

          double translationX = resolvedA.Center.X - resolvedB.Center.X;
          double translationY = resolvedA.Center.Y - resolvedB.Center.Y;
          PartPlacement shiftedB = ShiftedCopy(partB, translationX, translationY);
          foreach ((double startAngle, double endAngle) in AngularOverlaps(
            resolvedA,
            resolvedB,
            geometryTolerance / Math.Max(resolvedA.Radius, resolvedB.Radius)))
          {
            CommonBoundarySpan overlapA;
            CommonBoundarySpan overlapB;
            try
            {
              overlapA = SliceArcSpan(
                catalogA,
                spanA,
                resolvedA,
                startAngle,
                endAngle,
                geometryTolerance);
              overlapB = SliceArcSpan(
                catalogB,
                spanB,
                resolvedB,
                startAngle,
                endAngle,
                geometryTolerance);
            }
            catch (CommonLineValidationException)
            {
              // One numerically degenerate overlap cannot invalidate another exact source-arc pair.
              continue;
            }

            DxfResolvedBoundarySpan slicedA;
            DxfResolvedBoundarySpan slicedB;
            CommonCenterCurve center;
            try
            {
              slicedA = DxfCommonLineSpanResolver.Resolve(catalogA, overlapA, partA);
              slicedB = DxfCommonLineSpanResolver.Resolve(catalogB, overlapB, shiftedB);
              center = DxfCommonLineSpanResolver.CreateCenterCurve(
                slicedA,
                slicedB,
                commonLineGap,
                geometryTolerance);
            }
            catch (CommonLineValidationException)
            {
              continue;
            }

            candidates.Add(new DxfCommonLineArcCandidate(
              overlapA,
              overlapB,
              translationX,
              translationY,
              Math.Min(slicedA.Length, slicedB.Length),
              center));
          }
        }
      }

      return candidates
        .OrderByDescending(candidate => candidate.SharedLength)
        .ThenBy(candidate => candidate.Travel)
        .ThenBy(candidate => candidate.SpanA.ContourIndex)
        .ThenBy(candidate => candidate.SpanA.PrimitiveIndex)
        .ThenBy(candidate => candidate.SpanB.ContourIndex)
        .ThenBy(candidate => candidate.SpanB.PrimitiveIndex)
        .ToArray();
    }

    private static CommonBoundarySpan SliceArcSpan(
      DxfContourCatalog catalog,
      CommonBoundarySpan fullSpan,
      DxfResolvedBoundarySpan resolved,
      double counterClockwiseStart,
      double counterClockwiseEnd,
      double geometryTolerance)
    {
      double firstAngle = resolved.SweepRadians > 0D
        ? counterClockwiseStart
        : counterClockwiseEnd;
      double lastAngle = resolved.SweepRadians > 0D
        ? counterClockwiseEnd
        : counterClockwiseStart;
      double first = ParameterAtAngle(resolved, firstAngle);
      double last = ParameterAtAngle(resolved, lastAngle);
      double low = Math.Max(0D, Math.Min(first, last));
      double high = Math.Min(1D, Math.Max(first, last));
      double sourceStart = fullSpan.StartParameter
        + ((fullSpan.EndParameter - fullSpan.StartParameter) * low);
      double sourceEnd = fullSpan.StartParameter
        + ((fullSpan.EndParameter - fullSpan.StartParameter) * high);
      if ((high - low) * resolved.Length <= geometryTolerance)
      {
        throw new CommonLineValidationException(
          "A circular common-line overlap is shorter than GeometryTolerance.");
      }

      return DxfCommonLineSpanResolver.CreateSpan(
        catalog,
        fullSpan.ContourIndex,
        fullSpan.PrimitiveIndex,
        sourceStart,
        sourceEnd);
    }

    private static double ParameterAtAngle(DxfResolvedBoundarySpan arc, double angle)
    {
      const double ParameterTolerance = 1e-9D;
      double turn = 2D * Math.PI;
      double sweep = Math.Abs(arc.SweepRadians);
      long nearestTurn = (long)Math.Round((arc.StartAngleRadians - angle) / turn);
      double bestParameter = double.NaN;
      double bestResidual = double.PositiveInfinity;
      long bestTurnShift = long.MaxValue;
      for (long offset = -2; offset <= 2; offset++)
      {
        long turnShift = nearestTurn + offset;
        double equivalentAngle = angle + (turnShift * turn);
        double travel = arc.SweepRadians > 0D
          ? equivalentAngle - arc.StartAngleRadians
          : arc.StartAngleRadians - equivalentAngle;
        double parameter = travel / sweep;
        double residual = parameter < 0D
          ? -parameter
          : parameter > 1D ? parameter - 1D : 0D;
        if (residual < bestResidual
          || (Math.Abs(residual - bestResidual) <= ParameterTolerance
            && Math.Abs(turnShift) < Math.Abs(bestTurnShift)))
        {
          bestResidual = residual;
          bestParameter = parameter;
          bestTurnShift = turnShift;
        }
      }

      if (!double.IsFinite(bestParameter) || bestResidual > ParameterTolerance)
      {
        throw new CommonLineValidationException(
          "A circular overlap endpoint does not lie on the selected source arc.");
      }

      return Math.Clamp(bestParameter, 0D, 1D);
    }

    private static IEnumerable<(double Start, double End)> AngularOverlaps(
      DxfResolvedBoundarySpan first,
      DxfResolvedBoundarySpan second,
      double angularTolerance)
    {
      var overlaps = new List<(double Start, double End)>();
      foreach ((double firstStart, double firstEnd) in AngularIntervals(first))
      {
        foreach ((double secondStart, double secondEnd) in AngularIntervals(second))
        {
          double start = Math.Max(firstStart, secondStart);
          double end = Math.Min(firstEnd, secondEnd);
          if (end - start > angularTolerance)
          {
            overlaps.Add((start, end));
          }
        }
      }

      overlaps = overlaps
        .OrderBy(interval => interval.Start)
        .ThenBy(interval => interval.End)
        .ToList();
      var merged = new List<(double Start, double End)>();
      foreach ((double start, double end) in overlaps)
      {
        if (merged.Count == 0 || start > merged[^1].End + angularTolerance)
        {
          merged.Add((start, end));
        }
        else
        {
          (double priorStart, double priorEnd) = merged[^1];
          merged[^1] = (priorStart, Math.Max(priorEnd, end));
        }
      }

      // Zero is an arbitrary branch cut, not a geometric endpoint. Join the first and last interval
      // when they meet across 2pi so a semicircle crossing zero remains one candidate, not two quarters.
      if (merged.Count > 1
        && merged[0].Start <= angularTolerance
        && merged[^1].End >= (2D * Math.PI) - angularTolerance)
      {
        (double firstStart, double firstEnd) = merged[0];
        (double lastStart, double lastEnd) = merged[^1];
        merged.RemoveAt(merged.Count - 1);
        merged.RemoveAt(0);
        merged.Insert(0, (lastStart, firstEnd + (2D * Math.PI)));
      }

      foreach ((double start, double end) in merged)
      {
        yield return (start, end);
      }
    }

    private static IEnumerable<(double Start, double End)> AngularIntervals(
      DxfResolvedBoundarySpan arc)
    {
      double length = Math.Min(2D * Math.PI, Math.Abs(arc.SweepRadians));
      if (length >= (2D * Math.PI) - 1e-12D)
      {
        yield return (0D, 2D * Math.PI);
        yield break;
      }

      double directedStart = arc.SweepRadians >= 0D
        ? arc.StartAngleRadians
        : arc.StartAngleRadians + arc.SweepRadians;
      double start = PositiveAngle(directedStart);
      double end = start + length;
      if (end <= 2D * Math.PI)
      {
        yield return (start, end);
      }
      else
      {
        yield return (start, 2D * Math.PI);
        yield return (0D, end - (2D * Math.PI));
      }
    }

    private static double PositiveAngle(double angle)
    {
      double result = angle % (2D * Math.PI);
      return result < 0D ? result + (2D * Math.PI) : result;
    }

    private static IEnumerable<(CommonBoundarySpan Span, DxfResolvedBoundarySpan Resolved)> Arcs(
      DxfContourCatalog catalog,
      IPartPlacement placement)
    {
      foreach (DxfSourceContour contour in catalog.Contours.Where(contour =>
        !contour.IsChild && contour.IsClosed))
      {
        foreach (DxfSourcePrimitive primitive in contour.Primitives.Where(primitive =>
          primitive.GeometryKind == CommonLineGeometryKind.CircularArc))
        {
          CommonBoundarySpan span = DxfCommonLineSpanResolver.CreateSpan(
            catalog,
            contour.Index,
            primitive.Index);
          yield return (span, DxfCommonLineSpanResolver.Resolve(catalog, span, placement));
        }
      }
    }

    private static PartPlacement ShiftedCopy(
      IPartPlacement source,
      double translationX,
      double translationY)
      => new PartPlacement(source.Part)
      {
        Id = source.Id,
        IsMirrored = source.IsMirrored,
        Rotation = source.Rotation,
        Source = source.Source,
        X = source.X + translationX,
        Y = source.Y + translationY,
      };

    private static bool TryLoad(
      IPartPlacement placement,
      double endpointTolerance,
      out DxfContourCatalog catalog)
    {
      catalog = null;
      string sourceName = placement?.Part?.Name;
      if (string.IsNullOrWhiteSpace(sourceName)
        || !sourceName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase))
      {
        return false;
      }

      try
      {
        string path = Path.GetFullPath(sourceName);
        if (!File.Exists(path))
        {
          return false;
        }

        catalog = DxfContourCatalog.Load(path, endpointTolerance);
        return true;
      }
      catch
      {
        catalog = null;
        return false;
      }
    }
  }
}
