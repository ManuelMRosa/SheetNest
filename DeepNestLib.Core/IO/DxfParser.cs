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
      s.AddRangeContour(ConnectElements(approximations));
      if (s.Outers.Any(z => z.Points.Count < 3))
      {
        throw new Exception("Too few points");
      }

      return s;
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
                  localContour.AddRange(BulgePoints(vert.X, vert.Y, next.X, next.Y, vert.Bulge));
                }
              }

              elems.AddRange(ConnectTheDots(localContour).ToList());
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
                elems.Add(new LineElement() { Start = new PointF((float)p1.X, (float)p1.Y), End = new PointF((float)p2.X, (float)p2.Y) });
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

              elems.AddRange(ConnectTheDots(cc));
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
              var pverts = poly.Vertices.ToList();
              for (int vi = 0; vi < pverts.Count; vi++)
              {
                var vert = pverts[vi];
                localContour.Add(new PointF((float)vert.Location.X, (float)vert.Location.Y));

                bool hasNext = vi < pverts.Count - 1 || poly.IsClosed;
                if (hasNext && Math.Abs(vert.Bulge) > 1e-9)
                {
                  var next = pverts[(vi + 1) % pverts.Count];
                  localContour.AddRange(BulgePoints(vert.Location.X, vert.Location.Y, next.Location.X, next.Location.Y, vert.Bulge));
                }
              }

              elems.AddRange(ConnectTheDots(localContour));

              break;
            }

          default:
            throw new ArgumentException("unsupported entity type: " + ent);
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

    /// <summary>
    /// Returns a series of LineElements to connect the points passed in.
    /// </summary>
    /// <param name="points">List of <see cref="PointF"/> to join.</param>
    /// <returns>List of <see cref="LineElement"/> connecting the dots.</returns>
    private static IEnumerable<LineElement> ConnectTheDots(IList<PointF> points)
    {
      for (var i = 0; i < points.Count; i++)
      {
        var p0 = points[i];
        var p1 = points[(i + 1) % points.Count];
        yield return new LineElement() { Start = p0, End = p1 };
      }
    }

    private static LocalContour<DxfEntity>[] ConnectElements(Dictionary<DxfEntity, IList<LineElement>> approximations)
    {
      List<(DxfEntity Entity, LineElement LineElement)> allLineElements = GetAllLineElements(approximations);

      PointF prior = default;
      List<PointF> newContourPoints = new List<PointF>();
      var newContourEntities = new HashSet<DxfEntity>();
      var result = new List<LocalContour<DxfEntity>>();
      while (allLineElements.Any())
      {
        if (newContourPoints.Count == 0)
        {
          var toStart = allLineElements.First().LineElement;
          newContourPoints.Add(toStart.Start);
          prior = toStart.End;
          newContourPoints.Add(prior);
          newContourEntities.Add(allLineElements.First().Entity);
          allLineElements.RemoveAt(0);
        }
        else
        {
          if (!TryGetAnotherPoint(prior, allLineElements, out (DxfEntity Entity, LineElement LineElement) next))
          {
            result.Add(new LocalContour<DxfEntity>(newContourPoints.ToList(), newContourEntities));
            newContourPoints = new List<PointF>();
            newContourEntities = new HashSet<DxfEntity>();
          }
          else
          {
            allLineElements.Remove(next);
            newContourEntities.Add(next.Entity);
            prior = EndIsClosest(prior, next) ? next.LineElement.End : next.LineElement.Start;
            newContourPoints.Add(prior);
          }
        }
      }

      if (newContourPoints.Any())
      {
        result.Add(new LocalContour<DxfEntity>(newContourPoints.ToList(), newContourEntities));
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