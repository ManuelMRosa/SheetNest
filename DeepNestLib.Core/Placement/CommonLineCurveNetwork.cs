namespace DeepNestLib.Placement
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using System.Threading;

  /// <summary>
  /// Builds the deterministic open cut graph for exact straight and circular common paths. It merges
  /// coincident paths, splits every path at T/+ intersections and gives all consumers one canonical node.
  /// </summary>
  internal static class CommonLineCurveNetwork
  {
    private const double TwoPi = 2D * Math.PI;

    public static IReadOnlyList<CommonCenterCurve> Build(
      IEnumerable<CommonCenterCurve> input,
      double bridgeDistance,
      double tolerance,
      CancellationToken cancel = default)
    {
      if (input == null)
      {
        throw new ArgumentNullException(nameof(input));
      }

      if (!double.IsFinite(tolerance) || tolerance <= 0D)
      {
        throw new ArgumentOutOfRangeException(nameof(tolerance));
      }

      if (!double.IsFinite(bridgeDistance))
      {
        throw new ArgumentOutOfRangeException(nameof(bridgeDistance));
      }

      var lines = new List<LineWork>();
      var arcs = new List<ArcWork>();
      foreach (CommonCenterCurve curve in input)
      {
        cancel.ThrowIfCancellationRequested();
        if (curve == null)
        {
          throw Invalid("A COMMON_LINE curve is null.");
        }

        switch (curve.GeometryKind)
        {
          case CommonLineGeometryKind.Line:
            lines.Add(LineWork.From(curve, tolerance));
            break;
          case CommonLineGeometryKind.CircularArc:
            arcs.Add(ArcWork.From(curve, tolerance));
            break;
          default:
            throw Invalid("Only exact LINE and circular ARC geometry may enter COMMON_LINE.");
        }
      }

      List<LineWork> originalLines = lines.ToList();
      List<ArcWork> originalArcs = arcs.ToList();

      // Coincident/overlapping seams are always safe to deduplicate. A positive empty gap is
      // different: filling it creates cutting geometry that no recorded common edge owns. Build
      // those possible bridges separately and admit only candidates supported by a transverse
      // original curve (LINE/ARC) or by another transverse bridge candidate, as at a + junction.
      lines = MergeLines(lines, tolerance, tolerance);
      List<BridgeCandidate> bridgeCandidates = FindBridgeCandidates(
        lines,
        Math.Max(0D, bridgeDistance) + (2D * tolerance),
        tolerance);
      IReadOnlyList<LineWork> acceptedBridges = AcceptedBridges(
        bridgeCandidates,
        originalLines,
        originalArcs,
        tolerance);
      if (acceptedBridges.Count > 0)
      {
        lines.AddRange(acceptedBridges);
        lines = MergeLines(lines, tolerance, tolerance);
      }

      arcs = MergeArcs(arcs, tolerance);

      var nodes = new NodeRegistry(tolerance);
      var lineSplits = lines.Select(line => new List<SplitPoint>
      {
        new SplitPoint(0D, nodes.Canonical(line.Start)),
        new SplitPoint(1D, nodes.Canonical(line.End)),
      }).ToArray();
      var arcSplits = arcs.Select(arc => new List<SplitPoint>
      {
        new SplitPoint(0D, nodes.Canonical(arc.PointAt(0D))),
        new SplitPoint(1D, nodes.Canonical(arc.PointAt(1D))),
      }).ToArray();

      for (int i = 0; i < lines.Count; i++)
      {
        cancel.ThrowIfCancellationRequested();
        for (int j = i + 1; j < lines.Count; j++)
        {
          cancel.ThrowIfCancellationRequested();
          if (TryLineLine(lines[i], lines[j], tolerance, out double ta, out double tb, out CommonLinePoint point))
          {
            CommonLinePoint node = nodes.Canonical(point);
            lineSplits[i].Add(new SplitPoint(ta, node));
            lineSplits[j].Add(new SplitPoint(tb, node));
          }
        }

        for (int j = 0; j < arcs.Count; j++)
        {
          cancel.ThrowIfCancellationRequested();
          foreach (Intersection intersection in LineArcIntersections(lines[i], arcs[j], tolerance))
          {
            cancel.ThrowIfCancellationRequested();
            CommonLinePoint node = nodes.Canonical(intersection.Point);
            lineSplits[i].Add(new SplitPoint(intersection.FirstParameter, node));
            arcSplits[j].Add(new SplitPoint(intersection.SecondParameter, node));
          }
        }
      }

      for (int i = 0; i < arcs.Count; i++)
      {
        cancel.ThrowIfCancellationRequested();
        for (int j = i + 1; j < arcs.Count; j++)
        {
          cancel.ThrowIfCancellationRequested();
          foreach (Intersection intersection in ArcArcIntersections(arcs[i], arcs[j], tolerance))
          {
            cancel.ThrowIfCancellationRequested();
            CommonLinePoint node = nodes.Canonical(intersection.Point);
            arcSplits[i].Add(new SplitPoint(intersection.FirstParameter, node));
            arcSplits[j].Add(new SplitPoint(intersection.SecondParameter, node));
          }
        }
      }

      var result = new List<CommonCenterCurve>();
      var dedupe = new HashSet<string>(StringComparer.Ordinal);
      for (int i = 0; i < lines.Count; i++)
      {
        cancel.ThrowIfCancellationRequested();
        foreach ((SplitPoint Start, SplitPoint End) span in OrderedSpans(
          lineSplits[i],
          tolerance / Math.Max(lines[i].Length, tolerance)))
        {
          CommonLinePoint start = nodes.Canonical(span.Start.Point);
          CommonLinePoint end = nodes.Canonical(span.End.Point);
          if (Distance(start, end) <= tolerance)
          {
            continue;
          }

          CommonCenterCurve curve = new CommonCenterCurve(CommonLineGeometryKind.Line, start, end);
          if (dedupe.Add(LineKey(start, end, nodes)))
          {
            result.Add(curve);
          }
        }
      }

      for (int i = 0; i < arcs.Count; i++)
      {
        cancel.ThrowIfCancellationRequested();
        ArcWork arc = arcs[i];
        double parameterTolerance = tolerance / Math.Max(arc.Length, tolerance);
        foreach ((SplitPoint Start, SplitPoint End) span in OrderedSpans(arcSplits[i], parameterTolerance))
        {
          cancel.ThrowIfCancellationRequested();
          double sweep = arc.Sweep * (span.End.Parameter - span.Start.Parameter);
          if (arc.Radius * sweep <= tolerance)
          {
            continue;
          }

          double startAngle = arc.StartAngle + (arc.Sweep * span.Start.Parameter);
          // Intersections are registered once and shared by every incident primitive.  Preserve
          // those canonical coordinates here instead of evaluating the circle again: the latter
          // produces numerically-close, but not bit-identical, line/arc and arc/arc endpoints.
          CommonLinePoint start = nodes.Canonical(span.Start.Point);
          CommonLinePoint end = nodes.Canonical(span.End.Point);
          var curve = new CommonCenterCurve(
            CommonLineGeometryKind.CircularArc,
            start,
            end,
            arc.Center,
            arc.Radius,
            NormalizeAngle(startAngle),
            sweep);
          if (dedupe.Add(ArcKey(curve, nodes, tolerance)))
          {
            result.Add(curve);
          }
        }
      }

      return result
        .OrderBy(curve => curve.GeometryKind)
        .ThenBy(curve => Math.Min(curve.Start.X, curve.End.X))
        .ThenBy(curve => Math.Min(curve.Start.Y, curve.End.Y))
        .ThenBy(curve => Math.Max(curve.Start.X, curve.End.X))
        .ThenBy(curve => Math.Max(curve.Start.Y, curve.End.Y))
        .ToArray();
    }

    private static List<LineWork> MergeLines(
      List<LineWork> source,
      double maximumGap,
      double tolerance)
    {
      var merged = source
        .OrderBy(line => Math.Min(line.Start.X, line.End.X))
        .ThenBy(line => Math.Min(line.Start.Y, line.End.Y))
        .ThenBy(line => Math.Max(line.Start.X, line.End.X))
        .ThenBy(line => Math.Max(line.Start.Y, line.End.Y))
        .ToList();
      bool changed;
      do
      {
        changed = false;
        for (int i = 0; i < merged.Count && !changed; i++)
        {
          for (int j = i + 1; j < merged.Count; j++)
          {
            if (LineWork.TryMerge(merged[i], merged[j], maximumGap, tolerance, out LineWork joined))
            {
              merged[i] = joined;
              merged.RemoveAt(j);
              changed = true;
              break;
            }
          }
        }
      }
      while (changed);
      return merged;
    }

    private static List<BridgeCandidate> FindBridgeCandidates(
      IReadOnlyList<LineWork> lines,
      double maximumGap,
      double tolerance)
    {
      var candidates = new List<BridgeCandidate>();
      for (int i = 0; i < lines.Count; i++)
      {
        for (int j = i + 1; j < lines.Count; j++)
        {
          if (LineWork.TryCreateBridge(
            lines[i],
            lines[j],
            maximumGap,
            tolerance,
            out LineWork bridge))
          {
            candidates.Add(new BridgeCandidate(bridge));
          }
        }
      }

      return candidates;
    }

    private static IReadOnlyList<LineWork> AcceptedBridges(
      IReadOnlyList<BridgeCandidate> candidates,
      IReadOnlyList<LineWork> originalLines,
      IReadOnlyList<ArcWork> originalArcs,
      double tolerance)
    {
      var accepted = new bool[candidates.Count];
      for (int i = 0; i < candidates.Count; i++)
      {
        accepted[i] = HasOriginalTransverseEvidence(
          candidates[i].Bridge,
          originalLines,
          originalArcs,
          tolerance);
      }

      // Evaluate bridge candidates as a set. Two transverse candidates crossing in their interiors
      // mutually prove the missing center of a + junction even when neither has an original curve
      // through that tiny gap. Iterate to a fixed point so the result is independent of enumeration.
      bool changed;
      do
      {
        changed = false;
        for (int i = 0; i < candidates.Count; i++)
        {
          for (int j = i + 1; j < candidates.Count; j++)
          {
            if ((!accepted[i] || !accepted[j])
              && BridgeInteriorsIntersect(
                candidates[i].Bridge,
                candidates[j].Bridge,
                tolerance))
            {
              accepted[i] = true;
              accepted[j] = true;
              changed = true;
            }
          }
        }
      }
      while (changed);

      return candidates
        .Where((candidate, index) => accepted[index])
        .Select(candidate => candidate.Bridge)
        .ToArray();
    }

    private static bool HasOriginalTransverseEvidence(
      LineWork bridge,
      IReadOnlyList<LineWork> originalLines,
      IReadOnlyList<ArcWork> originalArcs,
      double tolerance)
    {
      foreach (LineWork line in originalLines)
      {
        if (TryLineLine(
          bridge,
          line,
          tolerance,
          out double bridgeParameter,
          out _,
          out _)
          && IsInterior(bridgeParameter, bridge.Length, tolerance))
        {
          return true;
        }
      }

      foreach (ArcWork arc in originalArcs)
      {
        if (LineArcIntersections(bridge, arc, tolerance)
          .Any(intersection => IsInterior(
            intersection.FirstParameter,
            bridge.Length,
            tolerance)))
        {
          return true;
        }
      }

      return false;
    }

    private static bool BridgeInteriorsIntersect(
      LineWork first,
      LineWork second,
      double tolerance)
      => TryLineLine(
          first,
          second,
          tolerance,
          out double firstParameter,
          out double secondParameter,
          out _)
        && IsInterior(firstParameter, first.Length, tolerance)
        && IsInterior(secondParameter, second.Length, tolerance);

    private static bool IsInterior(double parameter, double length, double tolerance)
    {
      double parameterTolerance = tolerance / Math.Max(length, tolerance);
      return parameter > parameterTolerance && parameter < 1D - parameterTolerance;
    }

    private static List<ArcWork> MergeArcs(List<ArcWork> source, double tolerance)
    {
      // Near-equality is not transitive. Sort first so grouping is independent of caller order, then
      // use complete-link buckets: every arc in a bucket must be within tolerance of every other arc.
      // A union-find chain could otherwise replace the last arc with geometry more than one tolerance
      // away merely because intermediate arcs formed a bridge.
      List<ArcWork> ordered = source
        .OrderBy(arc => arc.Center.X)
        .ThenBy(arc => arc.Center.Y)
        .ThenBy(arc => arc.Radius)
        .ThenBy(arc => arc.StartAngle)
        .ThenBy(arc => arc.Sweep)
        .ToList();
      var groups = new List<List<ArcWork>>();
      foreach (ArcWork arc in ordered)
      {
        List<ArcWork> compatible = groups.FirstOrDefault(group => group.All(existing =>
          Distance(existing.Center, arc.Center) <= tolerance
          && Math.Abs(existing.Radius - arc.Radius) <= tolerance));
        if (compatible == null)
        {
          compatible = new List<ArcWork>();
          groups.Add(compatible);
        }

        compatible.Add(arc);
      }

      var result = new List<ArcWork>();
      foreach (List<ArcWork> group in groups)
      {
        ArcWork basis = group[0];
        double angularTolerance = tolerance / Math.Max(basis.Radius, tolerance);
        var intervals = new List<AngleInterval>();
        foreach (ArcWork arc in group)
        {
          intervals.AddRange(arc.Intervals());
        }

        intervals = intervals.OrderBy(interval => interval.Start).ThenBy(interval => interval.End).ToList();
        var union = new List<AngleInterval>();
        foreach (AngleInterval interval in intervals)
        {
          if (union.Count == 0 || interval.Start > union[^1].End + angularTolerance)
          {
            union.Add(interval);
          }
          else
          {
            AngleInterval prior = union[^1];
            union[^1] = new AngleInterval(prior.Start, Math.Max(prior.End, interval.End));
          }
        }

        if (union.Count > 1
          && union[0].Start <= angularTolerance
          && union[^1].End >= TwoPi - angularTolerance)
        {
          AngleInterval first = union[0];
          AngleInterval last = union[^1];
          union.RemoveAt(union.Count - 1);
          union.RemoveAt(0);
          union.Insert(0, new AngleInterval(last.Start, TwoPi + first.End));
        }

        foreach (AngleInterval interval in union)
        {
          double sweep = Math.Min(TwoPi, interval.End - interval.Start);
          if (basis.Radius * sweep > tolerance)
          {
            result.Add(new ArcWork(basis.Center, basis.Radius, NormalizeAngle(interval.Start), sweep));
          }
        }
      }

      return result;
    }

    private static IEnumerable<(SplitPoint Start, SplitPoint End)> OrderedSpans(
      IEnumerable<SplitPoint> source,
      double parameterTolerance)
    {
      var points = new List<SplitPoint>();
      foreach (SplitPoint candidate in source.OrderBy(point => point.Parameter))
      {
        double parameter = Math.Max(0D, Math.Min(1D, candidate.Parameter));
        if (points.Count == 0 || Math.Abs(parameter - points[^1].Parameter) > parameterTolerance)
        {
          points.Add(new SplitPoint(parameter, candidate.Point));
        }
      }

      for (int i = 0; i + 1 < points.Count; i++)
      {
        yield return (points[i], points[i + 1]);
      }
    }

    private static bool TryLineLine(
      LineWork first,
      LineWork second,
      double tolerance,
      out double firstParameter,
      out double secondParameter,
      out CommonLinePoint point)
    {
      double rx = first.End.X - first.Start.X;
      double ry = first.End.Y - first.Start.Y;
      double sx = second.End.X - second.Start.X;
      double sy = second.End.Y - second.Start.Y;
      double denominator = Cross(rx, ry, sx, sy);
      double scale = Math.Sqrt(((rx * rx) + (ry * ry)) * ((sx * sx) + (sy * sy)));
      if (scale <= tolerance * tolerance || Math.Abs(denominator) <= tolerance * Math.Sqrt(scale))
      {
        firstParameter = 0D;
        secondParameter = 0D;
        point = null;
        return false;
      }

      double qpx = second.Start.X - first.Start.X;
      double qpy = second.Start.Y - first.Start.Y;
      firstParameter = Cross(qpx, qpy, sx, sy) / denominator;
      secondParameter = Cross(qpx, qpy, rx, ry) / denominator;
      if (!Within(firstParameter, tolerance / Math.Max(first.Length, tolerance))
        || !Within(secondParameter, tolerance / Math.Max(second.Length, tolerance)))
      {
        point = null;
        return false;
      }

      point = new CommonLinePoint(first.Start.X + (firstParameter * rx), first.Start.Y + (firstParameter * ry));
      return true;
    }

    private static IEnumerable<Intersection> LineArcIntersections(
      LineWork line,
      ArcWork arc,
      double tolerance)
    {
      double dx = line.End.X - line.Start.X;
      double dy = line.End.Y - line.Start.Y;
      double fx = line.Start.X - arc.Center.X;
      double fy = line.Start.Y - arc.Center.Y;
      double a = (dx * dx) + (dy * dy);
      double b = 2D * ((fx * dx) + (fy * dy));
      double c = (fx * fx) + (fy * fy) - (arc.Radius * arc.Radius);
      double discriminant = (b * b) - (4D * a * c);
      // The quadratic discriminant has units of length^4. Its tolerance must scale the same way;
      // comparing it with tolerance*length (length^2) made physically identical inch and metric
      // drawings disagree near tangency. Expanding the accepted circle by the spatial tolerance
      // gives the exact length^4 allowance for a line just outside the nominal radius.
      double radialAllowanceSquared = (2D * arc.Radius * tolerance) + (tolerance * tolerance);
      double discTolerance = 4D * a * radialAllowanceSquared;
      if (discriminant < -discTolerance)
      {
        yield break;
      }

      discriminant = Math.Max(0D, discriminant);
      double root = Math.Sqrt(discriminant);
      var parameters = new[] { (-b - root) / (2D * a), (-b + root) / (2D * a) };
      double last = double.NaN;
      foreach (double lineParameter in parameters.OrderBy(value => value))
      {
        if ((!double.IsNaN(last) && Math.Abs(lineParameter - last) <= tolerance / Math.Max(line.Length, tolerance))
          || !Within(lineParameter, tolerance / Math.Max(line.Length, tolerance)))
        {
          continue;
        }

        var point = new CommonLinePoint(line.Start.X + (lineParameter * dx), line.Start.Y + (lineParameter * dy));
        if (arc.TryParameter(point, tolerance, out double arcParameter))
        {
          yield return new Intersection(lineParameter, arcParameter, point);
          last = lineParameter;
        }
      }
    }

    private static IEnumerable<Intersection> ArcArcIntersections(
      ArcWork first,
      ArcWork second,
      double tolerance)
    {
      double dx = second.Center.X - first.Center.X;
      double dy = second.Center.Y - first.Center.Y;
      double distance = Math.Sqrt((dx * dx) + (dy * dy));
      if (distance <= tolerance
        || distance > first.Radius + second.Radius + tolerance
        || distance < Math.Abs(first.Radius - second.Radius) - tolerance)
      {
        yield break;
      }

      double along = ((first.Radius * first.Radius) - (second.Radius * second.Radius) + (distance * distance)) / (2D * distance);
      double heightSquared = (first.Radius * first.Radius) - (along * along);
      if (heightSquared < -tolerance * Math.Max(first.Radius, second.Radius))
      {
        yield break;
      }

      double height = Math.Sqrt(Math.Max(0D, heightSquared));
      double baseX = first.Center.X + (along * dx / distance);
      double baseY = first.Center.Y + (along * dy / distance);
      double offsetX = -dy * height / distance;
      double offsetY = dx * height / distance;
      var points = new[]
      {
        new CommonLinePoint(baseX + offsetX, baseY + offsetY),
        new CommonLinePoint(baseX - offsetX, baseY - offsetY),
      };
      CommonLinePoint prior = null;
      foreach (CommonLinePoint point in points)
      {
        if (prior != null && Distance(prior, point) <= tolerance)
        {
          continue;
        }

        if (first.TryParameter(point, tolerance, out double firstParameter)
          && second.TryParameter(point, tolerance, out double secondParameter))
        {
          yield return new Intersection(firstParameter, secondParameter, point);
          prior = point;
        }
      }
    }

    private static string LineKey(CommonLinePoint a, CommonLinePoint b, NodeRegistry nodes)
    {
      NodeKey first = nodes.Key(a);
      NodeKey second = nodes.Key(b);
      if (first.CompareTo(second) > 0)
      {
        NodeKey swap = first;
        first = second;
        second = swap;
      }

      return FormattableString.Invariant($"L:{first.X},{first.Y}:{second.X},{second.Y}");
    }

    private static string ArcKey(CommonCenterCurve curve, NodeRegistry nodes, double tolerance)
    {
      NodeKey center = nodes.Key(curve.Center);
      long radius = checked((long)Math.Round(curve.Radius / tolerance));
      long start = checked((long)Math.Round(NormalizeAngle(curve.StartAngleRadians) * curve.Radius / tolerance));
      long sweep = checked((long)Math.Round(curve.SweepRadians * curve.Radius / tolerance));
      return FormattableString.Invariant($"A:{center.X},{center.Y}:{radius}:{start}:{sweep}");
    }

    private static bool Within(double parameter, double tolerance)
      => parameter >= -tolerance && parameter <= 1D + tolerance;

    private static double NormalizeAngle(double angle)
    {
      double normalized = angle % TwoPi;
      return normalized < 0D ? normalized + TwoPi : normalized;
    }

    private static double PositiveAngle(double from, double to)
      => NormalizeAngle(to - from);

    private static double Distance(CommonLinePoint first, CommonLinePoint second)
    {
      double dx = second.X - first.X;
      double dy = second.Y - first.Y;
      return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double Cross(double ax, double ay, double bx, double by) => (ax * by) - (ay * bx);

    private static CommonLineValidationException Invalid(string message)
      => new CommonLineValidationException(message);

    private readonly struct SplitPoint
    {
      public SplitPoint(double parameter, CommonLinePoint point)
      {
        this.Parameter = parameter;
        this.Point = point;
      }

      public double Parameter { get; }

      public CommonLinePoint Point { get; }
    }

    private readonly struct Intersection
    {
      public Intersection(double firstParameter, double secondParameter, CommonLinePoint point)
      {
        this.FirstParameter = firstParameter;
        this.SecondParameter = secondParameter;
        this.Point = point;
      }

      public double FirstParameter { get; }

      public double SecondParameter { get; }

      public CommonLinePoint Point { get; }
    }

    private readonly struct AngleInterval
    {
      public AngleInterval(double start, double end)
      {
        this.Start = start;
        this.End = end;
      }

      public double Start { get; }

      public double End { get; }
    }

    private sealed class BridgeCandidate
    {
      public BridgeCandidate(LineWork bridge)
      {
        this.Bridge = bridge;
      }

      public LineWork Bridge { get; }
    }

    private sealed class LineWork
    {
      public LineWork(CommonLinePoint start, CommonLinePoint end)
      {
        this.Start = start;
        this.End = end;
        this.Length = Distance(start, end);
      }

      public CommonLinePoint Start { get; }

      public CommonLinePoint End { get; }

      public double Length { get; }

      public static LineWork From(CommonCenterCurve curve, double tolerance)
      {
        if (curve.Start == null || curve.End == null || Distance(curve.Start, curve.End) <= tolerance)
        {
          throw Invalid("COMMON_LINE contains a zero-length or invalid LINE.");
        }

        return new LineWork(curve.Start, curve.End);
      }

      public static bool TryMerge(
        LineWork first,
        LineWork second,
        double maximumGap,
        double tolerance,
        out LineWork merged)
      {
        double ux = (first.End.X - first.Start.X) / first.Length;
        double uy = (first.End.Y - first.Start.Y) / first.Length;
        if (ux < -1e-12 || (Math.Abs(ux) <= 1e-12 && uy < 0D))
        {
          ux = -ux;
          uy = -uy;
        }

        double sdx = second.End.X - second.Start.X;
        double sdy = second.End.Y - second.Start.Y;
        if (Math.Abs(Cross(ux, uy, sdx, sdy)) > tolerance
          || Math.Abs(Cross(second.Start.X - first.Start.X, second.Start.Y - first.Start.Y, ux, uy)) > tolerance
          || Math.Abs(Cross(second.End.X - first.Start.X, second.End.Y - first.Start.Y, ux, uy)) > tolerance)
        {
          merged = null;
          return false;
        }

        double first0 = (first.Start.X * ux) + (first.Start.Y * uy);
        double first1 = (first.End.X * ux) + (first.End.Y * uy);
        double second0 = (second.Start.X * ux) + (second.Start.Y * uy);
        double second1 = (second.End.X * ux) + (second.End.Y * uy);
        double minimum = Math.Min(Math.Min(first0, first1), Math.Min(second0, second1));
        double maximum = Math.Max(Math.Max(first0, first1), Math.Max(second0, second1));
        double between = Math.Max(Math.Min(first0, first1), Math.Min(second0, second1))
          - Math.Min(Math.Max(first0, first1), Math.Max(second0, second1));
        if (between > maximumGap)
        {
          merged = null;
          return false;
        }

        double anchor = (first.Start.X * ux) + (first.Start.Y * uy);
        merged = new LineWork(
          new CommonLinePoint(first.Start.X + ((minimum - anchor) * ux), first.Start.Y + ((minimum - anchor) * uy)),
          new CommonLinePoint(first.Start.X + ((maximum - anchor) * ux), first.Start.Y + ((maximum - anchor) * uy)));
        return true;
      }

      public static bool TryCreateBridge(
        LineWork first,
        LineWork second,
        double maximumGap,
        double tolerance,
        out LineWork bridge)
      {
        double ux = (first.End.X - first.Start.X) / first.Length;
        double uy = (first.End.Y - first.Start.Y) / first.Length;
        if (ux < -1e-12 || (Math.Abs(ux) <= 1e-12 && uy < 0D))
        {
          ux = -ux;
          uy = -uy;
        }

        double sdx = second.End.X - second.Start.X;
        double sdy = second.End.Y - second.Start.Y;
        if (Math.Abs(Cross(ux, uy, sdx, sdy)) > tolerance
          || Math.Abs(Cross(second.Start.X - first.Start.X, second.Start.Y - first.Start.Y, ux, uy)) > tolerance
          || Math.Abs(Cross(second.End.X - first.Start.X, second.End.Y - first.Start.Y, ux, uy)) > tolerance)
        {
          bridge = null;
          return false;
        }

        double first0 = (first.Start.X * ux) + (first.Start.Y * uy);
        double first1 = (first.End.X * ux) + (first.End.Y * uy);
        double second0 = (second.Start.X * ux) + (second.Start.Y * uy);
        double second1 = (second.End.X * ux) + (second.End.Y * uy);
        double firstMinimum = Math.Min(first0, first1);
        double firstMaximum = Math.Max(first0, first1);
        double secondMinimum = Math.Min(second0, second1);
        double secondMaximum = Math.Max(second0, second1);
        double bridgeStart;
        double bridgeEnd;
        if (firstMaximum < secondMinimum)
        {
          bridgeStart = firstMaximum;
          bridgeEnd = secondMinimum;
        }
        else if (secondMaximum < firstMinimum)
        {
          bridgeStart = secondMaximum;
          bridgeEnd = firstMinimum;
        }
        else
        {
          bridge = null;
          return false;
        }

        double gap = bridgeEnd - bridgeStart;
        if (gap <= tolerance || gap > maximumGap)
        {
          bridge = null;
          return false;
        }

        double anchor = (first.Start.X * ux) + (first.Start.Y * uy);
        bridge = new LineWork(
          new CommonLinePoint(
            first.Start.X + ((bridgeStart - anchor) * ux),
            first.Start.Y + ((bridgeStart - anchor) * uy)),
          new CommonLinePoint(
            first.Start.X + ((bridgeEnd - anchor) * ux),
            first.Start.Y + ((bridgeEnd - anchor) * uy)));
        return true;
      }
    }

    private sealed class ArcWork
    {
      public ArcWork(CommonLinePoint center, double radius, double startAngle, double sweep)
      {
        this.Center = center;
        this.Radius = radius;
        this.StartAngle = NormalizeAngle(startAngle);
        this.Sweep = sweep;
      }

      public CommonLinePoint Center { get; }

      public double Radius { get; }

      public double StartAngle { get; }

      public double Sweep { get; }

      public double Length => this.Radius * this.Sweep;

      public static ArcWork From(CommonCenterCurve curve, double tolerance)
      {
        if (curve.Center == null
          || !double.IsFinite(curve.Radius)
          || curve.Radius <= tolerance
          || !double.IsFinite(curve.StartAngleRadians)
          || !double.IsFinite(curve.SweepRadians)
          || Math.Abs(curve.SweepRadians) * curve.Radius <= tolerance
          || Math.Abs(curve.SweepRadians) > TwoPi + (tolerance / curve.Radius))
        {
          throw Invalid("COMMON_LINE contains a zero-length or invalid circular ARC.");
        }

        double start = curve.StartAngleRadians;
        double sweep = curve.SweepRadians;
        if (sweep < 0D)
        {
          start += sweep;
          sweep = -sweep;
        }

        return new ArcWork(curve.Center, curve.Radius, start, Math.Min(TwoPi, sweep));
      }

      public CommonLinePoint PointAt(double parameter)
      {
        double angle = this.StartAngle + (this.Sweep * parameter);
        return new CommonLinePoint(
          this.Center.X + (this.Radius * Math.Cos(angle)),
          this.Center.Y + (this.Radius * Math.Sin(angle)));
      }

      public bool TryParameter(CommonLinePoint point, double tolerance, out double parameter)
      {
        double radial = Distance(this.Center, point);
        if (Math.Abs(radial - this.Radius) > tolerance)
        {
          parameter = 0D;
          return false;
        }

        double angle = Math.Atan2(point.Y - this.Center.Y, point.X - this.Center.X);
        double delta = PositiveAngle(this.StartAngle, angle);
        double angularTolerance = tolerance / Math.Max(this.Radius, tolerance);
        if (this.Sweep >= TwoPi - angularTolerance)
        {
          parameter = delta / this.Sweep;
          return true;
        }

        if (delta > this.Sweep + angularTolerance)
        {
          parameter = 0D;
          return false;
        }

        parameter = Math.Max(0D, Math.Min(1D, delta / this.Sweep));
        return true;
      }

      public IEnumerable<AngleInterval> Intervals()
      {
        if (this.Sweep >= TwoPi - 1e-12)
        {
          yield return new AngleInterval(0D, TwoPi);
          yield break;
        }

        double end = this.StartAngle + this.Sweep;
        if (end <= TwoPi)
        {
          yield return new AngleInterval(this.StartAngle, end);
        }
        else
        {
          yield return new AngleInterval(this.StartAngle, TwoPi);
          yield return new AngleInterval(0D, end - TwoPi);
        }
      }
    }

    private readonly struct NodeKey : IComparable<NodeKey>, IEquatable<NodeKey>
    {
      public NodeKey(long x, long y)
      {
        this.X = x;
        this.Y = y;
      }

      public long X { get; }

      public long Y { get; }

      public int CompareTo(NodeKey other)
      {
        int x = this.X.CompareTo(other.X);
        return x != 0 ? x : this.Y.CompareTo(other.Y);
      }

      public bool Equals(NodeKey other) => this.X == other.X && this.Y == other.Y;

      public override bool Equals(object obj) => obj is NodeKey other && this.Equals(other);

      public override int GetHashCode() => HashCode.Combine(this.X, this.Y);
    }

    private sealed class NodeRegistry
    {
      private readonly Dictionary<NodeKey, List<CommonLinePoint>> points
        = new Dictionary<NodeKey, List<CommonLinePoint>>();
      private readonly int neighborRadius;
      private readonly double scale;
      private readonly double toleranceSquared;

      public NodeRegistry(double tolerance)
      {
        this.scale = Math.Min(1_000_000_000D, Math.Max(1D, Math.Ceiling(1D / tolerance)));
        double requiredRadius = Math.Ceiling(tolerance * this.scale) + 1D;
        this.neighborRadius = requiredRadius <= 8D ? Math.Max(1, (int)requiredRadius) : 9;
        this.toleranceSquared = tolerance * tolerance;
      }

      public NodeKey Key(CommonLinePoint point)
        => new NodeKey(
          checked((long)Math.Round(point.X * this.scale)),
          checked((long)Math.Round(point.Y * this.scale)));

      public CommonLinePoint Canonical(CommonLinePoint point)
      {
        NodeKey key = this.Key(point);
        IEnumerable<NodeKey> neighboringKeys = this.neighborRadius <= 8
          ? Enumerable.Range(-this.neighborRadius, (2 * this.neighborRadius) + 1)
            .SelectMany(dx => Enumerable.Range(-this.neighborRadius, (2 * this.neighborRadius) + 1)
              .Select(dy => new NodeKey(unchecked(key.X + dx), unchecked(key.Y + dy))))
          : this.points.Keys.OrderBy(candidate => candidate.X).ThenBy(candidate => candidate.Y);
        CommonLinePoint canonical = neighboringKeys
          .Where(candidate => this.points.ContainsKey(candidate))
          .SelectMany(candidate => this.points[candidate]
            .Select(candidatePoint => (Key: candidate, Point: candidatePoint)))
          .Where(candidate =>
          {
            double dx = candidate.Point.X - point.X;
            double dy = candidate.Point.Y - point.Y;
            return (dx * dx) + (dy * dy) <= this.toleranceSquared;
          })
          .OrderBy(candidate =>
          {
            double dx = candidate.Point.X - point.X;
            double dy = candidate.Point.Y - point.Y;
            return (dx * dx) + (dy * dy);
          })
          .ThenBy(candidate => candidate.Key)
          .ThenBy(candidate => candidate.Point.X)
          .ThenBy(candidate => candidate.Point.Y)
          .Select(candidate => candidate.Point)
          .FirstOrDefault();
        if (canonical == null)
        {
          canonical = point;
          if (!this.points.TryGetValue(key, out List<CommonLinePoint> bucket))
          {
            bucket = new List<CommonLinePoint>();
            this.points.Add(key, bucket);
          }

          bucket.Add(canonical);
        }

        return canonical;
      }
    }
  }
}
