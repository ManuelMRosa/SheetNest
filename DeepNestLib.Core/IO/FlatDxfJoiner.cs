namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// Joins the exploded LINE/ARC soup that FreeCAD's DXF exporter writes into CLOSED LWPOLYLINEs
  /// (arcs become bulge segments). Laser CAM and AutoCAD treat loose entities as "open" geometry
  /// even when every endpoint coincides exactly — a cut file must be one closed polyline per
  /// contour. Chains that do not close (or single loose segments) are left untouched rather than
  /// degraded; circles and any other entity types pass through unchanged.
  /// </summary>
  public static class FlatDxfJoiner
  {
    /// <summary>Endpoint match tolerance in drawing units (unfolds arrive with sub-micron joints).</summary>
    private const double Tolerance = 1e-4;

    /// <summary>Rewrites the DXF in place with its LINE/ARC chains joined into closed LWPOLYLINEs.</summary>
    public static void JoinToClosedPolylines(string dxfPath)
    {
      var file = DxfFile.Load(dxfPath);

      // Segments we can chain: lines and arcs. Everything else stays as-is.
      var segments = new List<Segment>();
      foreach (var ent in file.Entities)
      {
        if (ent is DxfLine line)
        {
          segments.Add(new Segment(ent, new DxfPoint(line.P1.X, line.P1.Y, 0), new DxfPoint(line.P2.X, line.P2.Y, 0), 0.0));
        }
        else if (ent is DxfArc arc)
        {
          // A DXF ARC always runs CCW from StartAngle to EndAngle; bulge = tan(sweep/4).
          double sweep = arc.EndAngle - arc.StartAngle;
          while (sweep <= 0)
          {
            sweep += 360.0;
          }

          segments.Add(new Segment(
            ent,
            arc.GetPointFromAngle(arc.StartAngle),
            arc.GetPointFromAngle(arc.EndAngle),
            Math.Tan(sweep * Math.PI / 180.0 / 4.0)));
        }
      }

      if (segments.Count < 2)
      {
        return;
      }

      var polylines = new List<DxfLwPolyline>();
      var consumed = new List<DxfEntity>();
      foreach (var seed in segments)
      {
        if (seed.Used)
        {
          continue;
        }

        // Walk the chain from this seed, flipping segments that arrive end-first.
        seed.Used = true;
        var chain = new List<(DxfPoint Vertex, double Bulge)> { (seed.Start, seed.Bulge) };
        var chainEnts = new List<DxfEntity> { seed.Entity };
        DxfPoint first = seed.Start;
        DxfPoint current = seed.End;
        while (true)
        {
          Segment next = null;
          bool reversed = false;
          foreach (var s in segments)
          {
            if (s.Used)
            {
              continue;
            }

            if (Coincide(s.Start, current))
            {
              next = s;
              reversed = false;
              break;
            }

            if (Coincide(s.End, current))
            {
              next = s;
              reversed = true;
              break;
            }
          }

          if (next == null)
          {
            break;
          }

          next.Used = true;
          chainEnts.Add(next.Entity);
          chain.Add((current, reversed ? -next.Bulge : next.Bulge));
          current = reversed ? next.Start : next.End;
        }

        // Only a closed multi-segment loop becomes a polyline; anything else keeps its
        // original entities (never degrade a cut file we cannot prove closed).
        if (chain.Count >= 2 && Coincide(current, first))
        {
          var poly = new DxfLwPolyline(chain.Select(v => new DxfLwPolylineVertex { X = v.Vertex.X, Y = v.Vertex.Y, Bulge = v.Bulge }))
          {
            IsClosed = true,
            Layer = seed.Entity.Layer,
          };
          polylines.Add(poly);
          consumed.AddRange(chainEnts);
        }
      }

      if (polylines.Count == 0)
      {
        return;
      }

      foreach (var ent in consumed)
      {
        file.Entities.Remove(ent);
      }

      foreach (var poly in polylines)
      {
        file.Entities.Add(poly);
      }

      // FreeCAD writes R12, which cannot hold LWPOLYLINE — save as R2000 (same as the exporter).
      if (file.Header.Version < DxfAcadVersion.R2000)
      {
        file.Header.Version = DxfAcadVersion.R2000;
      }

      file.Save(dxfPath, true);
    }

    private static bool Coincide(DxfPoint a, DxfPoint b)
      => Math.Abs(a.X - b.X) <= Tolerance && Math.Abs(a.Y - b.Y) <= Tolerance;

    private sealed class Segment
    {
      public Segment(DxfEntity entity, DxfPoint start, DxfPoint end, double bulge)
      {
        Entity = entity;
        Start = start;
        End = end;
        Bulge = bulge;
      }

      public DxfEntity Entity { get; }

      public DxfPoint Start { get; }

      public DxfPoint End { get; }

      /// <summary>Bulge when traversed start→end; negate when traversed backwards.</summary>
      public double Bulge { get; }

      public bool Used { get; set; }
    }
  }
}
