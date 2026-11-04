namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using ClipperLib;
  using DeepNestLib;

  /// <summary>
  /// Rows of one part for common-line cutting, worked out by arithmetic instead of searched for.
  /// <para>Each band holds copies side by side at the common-line gap, so every part shares the straight sides of
  /// its box with its neighbours. When a side only meets the same side of a copy turned half a turn (the flat
  /// back of a slotted bar), the band alternates the two. Bands keep the ordinary spacing from each other unless
  /// they are stacked into a grid, or a band of one pose is bridged to the band of another pose beside it. Every
  /// part in a band of two or more shares a cut. The searched layout of a
  /// rounded rectangle could not promise that: its best sheet of test2 left one part on its own.</para>
  /// <para>It is only used where laying the part out by its box gives little or nothing away: its copies cannot
  /// reach into each other's boxes (<see cref="CopiesKeepToTheirBoxes"/>), the part fills most of its box
  /// (<see cref="FillsItsBoxes"/>), or the whole job fits on one sheet anyway.</para>
  /// </summary>
  internal static class RowPatternPacker
  {
    /// <summary>How far two copies may push into each other's boxes, as a share of the thinner box side, and
    /// still count as keeping to them. Rounded corners let boxes overlap a little where two corners pass; a
    /// notch another copy fits into lets them overlap by most of the part.</summary>
    private const double MaxReachShare = 0.25D;

    /// <summary>How much of its box a part has to fill for the box layout to be used on a job of several sheets
    /// even when copies reach into each other's boxes. A slotted bar whose end fingers fit into a copy's slots
    /// fills about 84 %; a U with a notch most of its height deep fills 72 %.</summary>
    private const double MinBoxFill = 0.80D;

    private const double ClipperScale = 1e6D;

    /// <summary>Parts per band and band counts are small, so every combination is tried, but this bounds a
    /// tiny part on a huge sheet.</summary>
    private const int MaxBandsPerPose = 200;

    private const int MinXSide = 0;
    private const int MaxXSide = 1;
    private const int MinYSide = 2;
    private const int MaxYSide = 3;

    /// <summary>How the copies next to each other one way are laid: in the same pose, alternating with the
    /// pose half a turn round, or not at the gap at all.</summary>
    internal enum Link
    {
      None,
      Same,
      Alternate,
    }

    /// <summary>Every allowed angle the part can be laid out in by its box, without repeats (a rectangle at
    /// 180 degrees is the same as at 0). Edge numbers follow the direct planner: no closing point, edge i runs
    /// from point i to point i + 1, curved edges never count.</summary>
    internal static List<Pose> Poses(INfp outline, IReadOnlyList<int> angles, double gap, double tolerance)
    {
      var poses = new List<Pose>();
      if (outline?.Points == null || outline.Points.Length < 3 || angles == null)
      {
        return poses;
      }

      var shapes = new Dictionary<int, Shape?>();
      foreach (int angle in angles)
      {
        if (!shapes.ContainsKey(angle))
        {
          shapes[angle] = Measure(angle == 0 ? outline : outline.Rotate(angle), angle, tolerance);
        }
      }

      // The shortest seam the direct planner will consider; anything shorter is not worth a shared cut.
      double shortest = Math.Max(2 * gap, 8 * tolerance);
      foreach (int angle in angles)
      {
        Shape? shape = shapes[angle];
        if (shape == null)
        {
          continue;
        }

        Shape? twin = angles
          .Where(other => Turn(other) == Turn(angle + 180))
          .Select(other => shapes[other])
          .FirstOrDefault(other => other != null);
        var pose = new Pose(
          angle,
          twin?.Angle,
          shape.W,
          shape.H,
          Joints(shape, twin, MaxXSide, MinXSide, shortest, tolerance),
          Joints(shape, twin, MaxYSide, MinYSide, shortest, tolerance),
          shape.Sides,
          twin?.Sides,
          shape.Outline,
          twin?.Outline);
        if ((pose.AlongX != Link.None || pose.AlongY != Link.None) && !poses.Any(p => p.SameAs(pose, tolerance)))
        {
          poses.Add(pose);
        }
      }

      return poses;
    }

    /// <summary>
    /// Whether no two copies, in any pose the pattern may use, can push into each other's boxes by more than a
    /// quarter of the thinner box side both ways at once. Only then is a layout by the box as tight as a searched
    /// one: a notch another copy can reach into is room the box layout throws away.
    /// <para>For each pair of poses this takes every offset at which the two would overlap (the Minkowski sum
    /// of one with the other turned inside out, interior included) and checks that it covers every offset at
    /// which the boxes overlap by more than that much. The outlines are thinned first so a finely divided arc
    /// does not cost a million pieces; that moves them by a twentieth of the allowance at most.</para>
    /// </summary>
    internal static bool CopiesKeepToTheirBoxes(INfp outline, IReadOnlyList<Pose> poses)
    {
      if (outline?.Points == null || outline.Points.Length < 3 || poses == null || poses.Count == 0)
      {
        return false;
      }

      var angles = poses
        .SelectMany(p => p.TwinAngle is int twin ? new[] { p.Angle, twin } : new[] { p.Angle })
        .Distinct()
        .ToList();
      double reach = MaxReachShare * poses.Min(p => Math.Min(p.W, p.H));
      var paths = angles.Select(angle => ToClipper(angle == 0 ? outline : outline.Rotate(angle), reach / 20D)).ToList();
      for (int i = 0; i < paths.Count; i++)
      {
        for (int j = i; j < paths.Count; j++)
        {
          if (!KeepToTheirBoxes(paths[i], paths[j], reach))
          {
            return false;
          }
        }
      }

      return true;
    }

    /// <summary>
    /// Whether the part fills at least <see cref="MinBoxFill"/> of its box in every pose the pattern may use. A
    /// copy reaching into another's box can only win back the air in that box, so when there is little of it the
    /// box layout gives little away, however deep the copies reach.
    /// </summary>
    internal static bool FillsItsBoxes(INfp outline, IReadOnlyList<Pose> poses)
    {
      if (outline?.Points == null || outline.Points.Length < 3 || poses == null || poses.Count == 0)
      {
        return false;
      }

      double area = outline.Area;
      return poses.All(p => area >= MinBoxFill * p.W * p.H);
    }

    /// <summary>
    /// The best band layout for up to <paramref name="count"/> copies in a usable area of
    /// <paramref name="usableW"/> by <paramref name="usableH"/>, or null when not even one fits.
    /// <para>Bands run the full length of the sheet one way, then the other way. Every number of bands of
    /// each pose that fits across is tried, and every split of the parts over them. The ranking is the one the
    /// nest ranks whole jobs by: most parts, then fewest parts on their own, then the shortest along the sheet's
    /// long side, which leaves the rest of the sheet in one piece, then most shared cut, then the shortest along
    /// the short side.</para>
    /// <para>With <paramref name="stack"/>, bands of the same pose and the same length sit at the common-line
    /// gap from each other instead of the spacing, so the parts also share the sides that face across the
    /// bands and the block becomes a grid. Four seams meet at each inner corner of a grid; whether that cuts
    /// cleanly is for the common-line checks to say, so the caller falls back to plain bands when it does not.</para>
    /// <para>With <paramref name="bridge"/>, the first band of a pose also sits at the common-line gap from the
    /// last band of the pose before it, and every part shares the straight edges that face a part of the other
    /// band: lying parts on top of standing ones share a stretch with each standing part under them. Where the
    /// ends of a band would not rest on the band below, as 36 in lying parts do not on standing ones every
    /// 7.01 in, the band slides along until they do, within the room the longest band leaves. A band whose
    /// straight edges would still face the other band's over less than a seam keeps the spacing.</para>
    /// </summary>
    internal static Layout? Plan(
      double usableW,
      double usableH,
      IReadOnlyList<Pose> poses,
      double gap,
      double spacing,
      int count,
      int maxChain,
      bool longAxisIsX,
      double tolerance,
      bool stack = false,
      bool bridge = false)
    {
      if (poses == null || poses.Count == 0 || count <= 0)
      {
        return null;
      }

      var options = new List<(bool AlongX, int[] Bands, int[] Kmax)>();
      foreach (bool alongX in new[] { true, false })
      {
        double length = alongX ? usableW : usableH;
        double across = alongX ? usableH : usableW;
        int[] kmax = poses.Select(p =>
        {
          double a = alongX ? p.W : p.H;
          double t = alongX ? p.H : p.W;
          Link link = alongX ? p.AlongX : p.AlongY;
          if (t > across + tolerance || a > length + tolerance)
          {
            return 0;
          }

          return link != Link.None
            ? Math.Max(1, Math.Min(maxChain, (int)Math.Floor((length + gap + tolerance) / (a + gap))))
            : 1;
        }).ToArray();

        var bands = new int[poses.Count];
        AddBandCounts(0, 0D, 0);

        void AddBandCounts(int pose, double used, int total)
        {
          if (pose == poses.Count)
          {
            if (total > 0)
            {
              options.Add((alongX, (int[])bands.Clone(), kmax));
            }

            return;
          }

          double t = alongX ? poses[pose].H : poses[pose].W;
          for (int m = 0; m <= MaxBandsPerPose && (m == 0 || kmax[pose] > 0); m++)
          {
            double width = used + (m * t) + (Math.Max(0, total + m - 1) * spacing);
            if (m > 0 && width > across + tolerance)
            {
              break;
            }

            bands[pose] = m;
            AddBandCounts(pose + 1, used + (m * t), total + m);
          }

          bands[pose] = 0;
        }
      }

      int capacity = options.Count == 0 ? 0 : options.Max(o => o.Bands.Select((m, p) => m * o.Kmax[p]).Sum());
      int target = Math.Min(count, capacity);
      if (target <= 0)
      {
        return null;
      }

      Layout? best = null;
      var clearance = new Dictionary<(int, bool, int, bool, bool, long), bool>();
      foreach (var option in options)
      {
        if (option.Bands.Select((m, p) => m * option.Kmax[p]).Sum() < target)
        {
          continue;
        }

        var parts = new int[poses.Count];
        Split(0, target);

        void Split(int pose, int left)
        {
          if (pose == poses.Count)
          {
            if (left == 0)
            {
              Layout candidate = Build(poses, option.AlongX, option.Bands, parts, gap, spacing, stack, bridge, maxChain, tolerance, clearance);
              if (best == null || Ranks(candidate, best, longAxisIsX, tolerance) < 0)
              {
                best = candidate;
              }
            }

            return;
          }

          int m = option.Bands[pose];
          int most = Math.Min(left, m * option.Kmax[pose]);
          for (int n = m; n <= most; n++)
          {
            parts[pose] = n;
            Split(pose + 1, left - n);
          }

          parts[pose] = 0;
        }
      }

      return best;
    }

    private static int Turn(int angle) => ((angle % 360) + 360) % 360;

    /// <summary>The part at one angle: its box, and every straight edge lying on each side of the box, as a
    /// span measured from the box corner. Null for a degenerate outline.</summary>
    private static Shape? Measure(INfp turned, int angle, double tolerance)
    {
      SvgPoint[] points = turned.Points;
      int count = points.Length;
      if (count > 1 && Math.Abs(points[0].X - points[count - 1].X) <= tolerance && Math.Abs(points[0].Y - points[count - 1].Y) <= tolerance)
      {
        count--;
      }

      double minX = points.Min(p => p.X);
      double maxX = points.Max(p => p.X);
      double minY = points.Min(p => p.Y);
      double maxY = points.Max(p => p.Y);
      double w = maxX - minX;
      double h = maxY - minY;
      if (w <= tolerance || h <= tolerance)
      {
        return null;
      }

      bool[]? curved = (turned as NoFitPolygon)?.GetCurvedSegments();
      var sides = new[] { new List<SideEdge>(), new List<SideEdge>(), new List<SideEdge>(), new List<SideEdge>() };
      for (int i = 0; i < count; i++)
      {
        if (curved != null && i < curved.Length && curved[i])
        {
          continue;
        }

        SvgPoint a = points[i];
        SvgPoint b = points[(i + 1) % count];
        double length = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
        if (length <= tolerance)
        {
          continue;
        }

        // Sides along X (min X, max X) span Y; sides along Y (min Y, max Y) span X.
        Take(MinXSide, Same(a.X, minX, tolerance) && Same(b.X, minX, tolerance), a.Y - minY, b.Y - minY);
        Take(MaxXSide, Same(a.X, maxX, tolerance) && Same(b.X, maxX, tolerance), a.Y - minY, b.Y - minY);
        Take(MinYSide, Same(a.Y, minY, tolerance) && Same(b.Y, minY, tolerance), a.X - minX, b.X - minX);
        Take(MaxYSide, Same(a.Y, maxY, tolerance) && Same(b.Y, maxY, tolerance), a.X - minX, b.X - minX);

        void Take(int side, bool onSide, double from, double to)
        {
          if (onSide)
          {
            sides[side].Add(new SideEdge(i, from, to));
          }
        }
      }

      foreach (var side in sides)
      {
        side.Sort((x, y) => x.Lo.CompareTo(y.Lo));
      }

      return new Shape(angle, w, h, sides, turned);
    }

    /// <summary>The seams one way between the pose and its twin, indexed [lower or left turned, upper or right
    /// turned]. Without a twin only [0, 0] can be set.</summary>
    private static Joint?[,] Joints(Shape shape, Shape? twin, int highSide, int lowSide, double shortest, double tolerance)
    {
      var joints = new Joint?[2, 2];
      for (int first = 0; first < 2; first++)
      {
        for (int second = 0; second < 2; second++)
        {
          Shape? a = first == 0 ? shape : twin;
          Shape? b = second == 0 ? shape : twin;
          if (a != null && b != null)
          {
            joints[first, second] = Join(a.Sides[highSide], b.Sides[lowSide], shortest, tolerance);
          }
        }
      }

      return joints;
    }

    /// <summary>One copy's high side against the next copy's low side. They make a seam only when every
    /// straight edge on the one meets an edge of the same span on the other: an edge left over would sit at the
    /// common-line gap from its neighbour with no shared cut recorded, which no cut can honour.</summary>
    private static Joint? Join(IReadOnlyList<SideEdge> high, IReadOnlyList<SideEdge> low, double shortest, double tolerance)
    {
      if (high.Count == 0 || high.Count != low.Count)
      {
        return null;
      }

      var edges = new List<(int EdgeA, int EdgeB, double AStart, double AEnd, double BStart, double BEnd)>(high.Count);
      double length = 0;
      for (int i = 0; i < high.Count; i++)
      {
        if (!Same(high[i].Lo, low[i].Lo, tolerance)
            || !Same(high[i].Hi, low[i].Hi, tolerance)
            || high[i].Hi - high[i].Lo < shortest)
        {
          return null;
        }

        edges.Add((high[i].Edge, low[i].Edge, 0D, 1D, 0D, 1D));
        length += high[i].Hi - high[i].Lo;
      }

      return new Joint(edges, length);
    }

    /// <summary>The outline at one angle in Clipper units with its box corner at the origin, thinned to
    /// <paramref name="thinning"/>.</summary>
    private static List<IntPoint> ToClipper(INfp turned, double thinning)
    {
      SvgPoint[] points = Simplify.SimplifyDouglasPeucker(turned.Points, thinning * thinning);
      double minX = points.Min(p => p.X);
      double minY = points.Min(p => p.Y);
      return points
        .Select(p => new IntPoint((long)Math.Round((p.X - minX) * ClipperScale), (long)Math.Round((p.Y - minY) * ClipperScale)))
        .ToList();
    }

    /// <summary>Puts B's box corner at every offset where the boxes overlap by more than
    /// <paramref name="reach"/> both ways, and says whether the parts overlap at every one of them.</summary>
    private static bool KeepToTheirBoxes(List<IntPoint> a, List<IntPoint> b, double reach)
    {
      long d = (long)Math.Round(reach * ClipperScale);
      long wa = a.Max(p => p.X);
      long ha = a.Max(p => p.Y);
      long wb = b.Max(p => p.X);
      long hb = b.Max(p => p.Y);
      if (wa - d <= d - wb || ha - d <= d - hb)
      {
        return true;
      }

      // B's box corner at t overlaps A's box by more than d in X exactly when d - wb < t.X < wa - d.
      var deep = new List<IntPoint>
      {
        new IntPoint(d - wb, d - hb),
        new IntPoint(wa - d, d - hb),
        new IntPoint(wa - d, ha - d),
        new IntPoint(d - wb, ha - d),
      };
      List<List<IntPoint>> overlapping = Clipper.MinkowskiSum(
        b.Select(p => new IntPoint(-p.X, -p.Y)).ToList(),
        new List<List<IntPoint>> { a },
        true);
      var clipper = new Clipper();
      clipper.AddPath(deep, PolyType.ptSubject, true);
      clipper.AddPaths(overlapping, PolyType.ptClip, true);
      var free = new List<List<IntPoint>>();
      clipper.Execute(ClipType.ctDifference, free, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
      double freeArea = free.Sum(path => Clipper.Area(path)) / (ClipperScale * ClipperScale);
      return freeArea <= reach * reach * 1e-4;
    }

    /// <summary>Negative when <paramref name="a"/> is the better layout.</summary>
    private static int Ranks(Layout a, Layout b, bool longAxisIsX, double tolerance)
    {
      if (a.Placed != b.Placed)
      {
        return b.Placed.CompareTo(a.Placed);
      }

      if (a.Unshared != b.Unshared)
      {
        return a.Unshared.CompareTo(b.Unshared);
      }

      double longA = longAxisIsX ? a.ExtentX : a.ExtentY;
      double longB = longAxisIsX ? b.ExtentX : b.ExtentY;
      if (!Same(longA, longB, tolerance))
      {
        return longA.CompareTo(longB);
      }

      if (!Same(a.SharedLength, b.SharedLength, tolerance))
      {
        return b.SharedLength.CompareTo(a.SharedLength);
      }

      double shortA = longAxisIsX ? a.ExtentY : a.ExtentX;
      double shortB = longAxisIsX ? b.ExtentY : b.ExtentX;
      return Same(shortA, shortB, tolerance) ? 0 : shortA.CompareTo(shortB);
    }

    /// <summary>Lays one split out from the corner and counts what it gives. The pose with the most parts goes
    /// first and a pose with fewer after it, so a few odd parts close the layout next to the rest of the sheet;
    /// poses with as many keep their order. Each pose's longer bands go first, and its parts are spread evenly
    /// over its bands, so a band of one (a part on its own) only happens when there is no other way. A stacked
    /// block is one cluster, so it stops growing at <paramref name="maxChain"/> parts and the next band starts a
    /// block of its own. A part is turned half a turn when the way it meets a neighbour alternates: every other
    /// one along a band, every other band across a block. With <paramref name="bridge"/>, a pose's first band
    /// joins the block of the band before it when their facing straight edges make seams (see
    /// <see cref="Bridge"/>) and the block stays within <paramref name="maxChain"/>; where the band's ends would
    /// not rest on the band below, the pose's bands slide along within the room the longest band leaves.
    /// <paramref name="clearance"/> keeps, across the splits of one plan, whether two parts of bridged bands keep
    /// the spacing at a given offset.</summary>
    private static Layout Build(
      IReadOnlyList<Pose> poses,
      bool alongX,
      int[] bands,
      int[] parts,
      double gap,
      double spacing,
      bool stack,
      bool bridge,
      int maxChain,
      double tolerance,
      Dictionary<(int, bool, int, bool, bool, long), bool> clearance)
    {
      var slots = new List<Slot>();
      var seams = new List<Seam>();
      double shortest = Math.Max(2 * gap, 8 * tolerance);
      double cursor = 0;
      double along = 0;
      bool first = true;
      int lastStart = -1;
      int lastSize = 0;
      int lastBlock = 0;
      int bridged = 0;
      foreach (int p in Enumerable.Range(0, poses.Count).OrderByDescending(i => parts[i]))
      {
        int m = bands[p];
        if (m == 0)
        {
          continue;
        }

        Pose pose = poses[p];
        double a = alongX ? pose.W : pose.H;
        double t = alongX ? pose.H : pose.W;
        Link alongLink = alongX ? pose.AlongX : pose.AlongY;
        Link acrossLink = alongX ? pose.AlongY : pose.AlongX;
        int previousSize = -1;
        int previousStart = -1;
        int block = 0;
        int bandInBlock = 0;
        double shift = 0;
        for (int band = 0; band < m; band++)
        {
          int size = (parts[p] / m) + (band < parts[p] % m ? 1 : 0);
          bool stacked = stack && acrossLink != Link.None && size == previousSize && block + size <= maxChain;
          List<Seam>? across = null;
          if (bridge && band == 0 && !first && lastBlock + size <= maxChain)
          {
            // Where its ends do not rest on the band below, the band slides along a step of the spacing at a time:
            // never past the longest band laid so far, which would lengthen the layout, and never a whole part of
            // the band below, where the same meeting comes round again.
            Pose low = poses[slots[lastStart].PoseIndex];
            double pitch = (alongX ? low.W : low.H) + gap;
            double length = (size * a) + ((size - 1) * gap);
            for (int k = 0; across == null && (k == 0 || (spacing > 0 && k * spacing < pitch && (k * spacing) + length <= along + tolerance)); k++)
            {
              across = Bridge(poses, slots, lastStart, lastSize, p, size, k * spacing, alongLink == Link.Alternate, gap, spacing, alongX, shortest, tolerance, clearance);
              shift = across != null ? k * spacing : 0D;
            }
          }

          block = stacked ? block + size : across != null ? lastBlock + size : size;
          bandInBlock = stacked ? bandInBlock + 1 : 0;
          if (!first)
          {
            cursor += stacked || across != null ? gap : spacing;
          }

          if (across != null)
          {
            seams.AddRange(across);
            bridged++;
          }

          int start = slots.Count;
          for (int j = 0; j < size; j++)
          {
            double offset = shift + (j * (a + gap));
            bool flipped = (((alongLink == Link.Alternate ? j : 0) + (acrossLink == Link.Alternate ? bandInBlock : 0)) % 2) == 1;
            Joint? before = j > 0 ? pose.Join(alongX, slots[start + j - 1].Flipped, flipped) : null;
            if (before != null)
            {
              seams.Add(new Seam(start + j - 1, start + j, alongX, before));
            }

            Joint? below = stacked ? pose.Join(!alongX, slots[previousStart + j].Flipped, flipped) : null;
            if (below != null)
            {
              seams.Add(new Seam(previousStart + j, start + j, !alongX, below));
            }

            slots.Add(alongX ? new Slot(p, offset, cursor, flipped) : new Slot(p, cursor, offset, flipped));
          }

          along = Math.Max(along, shift + (size * a) + ((size - 1) * gap));
          previousSize = size;
          previousStart = start;
          lastStart = start;
          lastSize = size;
          lastBlock = block;
          cursor += t;
          first = false;
        }
      }

      var welded = new HashSet<int>(seams.SelectMany(s => new[] { s.A, s.B }));
      return new Layout(
        slots,
        seams,
        slots.Count - welded.Count,
        seams.Sum(s => s.Length),
        alongX ? along : cursor,
        alongX ? cursor : along,
        bridged);
    }

    /// <summary>The seams between the band laid last and the next band, of another pose, laid at the gap
    /// above it (or to its right) from <paramref name="shift"/> along: for each pair of parts, every pair of
    /// straight edges that face each other
    /// over at least <paramref name="shortest"/>, as spans of each edge. Null when there are none; when two
    /// facing straight edges would meet over less than that, as they would sit at the gap with no seam, which
    /// no cut can honour; when a facing straight edge runs into a square corner (see
    /// <see cref="StopShortOfTheCorners"/>); or when two parts that share nothing come closer than the spacing,
    /// as the corner where a lying part ends can over a standing one.</summary>
    private static List<Seam>? Bridge(
      IReadOnlyList<Pose> poses,
      List<Slot> slots,
      int lowStart,
      int lowSize,
      int highPose,
      int highSize,
      double shift,
      bool highAlternates,
      double gap,
      double spacing,
      bool alongX,
      double shortest,
      double tolerance,
      Dictionary<(int, bool, int, bool, bool, long), bool> clearance)
    {
      Pose high = poses[highPose];
      double highA = alongX ? high.W : high.H;
      int highStart = slots.Count;
      var seams = new List<Seam>();
      for (int i = 0; i < lowSize; i++)
      {
        Slot lowSlot = slots[lowStart + i];
        Pose low = poses[lowSlot.PoseIndex];
        double lowAt = alongX ? lowSlot.MinX : lowSlot.MinY;
        double lowEnd = lowAt + (alongX ? low.W : low.H);
        double lowAcross = alongX ? lowSlot.MinY : lowSlot.MinX;
        double highAcross = lowAcross + (alongX ? low.H : low.W) + gap;
        IReadOnlyList<SideEdge> lowEdges = low.Side(lowSlot.Flipped, alongX ? MaxYSide : MaxXSide);
        for (int j = Math.Max(0, (int)Math.Floor((lowAt - spacing - highA - shift) / (highA + gap))); j < highSize; j++)
        {
          double highAt = shift + (j * (highA + gap));
          if (highAt >= lowEnd + spacing)
          {
            break;
          }

          bool highTurned = highAlternates && j % 2 == 1;
          IReadOnlyList<SideEdge> highEdges = high.Side(highTurned, alongX ? MinYSide : MinXSide);
          if (!StopShortOfTheCorners(lowEdges, lowEnd - lowAt, tolerance) || !StopShortOfTheCorners(highEdges, highA, tolerance))
          {
            return null;
          }
          var edges = new List<(int EdgeA, int EdgeB, double AStart, double AEnd, double BStart, double BEnd)>();
          double length = 0;
          foreach (SideEdge e in lowEdges)
          {
            foreach (SideEdge f in highEdges)
            {
              double from = Math.Max(lowAt + e.Lo, highAt + f.Lo);
              double to = Math.Min(lowAt + e.Hi, highAt + f.Hi);
              if (to - from <= tolerance)
              {
                continue;
              }

              if (to - from + tolerance < shortest)
              {
                return null;
              }

              // Clamped as the direct planner clamps: an end on a corner can come out a rounding hair past it.
              double a0 = Math.Clamp(e.At(from - lowAt), 0, 1);
              double a1 = Math.Clamp(e.At(to - lowAt), 0, 1);
              double b0 = Math.Clamp(f.At(from - highAt), 0, 1);
              double b1 = Math.Clamp(f.At(to - highAt), 0, 1);
              edges.Add((e.Edge, f.Edge, Math.Min(a0, a1), Math.Max(a0, a1), Math.Min(b0, b1), Math.Max(b0, b1)));
              length += to - from;
            }
          }

          if (edges.Count > 0)
          {
            seams.Add(new Seam(lowStart + i, highStart + j, !alongX, new Joint(edges, length)));
          }
          else
          {
            // The parts sit the same distance across for every split, so only the offset along tells them apart.
            var key = (lowSlot.PoseIndex, lowSlot.Flipped, highPose, highTurned, alongX, (long)Math.Round((highAt - lowAt) * 1e9));
            if (!clearance.TryGetValue(key, out bool keeps))
            {
              keeps = KeepsTheSpacing(low.Outline(lowSlot.Flipped), lowAt, lowAcross, high.Outline(highTurned), highAt, highAcross, alongX, spacing);
              clearance[key] = keeps;
            }

            if (!keeps)
            {
              return null;
            }
          }
        }
      }

      return seams.Count > 0 ? seams : null;
    }

    /// <summary>Whether every straight edge on a side ends short of both corners of the box, as between rounded
    /// corners. Where one reaches a square corner, a part joined to its neighbours down its long sides and to
    /// the other band across its end loses both edges at that corner, three seams meet there, and the
    /// common-line topology refuses it: measured on the slotted bar's square ends.</summary>
    private static bool StopShortOfTheCorners(IReadOnlyList<SideEdge> edges, double side, double tolerance)
      => edges.All(e => e.Lo > tolerance && e.Hi < side - tolerance);

    /// <summary>Whether two copies with no seam between them, their box corners at the given places along and
    /// across the bands, keep the spacing. A pose made up in a test has no outline and is taken to.</summary>
    private static bool KeepsTheSpacing(INfp? a, double aAt, double aAcross, INfp? b, double bAt, double bAcross, bool alongX, double spacing)
    {
      if (a == null || b == null)
      {
        return true;
      }

      INfp placedA = alongX ? a.Shift(aAt - a.MinX, aAcross - a.MinY) : a.Shift(aAcross - a.MinX, aAt - a.MinY);
      INfp placedB = alongX ? b.Shift(bAt - b.MinX, bAcross - b.MinY) : b.Shift(bAcross - b.MinX, bAt - b.MinY);
      return !PlacementCollision.TooClose(placedA, placedB, spacing - PlacementCollision.PlacementNoise, 0D);
    }

    private static bool Same(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

    /// <summary>A straight edge on one side of the box, spanning <see cref="Lo"/> to <see cref="Hi"/> from the
    /// box corner. It runs from <see cref="From"/> (its first point) to <see cref="To"/>.</summary>
    internal readonly struct SideEdge
    {
      public SideEdge(int edge, double from, double to)
      {
        this.Edge = edge;
        this.From = from;
        this.To = to;
      }

      public int Edge { get; }

      public double From { get; }

      public double To { get; }

      public double Lo => Math.Min(this.From, this.To);

      public double Hi => Math.Max(this.From, this.To);

      /// <summary>How far along the edge a point of the side lies, from 0 at its first point to 1 at its last.</summary>
      public double At(double along) => (along - this.From) / (this.To - this.From);
    }

    /// <summary>The part's outline at one allowed angle: its box, and how copies meet across each pair of
    /// opposite sides, in this pose or alternating with its twin half a turn round.</summary>
    internal sealed class Pose
    {
      /// <summary>A pose whose opposite sides each carry one straight edge, with no twin. The straight edges sit
      /// in the middle of their sides, as between the rounded corners of a rectangle. With no outline, nothing
      /// checks whether two corners of bridged bands keep the spacing.</summary>
      public Pose(int angle, double w, double h, double seamAlongX, double seamAlongY, int minXEdge, int maxXEdge, int minYEdge, int maxYEdge)
        : this(
          angle,
          null,
          w,
          h,
          Single(seamAlongX, maxXEdge, minXEdge),
          Single(seamAlongY, maxYEdge, minYEdge),
          new[]
          {
            Middle(minXEdge, h, seamAlongX),
            Middle(maxXEdge, h, seamAlongX),
            Middle(minYEdge, w, seamAlongY),
            Middle(maxYEdge, w, seamAlongY),
          },
          null,
          null,
          null)
      {
      }

      internal Pose(
        int angle,
        int? twinAngle,
        double w,
        double h,
        Joint?[,] alongX,
        Joint?[,] alongY,
        IReadOnlyList<SideEdge>[] sides,
        IReadOnlyList<SideEdge>[]? twinSides,
        INfp? outline,
        INfp? twinOutline)
      {
        this.Angle = angle;
        this.TwinAngle = twinAngle;
        this.W = w;
        this.H = h;
        this.X = alongX;
        this.Y = alongY;
        this.Sides = sides;
        this.TwinSides = twinSides;
        this.TurnedOutline = outline;
        this.TwinOutline = twinOutline;
        this.AlongX = LinkOf(alongX, twinAngle != null);
        this.AlongY = LinkOf(alongY, twinAngle != null);
      }

      public int Angle { get; }

      /// <summary>The allowed angle half a turn from this one, if any.</summary>
      public int? TwinAngle { get; }

      public double W { get; }

      public double H { get; }

      public Link AlongX { get; }

      public Link AlongY { get; }

      /// <summary>The shared cut when a copy sits to the right of this one.</summary>
      public double SeamAlongX => this.First(this.AlongX, this.X);

      /// <summary>The shared cut when a copy sits above this one.</summary>
      public double SeamAlongY => this.First(this.AlongY, this.Y);

      private Joint?[,] X { get; }

      private Joint?[,] Y { get; }

      /// <summary>Min X, max X, min Y, max Y of the pose's box: the straight edges on each.</summary>
      private IReadOnlyList<SideEdge>[] Sides { get; }

      private IReadOnlyList<SideEdge>[]? TwinSides { get; }

      private INfp? TurnedOutline { get; }

      private INfp? TwinOutline { get; }

      /// <summary>The seam between a copy and the one to its right (or above it), each in this pose or turned
      /// to its twin.</summary>
      public Joint? Join(bool alongX, bool firstTurned, bool secondTurned)
        => (alongX ? this.X : this.Y)[firstTurned ? 1 : 0, secondTurned ? 1 : 0];

      /// <summary>The straight edges on one side of the box, in this pose or turned to its twin.</summary>
      internal IReadOnlyList<SideEdge> Side(bool turned, int side)
        => (turned ? this.TwinSides : this.Sides)?[side] ?? Array.Empty<SideEdge>();

      /// <summary>The outline a copy in this pose, or turned to its twin, is placed as; null for a pose made up
      /// in a test.</summary>
      internal INfp? Outline(bool turned) => turned ? this.TwinOutline : this.TurnedOutline;

      internal bool SameAs(Pose other, double tolerance)
        => Same(this.W, other.W, tolerance)
          && Same(this.H, other.H, tolerance)
          && this.AlongX == other.AlongX
          && this.AlongY == other.AlongY
          && SameLengths(this.AlongX, this.X, other.X, tolerance)
          && SameLengths(this.AlongY, this.Y, other.Y, tolerance);

      private static Joint?[,] Single(double seam, int highEdge, int lowEdge)
      {
        var joints = new Joint?[2, 2];
        if (seam > 0)
        {
          joints[0, 0] = new Joint(new[] { (highEdge, lowEdge, 0D, 1D, 0D, 1D) }, seam);
        }

        return joints;
      }

      private static IReadOnlyList<SideEdge> Middle(int edge, double side, double straight)
        => straight > 0 ? new[] { new SideEdge(edge, (side - straight) / 2, (side + straight) / 2) } : Array.Empty<SideEdge>();

      private static Link LinkOf(Joint?[,] joints, bool hasTwin)
        => joints[0, 0] != null && (!hasTwin || joints[1, 1] != null) ? Link.Same
          : hasTwin && joints[0, 1] != null && joints[1, 0] != null ? Link.Alternate
          : Link.None;

      /// <summary>The same seams up to which of the two poses comes first.</summary>
      private static bool SameLengths(Link link, Joint?[,] a, Joint?[,] b, double tolerance)
      {
        if (link == Link.Same)
        {
          return Same(LengthOf(a[0, 0]), LengthOf(b[0, 0]), tolerance);
        }

        if (link == Link.Alternate)
        {
          double aLow = Math.Min(LengthOf(a[0, 1]), LengthOf(a[1, 0]));
          double aHigh = Math.Max(LengthOf(a[0, 1]), LengthOf(a[1, 0]));
          double bLow = Math.Min(LengthOf(b[0, 1]), LengthOf(b[1, 0]));
          double bHigh = Math.Max(LengthOf(b[0, 1]), LengthOf(b[1, 0]));
          return Same(aLow, bLow, tolerance) && Same(aHigh, bHigh, tolerance);
        }

        return true;
      }

      private static double LengthOf(Joint? joint) => joint?.Length ?? 0D;

      private double First(Link link, Joint?[,] joints)
        => link == Link.Same ? LengthOf(joints[0, 0])
          : link == Link.Alternate ? LengthOf(joints[0, 1])
          : 0D;
    }

    /// <summary>The straight edges two neighbours share: pairs of (edge on the lower or left part, edge on the
    /// upper or right part), each with the stretch of it that is shared, from 0 at its first point to 1 at its
    /// last, and their total length.</summary>
    internal sealed class Joint
    {
      public Joint(IReadOnlyList<(int EdgeA, int EdgeB, double AStart, double AEnd, double BStart, double BEnd)> edges, double length)
      {
        this.Edges = edges;
        this.Length = length;
      }

      public IReadOnlyList<(int EdgeA, int EdgeB, double AStart, double AEnd, double BStart, double BEnd)> Edges { get; }

      public double Length { get; }
    }

    /// <summary>Where one copy goes: the lower-left corner of its box, measured from the usable area's corner,
    /// and whether it is turned to its pose's twin.</summary>
    internal sealed class Slot
    {
      public Slot(int poseIndex, double minX, double minY, bool flipped = false)
      {
        this.PoseIndex = poseIndex;
        this.MinX = minX;
        this.MinY = minY;
        this.Flipped = flipped;
      }

      public int PoseIndex { get; }

      public double MinX { get; }

      public double MinY { get; }

      public bool Flipped { get; }
    }

    /// <summary>Two neighbours that share a cut. <see cref="AlongX"/> means B sits to the right of A, so A's
    /// max-X side faces B's min-X side; otherwise B sits above A.</summary>
    internal sealed class Seam
    {
      public Seam(int a, int b, bool alongX, Joint joint)
      {
        this.A = a;
        this.B = b;
        this.AlongX = alongX;
        this.Edges = joint.Edges;
        this.Length = joint.Length;
      }

      public int A { get; }

      public int B { get; }

      public bool AlongX { get; }

      /// <summary>Pairs of (edge on A, edge on B), with the shared stretch of each.</summary>
      public IReadOnlyList<(int EdgeA, int EdgeB, double AStart, double AEnd, double BStart, double BEnd)> Edges { get; }

      public double Length { get; }
    }

    internal sealed class Layout
    {
      public Layout(IReadOnlyList<Slot> slots, IReadOnlyList<Seam> seams, int unshared, double sharedLength, double extentX, double extentY, int bridged)
      {
        this.Slots = slots;
        this.Seams = seams;
        this.Unshared = unshared;
        this.SharedLength = sharedLength;
        this.ExtentX = extentX;
        this.ExtentY = extentY;
        this.Bridged = bridged;
      }

      public IReadOnlyList<Slot> Slots { get; }

      public IReadOnlyList<Seam> Seams { get; }

      public int Placed => this.Slots.Count;

      /// <summary>Copies with no shared cut at all.</summary>
      public int Unshared { get; }

      public double SharedLength { get; }

      public double ExtentX { get; }

      public double ExtentY { get; }

      /// <summary>How many bands sit at the common-line gap from a band of another pose.</summary>
      public int Bridged { get; }
    }

    private sealed class Shape
    {
      public Shape(int angle, double w, double h, List<SideEdge>[] sides, INfp outline)
      {
        this.Angle = angle;
        this.W = w;
        this.H = h;
        this.Sides = sides;
        this.Outline = outline;
      }

      public int Angle { get; }

      public double W { get; }

      public double H { get; }

      /// <summary>Min X, max X, min Y, max Y, each sorted along the side.</summary>
      public List<SideEdge>[] Sides { get; }

      /// <summary>The outline turned to this angle, the polygon a copy in this pose is placed as.</summary>
      public INfp Outline { get; }
    }
  }
}
