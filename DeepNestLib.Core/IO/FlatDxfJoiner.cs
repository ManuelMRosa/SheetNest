namespace DeepNestLib.IO
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// Joins exploded LINE/ARC soup (FreeCAD unfolds, many client CAD exports) into CLOSED
  /// LWPOLYLINEs (arcs become bulge segments). Laser CAM and AutoCAD treat loose entities as
  /// "open" geometry even when every endpoint coincides exactly — a cut file must be one closed
  /// polyline per contour. Chains that do not close (or single loose segments) are left untouched
  /// rather than degraded; circles and any other entity types pass through unchanged.
  /// </summary>
  public static class FlatDxfJoiner
  {
    /// <summary>Endpoint match tolerance in drawing units (unfolds arrive with sub-micron joints).</summary>
    private const double Tolerance = 1e-4;

    /// <summary>Rewrites the DXF in place with its LINE/ARC chains joined into closed LWPOLYLINEs.</summary>
    public static void JoinToClosedPolylines(string dxfPath)
    {
      var file = DxfFile.Load(dxfPath);
      if (!JoinEntities(file.Entities))
      {
        return;
      }

      // FreeCAD writes R12, which cannot hold LWPOLYLINE — save as R2000 (same as the exporter).
      if (file.Header.Version < DxfAcadVersion.R2000)
      {
        file.Header.Version = DxfAcadVersion.R2000;
      }

      file.Save(dxfPath, true);
    }

    /// <summary>
    /// Joins the LINE/ARC chains in the list into closed LWPOLYLINEs, in place (consumed segments
    /// removed, polylines appended). Returns true when anything was joined. The whole set is left
    /// untouched when any endpoint junction is ambiguous (3+ segment ends meeting — T joints,
    /// crossings) so a cut contour can never be mis-chained.
    /// </summary>
    public static bool JoinEntities(IList<DxfEntity> entities)
      => JoinEntities(entities, Tolerance);

    /// <summary>
    /// Rewrites closed LINE/ARC chains using a caller-supplied tolerance in the drawing's active
    /// units. Common-line export uses this overload so 0.0001 in and 0.00254 mm remain physically
    /// identical instead of treating the imperial constant as millimetres.
    /// </summary>
    public static bool JoinEntities(IList<DxfEntity> entities, double tolerance)
    {
      if (!double.IsFinite(tolerance) || tolerance <= 0D)
      {
        throw new ArgumentOutOfRangeException(nameof(tolerance));
      }

      // Segments we can chain: lines and arcs. Everything else stays as-is.
      var segments = new List<Segment>();
      foreach (var ent in entities)
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

      // Every endpoint in a grid of cells as wide as the tolerance, so "which ends coincide with this one" looks
      // at nine cells instead of the whole sheet. Both checks below scanned every segment for every end, and
      // a sheet of arc-drawn parts has tens of thousands of them.
      var grid = new EndGrid(segments, tolerance);
      if (segments.Count < 2 || HasAmbiguousJunction(segments, grid, tolerance))
      {
        return false;
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
          // The first unused segment in list order with an end on the current point, its start before its end:
          // what the scan over every segment found, taken from the grid.
          Segment next = null;
          bool reversed = false;
          int nextIndex = int.MaxValue;
          foreach (int i in grid.Near(current))
          {
            var s = segments[i];
            if (s.Used || i >= nextIndex)
            {
              continue;
            }

            if (Coincide(s.Start, current, tolerance))
            {
              next = s;
              nextIndex = i;
              reversed = false;
            }
            else if (Coincide(s.End, current, tolerance))
            {
              next = s;
              nextIndex = i;
              reversed = true;
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
        if (chain.Count >= 2 && Coincide(current, first, tolerance))
        {
          var poly = new DxfLwPolyline(chain.Select(v => new DxfLwPolylineVertex { X = v.Vertex.X, Y = v.Vertex.Y, Bulge = v.Bulge }))
          {
            IsClosed = true,
            Layer = seed.Entity.Layer,
            Color = seed.Entity.Color, // hole contours keep the exporter's differentiating colour
          };
          polylines.Add(poly);
          consumed.AddRange(chainEnts);
        }
      }

      if (polylines.Count == 0)
      {
        return false;
      }

      if (entities is List<DxfEntity> list)
      {
        var gone = new HashSet<DxfEntity>(consumed, ReferenceEqualityComparer.Instance);
        list.RemoveAll(gone.Contains);
      }
      else
      {
        foreach (var ent in consumed)
        {
          entities.Remove(ent);
        }
      }

      foreach (var poly in polylines)
      {
        entities.Add(poly);
      }

      return true;
    }

    /// <summary>True when any junction joins 3+ segment ends — chaining there would be a guess.</summary>
    private static bool HasAmbiguousJunction(List<Segment> segments, EndGrid grid, double tolerance)
    {
      for (int si = 0; si < segments.Count; si++)
      {
        foreach (bool atStart in new[] { true, false })
        {
          DxfPoint end = atStart ? segments[si].Start : segments[si].End;
          // Every OTHER end that coincides with this one - a segment's own far end included, as when every end
          // was compared with every end.
          int matches = 0;
          foreach (int sj in grid.Near(end))
          {
            if (!(sj == si && atStart) && Coincide(end, segments[sj].Start, tolerance))
            {
              matches++;
            }

            if (!(sj == si && !atStart) && Coincide(end, segments[sj].End, tolerance))
            {
              matches++;
            }

            if (matches >= 2)
            {
              return true;
            }
          }
        }
      }

      return false;
    }

    /// <summary>Segment indices by the cells their ends fall in; <see cref="Near"/> yields each segment with
    /// an end in the 3 x 3 cells around a point once, in no particular order.</summary>
    private sealed class EndGrid
    {
      private readonly double cell;
      private readonly Dictionary<(long X, long Y), List<int>> cells = new Dictionary<(long X, long Y), List<int>>();

      public EndGrid(List<Segment> segments, double tolerance)
      {
        this.cell = tolerance * 1.01;
        for (int i = 0; i < segments.Count; i++)
        {
          this.Add(segments[i].Start, i);
          var endKey = this.Key(segments[i].End);
          if (endKey != this.Key(segments[i].Start))
          {
            this.Add(segments[i].End, i);
          }
        }
      }

      public IEnumerable<int> Near(DxfPoint p)
      {
        var (cx, cy) = this.Key(p);
        var seen = new HashSet<int>();
        for (long x = cx - 1; x <= cx + 1; x++)
        {
          for (long y = cy - 1; y <= cy + 1; y++)
          {
            if (this.cells.TryGetValue((x, y), out var list))
            {
              foreach (int i in list)
              {
                if (seen.Add(i))
                {
                  yield return i;
                }
              }
            }
          }
        }
      }

      private (long X, long Y) Key(DxfPoint p)
        => (double.IsFinite(p.X) && double.IsFinite(p.Y))
          ? ((long)Math.Floor(p.X / this.cell), (long)Math.Floor(p.Y / this.cell))
          : (long.MinValue, long.MinValue);

      private void Add(DxfPoint p, int index)
      {
        var key = this.Key(p);
        if (!this.cells.TryGetValue(key, out var list))
        {
          this.cells[key] = list = new List<int>(2);
        }

        list.Add(index);
      }
    }

    private static bool Coincide(DxfPoint a, DxfPoint b, double tolerance)
      => Math.Abs(a.X - b.X) <= tolerance && Math.Abs(a.Y - b.Y) <= tolerance;

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
