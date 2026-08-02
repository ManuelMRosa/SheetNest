namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.Drawing;
  using System.IO;
  using System.Linq;
  using System.Reflection;
  using System.Threading.Tasks;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Blocks;
  using IxMilia.Dxf.Entities;

  public class DxfParser
  {
    private const int NumberOfRetries = 5;
    private const int DelayOnRetry = 1000;
    private const double RemoveThreshold = 10e-5;
    private const double ClosingThreshold = 10e-2;

    // Curve tessellation: arcs/circles are approximated by line segments. The angular step is
    // chosen so the chord deviation stays within CurveChordTolerance (drawing units), adaptive to
    // radius, and clamped so even tiny holes look smooth and huge arcs don't explode in segments.
    // (Replaces the old hard-coded 15 deg step that turned every circle into a 24-gon.)
    private const double CurveChordTolerance = 0.05;
    private const double MinArcStepDeg = 1.0;
    private const double MaxArcStepDeg = 6.0;

    /// <summary>Entity types that are never a cut path, so dropping them is the right answer: notes,
    /// dimensions, construction lines, raster underlays. Anything NOT here and not tessellated is
    /// reported instead of dropped — silently losing real geometry would nest a part that is missing a
    /// piece, and that ends up as scrap metal.</summary>
    private static readonly HashSet<DxfEntityType> AnnotationTypes = new HashSet<DxfEntityType>
    {
      DxfEntityType.Text, DxfEntityType.MText, DxfEntityType.ArcAlignedText, DxfEntityType.RText,
      DxfEntityType.Attribute, DxfEntityType.AttributeDefinition, DxfEntityType.Dimension,
      DxfEntityType.Leader, DxfEntityType.Tolerance, DxfEntityType.Point, DxfEntityType.Seqend,
      DxfEntityType.Vertex, DxfEntityType.XLine, DxfEntityType.Ray, DxfEntityType.Hatch,
      DxfEntityType.Image, DxfEntityType.WipeOut, DxfEntityType.OleFrame, DxfEntityType.Ole2Frame,
      DxfEntityType.DgnUnderlay, DxfEntityType.DwfUnderlay, DxfEntityType.PdfUnderlay,
      DxfEntityType.Underlay, DxfEntityType.Light, DxfEntityType.Shape, DxfEntityType.Section,
    };

    private static double ArcStepDegrees(double radius)
    {
      if (radius <= 0)
      {
        return MaxArcStepDeg;
      }

      double cosHalf = 1.0 - (CurveChordTolerance / radius);
      cosHalf = Math.Max(-1.0, Math.Min(1.0, cosHalf));
      double stepDeg = 2.0 * Math.Acos(cosHalf) * 180.0 / Math.PI;
      return Math.Max(MinArcStepDeg, Math.Min(MaxArcStepDeg, stepDeg));
    }

    /// <summary>
    /// Intermediate arc points for a polyline "bulge" segment (DXF encodes arcs inside polylines as
    /// bulge = tan(includedAngle/4) on the start vertex). Returns the points strictly between the two
    /// vertices; the vertices themselves are added by the caller. A zero bulge yields nothing (straight).
    /// </summary>
    private static IEnumerable<PointF> BulgePoints(double x1, double y1, double x2, double y2, double bulge)
    {
      if (Math.Abs(bulge) < 1e-9)
      {
        yield break;
      }

      double theta = 4.0 * Math.Atan(bulge); // signed included angle
      double dx = x2 - x1;
      double dy = y2 - y1;
      double chord = Math.Sqrt((dx * dx) + (dy * dy));
      double sinHalf = Math.Sin(theta / 2.0);
      if (chord < 1e-9 || Math.Abs(sinHalf) < 1e-12)
      {
        yield break;
      }

      double radius = chord / (2.0 * sinHalf); // signed
      double mx = (x1 + x2) / 2.0;
      double my = (y1 + y2) / 2.0;
      double apothem = radius * Math.Cos(theta / 2.0); // signed
      double ux = -dy / chord; // left perpendicular to the chord
      double uy = dx / chord;
      double cx = mx + (apothem * ux);
      double cy = my + (apothem * uy);
      double absR = Math.Abs(radius);
      double a1 = Math.Atan2(y1 - cy, x1 - cx);
      double stepRad = ArcStepDegrees(absR) * Math.PI / 180.0;
      int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(theta) / stepRad));
      double da = theta / n;
      for (int k = 1; k < n; k++)
      {
        double a = a1 + (da * k);
        yield return new PointF((float)(cx + (absR * Math.Cos(a))), (float)(cy + (absR * Math.Sin(a))));
      }
    }

    private static volatile object loadLock = new object();

    public static async Task<IRawDetail> LoadDxfFile(string path)
    {
      FileInfo fi = new FileInfo(path);
      DxfFile dxffile;
      for (var i = 1; i <= NumberOfRetries; ++i)
      {
        try
        {
          lock (loadLock)
          {
            dxffile = DxfFile.Load(fi.FullName);
            IEnumerable<DxfEntity> entities = dxffile.Entities.ToArray();
            return ConvertDxfToRawDetail(fi.FullName, entities);
          }
        }
        catch (IOException) when (i <= NumberOfRetries)
        {
          await Task.Delay(DelayOnRetry);
        }
        catch (IOException)
        {
          throw;
        }
      }

      return default;
    }

    public static RawDetail<DxfEntity> ConvertDxfToRawDetail(string fullFilename, IEnumerable<DxfEntity> entities)
    {
      RawDetail<DxfEntity> s = new RawDetail<DxfEntity>();
      s.Name = fullFilename;
      Dictionary<DxfEntity, IList<LineElement>> approximations = ApproximateEntities(entities);

      // Annotation is now dropped rather than fatal, so a drawing can reach here with nothing to cut.
      // Say that plainly: ConnectElements would otherwise fail on an empty contour list, and "Sequence
      // contains no elements" tells the operator nothing about their file.
      if (!approximations.Any(kvp => kvp.Value.Count > 0))
      {
        throw new ArgumentException($"'{fullFilename}' has no geometry to nest - it holds only text, dimensions or other annotation.");
      }

      s.AddRangeContour(ConnectElements(approximations));
      if (s.Outers.Any(z => z.Points.Count < 3))
      {
        throw new Exception("Too few points");
      }

      return s;
    }

    private static ArgumentException UnmeasurableGeometry(DxfEntity ent)
    {
      return new ArgumentException($"This drawing contains a {ent.EntityTypeString}, which SheetNest cannot measure. Explode it or redraw it as lines, arcs, polylines, ellipses or splines and import again.");
    }

    private static Dictionary<DxfEntity, IList<LineElement>> ApproximateEntities(IEnumerable<DxfEntity> entities)
    {
      var approximations = new Dictionary<DxfEntity, IList<LineElement>>();

      foreach (DxfEntity ent in entities)
      {
        var elems = new List<LineElement>();
        switch (ent.EntityType)
        {
          case DxfEntityType.LwPolyline:
            {
              DxfLwPolyline poly = (DxfLwPolyline)ent;
              if (poly.Vertices.Count() < 2)
              {
                continue;
              }

              var localContour = new List<PointF>();
              var curvedSegs = new List<bool>();   // one per segment, aligned with the START point
              var verts = poly.Vertices.ToList();
              for (int vi = 0; vi < verts.Count; vi++)
              {
                var vert = verts[vi];
                localContour.Add(new PointF((float)vert.X, (float)vert.Y));

                // If this vertex carries a bulge, the segment to the next vertex is an arc.
                bool hasNext = vi < verts.Count - 1 || poly.IsClosed;
                if (hasNext && Math.Abs(vert.Bulge) > 1e-9)
                {
                  var next = verts[(vi + 1) % verts.Count];
                  var bulge = BulgePoints(vert.X, vert.Y, next.X, next.Y, vert.Bulge).ToList();
                  curvedSegs.Add(true);   // this vertex into the first sample of the arc
                  foreach (var bp in bulge)
                  {
                    localContour.Add(bp);
                    curvedSegs.Add(true); // and every sample onward, up to the next vertex
                  }
                }
                else
                {
                  curvedSegs.Add(false);  // a straight run to the next vertex
                }
              }

              elems.AddRange(ConnectTheDots(localContour, curvedSegs).ToList());
            }

            break;
          case DxfEntityType.Arc:
            {
              DxfArc arc = (DxfArc)ent;
              List<PointF> pp = new List<PointF>();

              if (arc.StartAngle > arc.EndAngle)
              {
                arc.StartAngle -= 360;
              }

              double arcStep = ArcStepDegrees(arc.Radius);
              for (var i = arc.StartAngle; i < arc.EndAngle; i += arcStep)
              {
                var tt = arc.GetPointFromAngle(i);
                pp.Add(new PointF((float)tt.X, (float)tt.Y));
              }

              var t = arc.GetPointFromAngle(arc.EndAngle);
              pp.Add(new PointF((float)t.X, (float)t.Y));
              for (var j = 1; j < pp.Count; j++)
              {
                var p1 = pp[j - 1];
                var p2 = pp[j];
                elems.Add(new LineElement() { Start = new PointF((float)p1.X, (float)p1.Y), End = new PointF((float)p2.X, (float)p2.Y), Curved = true });
              }
            }

            break;
          case DxfEntityType.Circle:
            {
              DxfCircle cr = (DxfCircle)ent;
              var cc = new List<PointF>();

              double circleStep = ArcStepDegrees(cr.Radius);
              for (double i = 0; i < 360; i += circleStep)
              {
                var ang = i * Math.PI / 180f;
                var xx = cr.Center.X + cr.Radius * Math.Cos(ang);
                var yy = cr.Center.Y + cr.Radius * Math.Sin(ang);
                cc.Add(new PointF((float)xx, (float)yy));
              }

              // Ensure the ring closes back on the first point.
              cc.Add(cc[0]);

              elems.AddRange(ConnectTheDots(cc, Enumerable.Repeat(true, cc.Count).ToList()));
            }

            break;
          case DxfEntityType.Line:
            {
              DxfLine poly = (DxfLine)ent;
              elems.Add(new LineElement() { Start = new PointF((float)poly.P1.X, (float)poly.P1.Y), End = new PointF((float)poly.P2.X, (float)poly.P2.Y) });
              break;
            }

          case DxfEntityType.Polyline:
            {
              DxfPolyline poly = (DxfPolyline)ent;
              if (poly.Vertices.Count() < 2)
              {
                continue;
              }

              var localContour = new List<PointF>();
              var pcurved = new List<bool>();
              var pverts = poly.Vertices.ToList();
              for (int vi = 0; vi < pverts.Count; vi++)
              {
                var vert = pverts[vi];
                localContour.Add(new PointF((float)vert.Location.X, (float)vert.Location.Y));

                bool hasNext = vi < pverts.Count - 1 || poly.IsClosed;
                if (hasNext && Math.Abs(vert.Bulge) > 1e-9)
                {
                  var next = pverts[(vi + 1) % pverts.Count];
                  var bulge = BulgePoints(vert.Location.X, vert.Location.Y, next.Location.X, next.Location.Y, vert.Bulge).ToList();
                  pcurved.Add(true);
                  foreach (var bp in bulge)
                  {
                    localContour.Add(bp);
                    pcurved.Add(true);
                  }
                }
                else
                {
                  pcurved.Add(false);
                }
              }

              elems.AddRange(ConnectTheDots(localContour, pcurved));

              break;
            }

          case DxfEntityType.Ellipse:
            {
              DxfEllipse ellipse = (DxfEllipse)ent;
              double ax = ellipse.MajorAxis.X;
              double ay = ellipse.MajorAxis.Y;
              double major = Math.Sqrt((ax * ax) + (ay * ay));
              if (major < 1e-12)
              {
                continue;
              }

              double ratio = Math.Abs(ellipse.MinorAxisRatio);
              double from = ellipse.StartParameter;
              double to = ellipse.EndParameter;
              if (to <= from)
              {
                to = from + (2 * Math.PI); // a full ellipse, which is also what an unset sweep means
              }

              double ellipseStep = ArcStepDegrees(major) * Math.PI / 180.0;
              int steps = Math.Max(2, (int)Math.Ceiling((to - from) / ellipseStep));
              var ee = new List<PointF>();
              for (int i = 0; i <= steps; i++)
              {
                double t = from + ((to - from) * i / steps);
                double c = Math.Cos(t);
                double s = Math.Sin(t);

                // The minor axis is the major one turned a quarter turn and shortened by the ratio.
                ee.Add(new PointF(
                  (float)(ellipse.Center.X + (ax * c) - (ay * ratio * s)),
                  (float)(ellipse.Center.Y + (ay * c) + (ax * ratio * s))));
              }

              elems.AddRange(Chain(ee, curved: true));
            }

            break;
          case DxfEntityType.Spline:
            {
              List<PointF> sp = SplinePoints((DxfSpline)ent);
              if (sp.Count < 2)
              {
                continue;
              }

              elems.AddRange(Chain(sp, curved: true));
            }

            break;
          case DxfEntityType.Insert:
            {
              // A part drawn as a block is not nested. Every nesting package in the trade expects the
              // blocks exploded before import, and the ones that do not say so read the part in EMPTY or
              // drop the reference in silence - a sheet that gets cut with a piece missing. Say it instead.
              throw new ArgumentException("This file is not compatible with SheetNest: its geometry is drawn as a block (a block reference). Open it in your CAD program, explode the blocks, and save the DXF again.");
            }

          default:
            {
              // Annotation draws nothing that gets cut, so dropping it is right. Anything else might be
              // real geometry, and quietly losing that would nest a part with a piece missing.
              if (!AnnotationTypes.Contains(ent.EntityType))
              {
                throw UnmeasurableGeometry(ent);
              }

              continue;
            }
        }

        elems = elems.Where(z => z.Start.DistTo(z.End) > RemoveThreshold).ToList();
        approximations.Add(ent, elems);
      }

      return approximations;
    }

    internal static RawDetail<DxfEntity> LoadDxfFileStreamAsRawDetail(string path)
    {
      using (var inputStream = Assembly.GetExecutingAssembly().GetEmbeddedResourceStream(path))
      {
        return LoadDxfStream(path, inputStream);
      }
    }

    internal static INfp LoadDxfFileStreamAsNfp(string path)
    {
      using (var inputStream = Assembly.GetExecutingAssembly().GetEmbeddedResourceStream(path))
      {
        return LoadDxfStream(path, inputStream).ToNfp();
      }
    }

    internal static DxfFile LoadDxfFileStream(string path)
    {
      using (var inputStream = Assembly.GetExecutingAssembly().GetEmbeddedResourceStream(path))
      {
        return DxfFile.Load(inputStream);
      }
    }

    internal static RawDetail<DxfEntity> LoadDxfStream(string name, Stream inputStream)
    {
      DxfFile dxffile = DxfFile.Load(inputStream);
      IEnumerable<DxfEntity> entities = dxffile.Entities.ToArray();
      return ConvertDxfToRawDetail(name, entities);
    }

    /// <summary>Segments joining the points in the order given. Unlike <see cref="ConnectTheDots"/> this
    /// does not wrap the last point back to the first, so a partial sweep stays open.</summary>
    private static IEnumerable<LineElement> Chain(IList<PointF> points, bool curved = false)
    {
      for (var i = 1; i < points.Count; i++)
      {
        yield return new LineElement() { Start = points[i - 1], End = points[i], Curved = curved };
      }
    }

    /// <summary>
    /// Samples a SPLINE finely enough that it strays less than <see cref="CurveChordTolerance"/> from the
    /// true curve - the same accuracy arcs and circles get. Control point weights are honoured: a rational
    /// spline is how a DXF stores an exact circle or ellipse in spline form, and ignoring the weights bows
    /// the curve visibly inwards.
    /// </summary>
    private static List<PointF> SplinePoints(DxfSpline spline)
    {
      IList<DxfControlPoint> control = spline.ControlPoints;
      int degree = Math.Max(1, spline.DegreeOfCurve);
      IList<double> knots = spline.KnotValues;

      // Fit points are the points the curve was drawn THROUGH, so they are a faithful fallback when the
      // knot vector is missing or inconsistent. Inventing a knot vector would draw a different curve.
      if (control.Count < degree + 1 || knots.Count != control.Count + degree + 1)
      {
        if (spline.FitPoints.Count >= 2)
        {
          return spline.FitPoints.Select(p => new PointF((float)p.X, (float)p.Y)).ToList();
        }

        throw new ArgumentException($"This drawing contains a SPLINE whose {control.Count} control points and {knots.Count} knots do not describe a curve. Convert it to a polyline and import again.");
      }

      double first = knots[degree];
      double last = knots[control.Count];
      if (last - first < 1e-12)
      {
        return new List<PointF>();
      }

      // Double the sampling until the curve's own midpoints sit within tolerance of the chords being
      // emitted. A handful of extra evaluations, and it adapts to how much the spline actually bends.
      int segments = 8 * Math.Max(1, control.Count - degree);
      List<PointF> points = SampleSpline(spline, knots, degree, first, last, segments);
      for (int attempt = 0; attempt < 5 && !IsFlatEnough(spline, knots, degree, first, last, segments, points); attempt++)
      {
        segments *= 2;
        points = SampleSpline(spline, knots, degree, first, last, segments);
      }

      return points;
    }

    private static List<PointF> SampleSpline(DxfSpline spline, IList<double> knots, int degree, double first, double last, int segments)
    {
      var points = new List<PointF>(segments + 1);
      for (int i = 0; i <= segments; i++)
      {
        points.Add(SplinePoint(spline, knots, degree, first + ((last - first) * i / segments)));
      }

      return points;
    }

    /// <summary>Compares the curve at the middle of every emitted chord against that chord's midpoint.</summary>
    private static bool IsFlatEnough(DxfSpline spline, IList<double> knots, int degree, double first, double last, int segments, List<PointF> points)
    {
      for (int i = 1; i < points.Count; i++)
      {
        PointF mid = SplinePoint(spline, knots, degree, first + ((last - first) * (i - 0.5) / segments));
        double dx = mid.X - ((points[i - 1].X + points[i].X) / 2.0);
        double dy = mid.Y - ((points[i - 1].Y + points[i].Y) / 2.0);
        if (Math.Sqrt((dx * dx) + (dy * dy)) > CurveChordTolerance)
        {
          return false;
        }
      }

      return true;
    }

    /// <summary>De Boor's algorithm, run in homogeneous coordinates so the weights come out for free.</summary>
    private static PointF SplinePoint(DxfSpline spline, IList<double> knots, int degree, double u)
    {
      IList<DxfControlPoint> control = spline.ControlPoints;
      int span = degree;
      while (span < control.Count - 1 && u >= knots[span + 1])
      {
        span++;
      }

      var x = new double[degree + 1];
      var y = new double[degree + 1];
      var w = new double[degree + 1];
      for (int j = 0; j <= degree; j++)
      {
        DxfControlPoint cp = control[span - degree + j];
        double weight = cp.Weight > 0 ? cp.Weight : 1; // some writers leave an unused weight at zero
        x[j] = cp.Point.X * weight;
        y[j] = cp.Point.Y * weight;
        w[j] = weight;
      }

      for (int r = 1; r <= degree; r++)
      {
        for (int j = degree; j >= r; j--)
        {
          int i = span - degree + j;
          double denominator = knots[i + degree - r + 1] - knots[i];
          double a = denominator <= 0 ? 0 : (u - knots[i]) / denominator;
          x[j] = ((1 - a) * x[j - 1]) + (a * x[j]);
          y[j] = ((1 - a) * y[j - 1]) + (a * y[j]);
          w[j] = ((1 - a) * w[j - 1]) + (a * w[j]);
        }
      }

      double divisor = Math.Abs(w[degree]) < 1e-12 ? 1 : w[degree];
      return new PointF((float)(x[degree] / divisor), (float)(y[degree] / divisor));
    }

    /// <summary>
    /// Returns a series of LineElements to connect the points passed in.
    /// </summary>
    /// <param name="points">List of <see cref="PointF"/> to join.</param>
    /// <returns>List of <see cref="LineElement"/> connecting the dots.</returns>
    /// <param name="curved">Per-segment "this is a chord of a curve" flags, aligned with the START
    /// index. Null means none of them are.</param>
    private static IEnumerable<LineElement> ConnectTheDots(IList<PointF> points, IList<bool> curved = null)
    {
      for (var i = 0; i < points.Count; i++)
      {
        var p0 = points[i];
        var p1 = points[(i + 1) % points.Count];
        yield return new LineElement() { Start = p0, End = p1, Curved = curved != null && i < curved.Count && curved[i] };
      }
    }

    private static LocalContour<DxfEntity>[] ConnectElements(Dictionary<DxfEntity, IList<LineElement>> approximations)
    {
      List<(DxfEntity Entity, LineElement LineElement)> allLineElements = GetAllLineElements(approximations);

      PointF prior = default;
      List<PointF> newContourPoints = new List<PointF>();
      var newContourEntities = new HashSet<DxfEntity>();

      // Built in lockstep with the points: entry i is the segment from point i to point i + 1, so it is
      // appended at the same moment as point i + 1. The last entry is the segment that wraps back to the
      // start, which no entity produced - this chainer invents it, joining across a gap of up to
      // ClosingThreshold - so it is marked as a curve and can never become a shared cut.
      var newContourCurved = new List<bool>();

      var result = new List<LocalContour<DxfEntity>>();

      void CloseContour()
      {
        newContourCurved.Add(true); // the invented closing segment
        result.Add(new LocalContour<DxfEntity>(newContourPoints.ToList(), newContourEntities, newContourCurved.ToList()));
        newContourPoints = new List<PointF>();
        newContourEntities = new HashSet<DxfEntity>();
        newContourCurved = new List<bool>();
      }

      while (allLineElements.Any())
      {
        if (newContourPoints.Count == 0)
        {
          var toStart = allLineElements.First().LineElement;
          newContourPoints.Add(toStart.Start);
          prior = toStart.End;
          newContourPoints.Add(prior);
          newContourCurved.Add(toStart.Curved);
          newContourEntities.Add(allLineElements.First().Entity);
          allLineElements.RemoveAt(0);
        }
        else
        {
          if (!TryGetAnotherPoint(prior, allLineElements, out (DxfEntity Entity, LineElement LineElement) next))
          {
            CloseContour();
          }
          else
          {
            allLineElements.Remove(next);
            newContourEntities.Add(next.Entity);
            prior = EndIsClosest(prior, next) ? next.LineElement.End : next.LineElement.Start;
            newContourPoints.Add(prior);
            newContourCurved.Add(next.LineElement.Curved);
          }
        }
      }

      if (newContourPoints.Any())
      {
        CloseContour();
      }

      result.OrderByDescending(o => Math.Abs(Geometry.GeometryUtil.PolygonArea(o.Points))).First().IsChild = false;
      return result.ToArray();
    }

    private static List<(DxfEntity Entity, LineElement LineElement)> GetAllLineElements(Dictionary<DxfEntity, IList<LineElement>> approximations)
    {
      var allLineElements = new List<(DxfEntity Entity, LineElement LineElement)>();
      foreach (KeyValuePair<DxfEntity, IList<LineElement>> kvp in approximations)
      {
        allLineElements.AddRange(kvp.Value.Select(o => (kvp.Key, o)));
      }

      return allLineElements;
    }

    private static bool EndIsClosest(PointF prior, (DxfEntity Entity, LineElement LineElement) next)
    {
      return next.LineElement.Start.DistTo(prior) < next.LineElement.End.DistTo(prior);
    }

    private static bool TryGetAnotherPoint(PointF prior, List<(DxfEntity Entity, LineElement LineElement)> allLineElements, out (DxfEntity Entity, LineElement LineElement) next)
    {
      var match = allLineElements.Select(candidate => (candidate, MinDistance(prior, candidate)))
                       .Where(o => o.Item2 <= ClosingThreshold)
                       .OrderBy(o => o.Item2).FirstOrDefault();
      if (match != default)
      {
        next = match.candidate;
        return true;
      }

      next = default;
      return false;
    }

    private static double MinDistance(PointF prior, (DxfEntity Entity, LineElement LineElement) candidate)
    {
      return Math.Min(candidate.LineElement.Start.DistTo(prior), candidate.LineElement.End.DistTo(prior));
    }
  }
}
