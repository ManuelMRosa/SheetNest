namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  public class DxfLineMerger
  {
    private const decimal Tolerance = 0.0001M;

    internal static MergeLine GetCombined(MergeLine a, MergeLine b)
    {
      if (!Coincident(a, b) || !Coaligned(a, b))
      {
        throw new InvalidOperationException("Mergelines are not coincident.");
      }

      if (b.IsVertical)
      {
        var minY = Math.Min(Math.Min(a.Left.Y, a.Right.Y), Math.Min(b.Left.Y, b.Right.Y));
        var maxY = Math.Max(Math.Max(a.Left.Y, a.Right.Y), Math.Max(b.Left.Y, b.Right.Y));
        var min = new DxfPoint(a.Left.X, minY, 0);
        var max = new DxfPoint(a.Left.X, maxY, 0);

        return new MergeLine(new DxfLine(min, max));
      }
      else
      {
        var newY = (double)b.Slope * Math.Max(b.Right.X, a.Right.X) + (double)b.Intercept;
        var right = new DxfPoint(Math.Max(b.Right.X, a.Right.X), newY, 0);
        return new MergeLine(new DxfLine(a.Left, right));
      }
    }

    internal static bool Coincident(MergeLine prior, MergeLine current)
    {
      if (Coaligned(prior, current))
      {
        if (prior.IsVertical)
        {
          return PairCoincident(prior, current) ||
                 PairCoincident(current, prior);
        }
        else
        {
          return current.Left.X >= prior.Left.X &&
                 current.Left.X <= prior.Right.X;
        }
      }

      return false;
    }

    private static bool PairCoincident(MergeLine prior, MergeLine current)
    {
      var result = current.Left.Y >= prior.Left.Y &&
                    current.Left.Y <= prior.Right.Y ||
                   current.Right.Y >= prior.Left.Y &&
                    current.Right.Y <= prior.Right.Y;
      return result;
    }

    internal static bool Coaligned(MergeLine prior, MergeLine current)
    {
      return (current.IsVertical && prior.IsVertical || current.Slope - prior.Slope < Tolerance) &&
             current.Intercept - prior.Intercept < Tolerance;
    }

    internal DxfFile MergeLines(DxfFile dxfFile)
    {
      var result = new DxfFile();
      foreach (var entity in MergeLines(dxfFile.Entities))
      {
        result.Entities.Add(entity);
      }

      return result;
    }

    internal IEnumerable<DxfEntity> MergeLines(IEnumerable<DxfEntity> entities)
    {
      int entityCount;
      do
      {
        entityCount = entities.Count();
        entities = DoMergeLines(entities);
      }
      while (entityCount != entities.Count());
      return entities;
    }

    private IEnumerable<DxfEntity> DoMergeLines(IEnumerable<DxfEntity> entities)
    {
      var result = new List<DxfEntity>();
      var splitEntities = SplitLines(entities);
      result.AddRange(splitEntities.Where(o => !(o is DxfLine)));
      var mergeLines = MergeLines(splitEntities.Where(o => o is DxfLine).Cast<DxfLine>()).ToList();
      result.AddRange(mergeLines);
      return result;
    }

    internal IEnumerable<DxfEntity> SplitLines(IEnumerable<DxfEntity> entities)
    {
      foreach (var entity in entities)
      {
        if (entity is DxfPolyline polyline)
        {
          foreach (var line in Split(polyline))
          {
            yield return line;
          }
        }
        else if (entity is DxfLwPolyline lwPolyline)
        {
          // LWPOLYLINE segments never merged before (only the classic POLYLINE was split), so a
          // polyline source silently disabled common-line merging. Straight segments become lines
          // (mergeable); bulge segments become the equivalent arcs. FlatDxfJoiner re-chains the
          // survivors into closed polylines after the merge.
          foreach (var seg in Split(lwPolyline))
          {
            yield return seg;
          }
        }
        else if (entity is DxfLine line)
        {
          yield return line;
        }
        else
        {
          yield return entity;
        }
      }
    }

    private static IEnumerable<DxfEntity> Split(DxfLwPolyline polyline)
    {
      var verts = polyline.Vertices;
      int count = verts.Count;
      int segments = polyline.IsClosed ? count : count - 1;
      for (int i = 0; i < segments; i++)
      {
        var a = verts[i];
        var b = verts[(i + 1) % count];
        var p1 = new DxfPoint(a.X, a.Y, 0);
        var p2 = new DxfPoint(b.X, b.Y, 0);
        DxfEntity seg;
        if (Math.Abs(a.Bulge) < 1e-12)
        {
          seg = new DxfLine(p1, p2);
        }
        else
        {
          // Bulge -> arc: included angle = 4*atan(bulge); DXF arcs always run CCW start->end.
          double theta = 4.0 * Math.Atan(a.Bulge);
          double chord = Math.Sqrt(((p2.X - p1.X) * (p2.X - p1.X)) + ((p2.Y - p1.Y) * (p2.Y - p1.Y)));
          double radius = Math.Abs(chord / (2.0 * Math.Sin(Math.Abs(theta) / 2.0)));
          double midX = (p1.X + p2.X) / 2.0;
          double midY = (p1.Y + p2.Y) / 2.0;

          // Centre sits perpendicular to the chord: mid + signed-apothem * left-normal — the SAME
          // convention DxfParser.BulgePoints uses. (A minus here put the centre on the wrong side,
          // so corner fillets exported as the COMPLEMENTARY sweep — near-full circles in AutoCAD.)
          double d = radius * Math.Cos(theta / 2.0) * Math.Sign(a.Bulge);
          double nx = -(p2.Y - p1.Y) / chord;
          double ny = (p2.X - p1.X) / chord;
          double cx = midX + (nx * d);
          double cy = midY + (ny * d);

          double angle1 = Math.Atan2(p1.Y - cy, p1.X - cx) * 180.0 / Math.PI;
          double angle2 = Math.Atan2(p2.Y - cy, p2.X - cx) * 180.0 / Math.PI;
          seg = a.Bulge > 0
            ? new DxfArc(new DxfPoint(cx, cy, 0), radius, angle1, angle2)
            : new DxfArc(new DxfPoint(cx, cy, 0), radius, angle2, angle1);
        }

        seg.Layer = polyline.Layer;
        seg.Color = polyline.Color;
        yield return seg;
      }
    }

    internal IEnumerable<DxfLine> MergeLines(IEnumerable<DxfLine> list)
    {
      if (list.Count() <= 1)
      {
        return list;
      }

      var result = new List<MergeLine>();
      foreach (var line in list)
      {
        result.Add(new MergeLine(line));
      }

      var lines = result
        .OrderBy(o => o.Slope)
        .ThenBy(o => o.Intercept)
        .ThenBy(o => o.IsVertical ? o.Left.Y : o.Left.X)
        .ThenBy(o => o.Left.Y).ToList();
      MergeLine prior = null;
      MergeLine current = null;
      for (var i = 0; i < lines.Count; i++)
      {
        if (prior == null)
        {
          prior = lines[i];
        }
        else
        {
          current = lines[i];
          if (Coincident(prior, current))
          {
            lines.Remove(current);
            i--;
            var combined = GetCombined(prior, current);
            lines[i] = combined;
            prior = combined;
          }
          else
          {
            prior = current;
          }
        }
      }

      return lines.Select(o => o.Line);
    }

    private static IEnumerable<DxfLine> Split(DxfPolyline polyline)
    {
      var isFirst = true;
      DxfVertex origin = null;
      DxfVertex prior = null;
      foreach (var point in polyline.Vertices)
      {
        if (isFirst)
        {
          isFirst = false;
          origin = point;
        }
        else
        {
          yield return new DxfLine(prior.Location, point.Location);
        }

        prior = point;
      }

      if (polyline.IsClosed)
      {
        yield return new DxfLine(prior.Location, origin.Location);
      }
    }
  }
}