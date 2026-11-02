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
  /// they are stacked into a grid. Every part in a band of two or more shares a cut. The searched layout of a
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
          Joints(shape, twin, MaxYSide, MinYSide, shortest, tolerance));
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
      bool stack = false)
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
              Layout candidate = Build(poses, option.AlongX, option.Bands, parts, gap, spacing, stack, maxChain);
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
            sides[side].Add(new SideEdge(i, Math.Min(from, to), Math.Max(from, to)));
          }
        }
      }

      foreach (var side in sides)
      {
        side.Sort((x, y) => x.Lo.CompareTo(y.Lo));
      }

      return new Shape(angle, w, h, sides);
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

      var edges = new List<(int EdgeA, int EdgeB)>(high.Count);
      double length = 0;
      for (int i = 0; i < high.Count; i++)
      {
        if (!Same(high[i].Lo, low[i].Lo, tolerance)
            || !Same(high[i].Hi, low[i].Hi, tolerance)
            || high[i].Hi - high[i].Lo < shortest)
        {
          return null;
        }

        edges.Add((high[i].Edge, low[i].Edge));
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

    /// <summary>Lays one split out from the corner and counts what it gives. Bands go in pose order, longer
    /// bands first; each pose's parts are spread evenly over its bands, so a band of one (a part on its own)
    /// only happens when there is no other way. A stacked block is one cluster, so it stops growing at
    /// <paramref name="maxChain"/> parts and the next band starts a block of its own. A part is turned half a
    /// turn when the way it meets a neighbour alternates: every other one along a band, every other band across
    /// a block.</summary>
    private static Layout Build(IReadOnlyList<Pose> poses, bool alongX, int[] bands, int[] parts, double gap, double spacing, bool stack, int maxChain)
    {
      var slots = new List<Slot>();
      var seams = new List<Seam>();
      double cursor = 0;
      double along = 0;
      bool first = true;
      for (int p = 0; p < poses.Count; p++)
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
        for (int band = 0; band < m; band++)
        {
          int size = (parts[p] / m) + (band < parts[p] % m ? 1 : 0);
          bool stacked = stack && acrossLink != Link.None && size == previousSize && block + size <= maxChain;
          block = stacked ? block + size : size;
          bandInBlock = stacked ? bandInBlock + 1 : 0;
          if (!first)
          {
            cursor += stacked ? gap : spacing;
          }

          int start = slots.Count;
          for (int j = 0; j < size; j++)
          {
            double offset = j * (a + gap);
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

          along = Math.Max(along, (size * a) + ((size - 1) * gap));
          previousSize = size;
          previousStart = start;
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
        alongX ? cursor : along);
    }

    private static bool Same(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

    /// <summary>A straight edge on one side of the box, spanning <see cref="Lo"/> to <see cref="Hi"/> from the
    /// box corner.</summary>
    private readonly struct SideEdge
    {
      public SideEdge(int edge, double lo, double hi)
      {
        this.Edge = edge;
        this.Lo = lo;
        this.Hi = hi;
      }

      public int Edge { get; }

      public double Lo { get; }

      public double Hi { get; }
    }

    /// <summary>The part's outline at one allowed angle: its box, and how copies meet across each pair of
    /// opposite sides, in this pose or alternating with its twin half a turn round.</summary>
    internal sealed class Pose
    {
      /// <summary>A pose whose opposite sides each carry one straight edge, with no twin.</summary>
      public Pose(int angle, double w, double h, double seamAlongX, double seamAlongY, int minXEdge, int maxXEdge, int minYEdge, int maxYEdge)
        : this(angle, null, w, h, Single(seamAlongX, maxXEdge, minXEdge), Single(seamAlongY, maxYEdge, minYEdge))
      {
      }

      internal Pose(int angle, int? twinAngle, double w, double h, Joint?[,] alongX, Joint?[,] alongY)
      {
        this.Angle = angle;
        this.TwinAngle = twinAngle;
        this.W = w;
        this.H = h;
        this.X = alongX;
        this.Y = alongY;
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

      /// <summary>The seam between a copy and the one to its right (or above it), each in this pose or turned
      /// to its twin.</summary>
      public Joint? Join(bool alongX, bool firstTurned, bool secondTurned)
        => (alongX ? this.X : this.Y)[firstTurned ? 1 : 0, secondTurned ? 1 : 0];

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
          joints[0, 0] = new Joint(new[] { (highEdge, lowEdge) }, seam);
        }

        return joints;
      }

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
    /// upper or right part), and their total length.</summary>
    internal sealed class Joint
    {
      public Joint(IReadOnlyList<(int EdgeA, int EdgeB)> edges, double length)
      {
        this.Edges = edges;
        this.Length = length;
      }

      public IReadOnlyList<(int EdgeA, int EdgeB)> Edges { get; }

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

      /// <summary>Pairs of (edge on A, edge on B).</summary>
      public IReadOnlyList<(int EdgeA, int EdgeB)> Edges { get; }

      public double Length { get; }
    }

    internal sealed class Layout
    {
      public Layout(IReadOnlyList<Slot> slots, IReadOnlyList<Seam> seams, int unshared, double sharedLength, double extentX, double extentY)
      {
        this.Slots = slots;
        this.Seams = seams;
        this.Unshared = unshared;
        this.SharedLength = sharedLength;
        this.ExtentX = extentX;
        this.ExtentY = extentY;
      }

      public IReadOnlyList<Slot> Slots { get; }

      public IReadOnlyList<Seam> Seams { get; }

      public int Placed => this.Slots.Count;

      /// <summary>Copies with no shared cut at all.</summary>
      public int Unshared { get; }

      public double SharedLength { get; }

      public double ExtentX { get; }

      public double ExtentY { get; }
    }

    private sealed class Shape
    {
      public Shape(int angle, double w, double h, List<SideEdge>[] sides)
      {
        this.Angle = angle;
        this.W = w;
        this.H = h;
        this.Sides = sides;
      }

      public int Angle { get; }

      public double W { get; }

      public double H { get; }

      /// <summary>Min X, max X, min Y, max Y, each sorted along the side.</summary>
      public List<SideEdge>[] Sides { get; }
    }
  }
}
