namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.IO;
  using System.Linq;
  using System.Threading;
  using System.Globalization;
  using System.Text;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;

  /// <summary>
  /// Applies the normal part-to-part clearance around an intentional common-line graph. The clearance
  /// exception is geometric, not pair-wide: only the recorded outside-edge intervals, plus the short
  /// endpoint caps needed for their adjacent faces, may be closer than the normal gap.
  /// </summary>
  internal static class CommonEdgeClearance
  {
    private const int MaxExactBoundarySegments = 131072;

    internal readonly struct EdgeReference
    {
      internal EdgeReference(
        IPartPlacement partA,
        int edgeAIndex,
        IPartPlacement partB,
        int edgeBIndex,
        double edgeAStart = 0,
        double edgeAEnd = 1,
        double edgeBStart = 0,
        double edgeBEnd = 1,
        CommonBoundarySpan spanA = null,
        CommonBoundarySpan spanB = null,
        DxfResolvedBoundarySpan resolvedA = null,
        DxfResolvedBoundarySpan resolvedB = null,
        DxfContourCatalog catalogA = null,
        DxfContourCatalog catalogB = null)
      {
        this.PartA = partA;
        this.EdgeAIndex = edgeAIndex;
        this.PartB = partB;
        this.EdgeBIndex = edgeBIndex;
        this.EdgeAStart = edgeAStart;
        this.EdgeAEnd = edgeAEnd;
        this.EdgeBStart = edgeBStart;
        this.EdgeBEnd = edgeBEnd;
        this.SpanA = spanA;
        this.SpanB = spanB;
        this.ResolvedA = resolvedA;
        this.ResolvedB = resolvedB;
        this.CatalogA = catalogA;
        this.CatalogB = catalogB;
      }

      internal IPartPlacement PartA { get; }

      internal int EdgeAIndex { get; }

      internal IPartPlacement PartB { get; }

      internal int EdgeBIndex { get; }

      internal double EdgeAStart { get; }

      internal double EdgeAEnd { get; }

      internal double EdgeBStart { get; }

      internal double EdgeBEnd { get; }

      internal CommonBoundarySpan SpanA { get; }

      internal CommonBoundarySpan SpanB { get; }

      internal DxfResolvedBoundarySpan ResolvedA { get; }

      internal DxfResolvedBoundarySpan ResolvedB { get; }

      internal DxfContourCatalog CatalogA { get; }

      internal DxfContourCatalog CatalogB { get; }
    }

    /// <summary>
    /// The cut-safety verdict for one pair. Compatibility never enters this method: callers supply only
    /// concrete edge records selected by the planner and re-hydrated from the saved sheet.
    /// </summary>
    internal static bool TooClose(
      IPartPlacement a,
      IPartPlacement b,
      double normalClearance,
      double sliver,
      IReadOnlyList<EdgeReference> commonEdges,
      double commonLineGap,
      double geometryTolerance)
    {
      if (a?.PlacedPart == null || b?.PlacedPart == null)
      {
        return false;
      }

      double clearance = Math.Max(0, normalClearance);
      double geometryTol = Math.Max(0, geometryTolerance);
      double numericalTol = Math.Max(PlacementCollision.PlacementNoise, geometryTol);
      if (clearance <= 0 || commonEdges == null || commonEdges.Count == 0)
      {
        return PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, clearance, sliver);
      }

      IReadOnlyList<EdgeReference> valid = ValidEdges(commonEdges, commonLineGap, geometryTol);
      return TooCloseWithValidEdges(
        a,
        b,
        clearance,
        sliver,
        valid,
        commonLineGap,
        geometryTol,
        numericalTol,
        new Dictionary<IPartPlacement, BoundaryGeometry>());
    }

    private static bool TooCloseWithValidEdges(
      IPartPlacement a,
      IPartPlacement b,
      double clearance,
      double sliver,
      IReadOnlyList<EdgeReference> valid,
      double commonLineGap,
      double geometryTol,
      double numericalTol,
      IDictionary<IPartPlacement, BoundaryGeometry> boundaryCache,
      CancellationToken cancel = default,
      PairVerdictCache verdictCache = null)
    {
      cancel.ThrowIfCancellationRequested();
      string key = verdictCache?.Key(a, b, valid, clearance, sliver, commonLineGap, geometryTol, numericalTol);
      if (key != null && verdictCache.TryGet(key, out bool cached))
      {
        return cached;
      }

      bool result = EvaluatePair(a, b, clearance, sliver, valid, commonLineGap, geometryTol,
        numericalTol, boundaryCache, cancel);
      if (key != null)
      {
        verdictCache.Store(key, result);
      }

      return result;
    }

    private static bool EvaluatePair(
      IPartPlacement a,
      IPartPlacement b,
      double clearance,
      double sliver,
      IReadOnlyList<EdgeReference> valid,
      double commonLineGap,
      double geometryTol,
      double numericalTol,
      IDictionary<IPartPlacement, BoundaryGeometry> boundaryCache,
      CancellationToken cancel)
    {
      bool connected = AreConnected(a, b, valid);
      bool hasExactBoundary = HasExactBoundary(a, valid) || HasExactBoundary(b, valid);
      if (!connected && !hasExactBoundary)
      {
        return PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, clearance, sliver);
      }

      // A common-line record only relaxes CLEARANCE. It never authorises material overlap. This also
      // catches containment, which a boundary-distance-only test cannot see.
      if (PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, 0, numericalTol))
      {
        return true;
      }

      // Keep the cheap and battle-tested Clipper result as the broad phase for ordinary polygonal
      // placements. A source-backed curve needs the exact cached bounds below: a convex arc can extend
      // beyond its coarse NFP chord by more than GeometryTolerance.
      if (!hasExactBoundary
        && !PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, clearance, sliver))
      {
        return false;
      }

      BoundaryGeometry aBoundary = GetBoundaryGeometry(a, valid, numericalTol, boundaryCache);
      BoundaryGeometry bBoundary = GetBoundaryGeometry(b, valid, numericalTol, boundaryCache);
      if (aBoundary.Invalid || bBoundary.Invalid)
      {
        return true;
      }

      List<BoundarySegment> aSegments = aBoundary.Segments;
      List<BoundarySegment> bSegments = bBoundary.Segments;
      double required = Math.Max(0, clearance - Math.Max(Math.Max(0, sliver), numericalTol));
      required += aBoundary.ApproximationError + bBoundary.ApproximationError;
      if (!aBoundary.Bounds.MayBeWithin(bBoundary.Bounds, required))
      {
        return false;
      }

      double requiredSquared = required * required;
      var closePairs = new HashSet<SegmentPair>();
      for (int aPosition = 0; aPosition < aSegments.Count; aPosition++)
      {
        cancel.ThrowIfCancellationRequested();
        BoundarySegment edgeA = aSegments[aPosition];
        foreach (int bPosition in bBoundary.Index.Query(edgeA, required))
        {
          BoundarySegment edgeB = bSegments[bPosition];
          if (SegmentsWithin(edgeA, edgeB, numericalTol, requiredSquared))
          {
            if (!connected)
            {
              return true;
            }

            closePairs.Add(new SegmentPair(aPosition, bPosition));
          }
        }
      }

      // A rounded corner can be tessellated into an arbitrary number of short chords. Treat the
      // below-clearance cells that are topologically connected to a recorded seam endpoint as one
      // local cap, rather than assuming that one chord (or one fixed travel distance) is enough.
      // Once both branches recover the normal gap there is no product-grid connection, so a later
      // concave lobe remains an ordinary collision.
      IReadOnlyList<JunctionPointPair> junctionPairs = SharedJunctionPointPairs(
        a,
        b,
        valid,
        commonLineGap,
        geometryTol);
      HashSet<SegmentPair> endpointCaps = EndpointConnectedPairs(
        a,
        b,
        valid,
        aSegments,
        bSegments,
        closePairs,
        junctionPairs,
        geometryTol,
        requiredSquared,
        cancel);
      foreach (SegmentPair pair in closePairs)
      {
        if (!IsRecordedPair(
          a,
          aSegments[pair.APosition],
          b,
          bSegments[pair.BPosition],
          valid,
          numericalTol)
          && !endpointCaps.Contains(pair))
        {
          return true;
        }
      }

      // Connectivity alone must not turn a long, nearly-parallel branch into a common cut. Keep the
      // connected exception physically local to each junction. Segment clipping matters here: a long
      // straight branch may begin inside the cap and continue far outside it.
      List<Point> aCapPoints = junctionPairs.Select(pair => pair.A).ToList();
      List<Point> bCapPoints = junctionPairs.Select(pair => pair.B).ToList();
      // Four clearances is still a bounded, pair-local junction allowance (so a long nearly-parallel
      // lobe remains invalid), but it lets ordinary triangular/mitred faces fan out far enough to regain
      // the requested normal gap. A two-clearance disk clipped valid 20-30 degree corners prematurely.
      double capRadius = (4 * clearance) + (2 * Math.Max(0, commonLineGap)) + numericalTol;
      double directArcRadius = valid
        .Where(edge => (ReferenceEquals(edge.PartA, a) && ReferenceEquals(edge.PartB, b))
          || (ReferenceEquals(edge.PartA, b) && ReferenceEquals(edge.PartB, a)))
        .SelectMany(edge => new[] { edge.ResolvedA, edge.ResolvedB })
        .Where(span => span?.GeometryKind == CommonLineGeometryKind.CircularArc)
        .Select(span => span.Radius)
        .DefaultIfEmpty(0)
        .Max();
      if (directArcRadius > 0)
      {
        // At a tangent arc-to-line endpoint, clearance recovers by circular sagitta rather than linearly.
        // Scale the local cap by that exact radius; otherwise a perfectly valid large-radius seam would
        // be rejected a fixed four-clearance distance from its endpoint. The cap is still endpoint-local
        // and the connected-pair walk must first prove every admitted cell leads back to the seam.
        double commonGap = Math.Max(0, commonLineGap);
        double targetRadius = directArcRadius + Math.Max(clearance, commonGap);
        double initialRadius = directArcRadius + commonGap;
        double curvedRecovery = Math.Sqrt(Math.Max(
          0D,
          (targetRadius * targetRadius) - (initialRadius * initialRadius)));
        capRadius = Math.Max(capRadius, curvedRecovery + (2 * Math.Max(0, commonLineGap)) + numericalTol);
      }

      capRadius = StraightEndpointRecoveryRadius(
        a, b, valid, aSegments, bSegments, junctionPairs, clearance, capRadius, numericalTol);

      if (HasReentrantEndpointGeometry(
        a,
        b,
        valid,
        aSegments,
        bSegments,
        endpointCaps,
        junctionPairs,
        capRadius,
        numericalTol,
        cancel))
      {
        return true;
      }

      if (HasCloseGeometryOutsideCaps(
        a,
        b,
        valid,
        aSegments,
        bSegments,
        aCapPoints,
        bCapPoints,
        capRadius,
        numericalTol,
        requiredSquared,
        aBoundary.Index,
        bBoundary.Index,
        cancel))
      {
        return true;
      }

      return false;
    }

    /// <summary>Sheet-wide form shared by final engine acceptance and the manual editor/export gate.</summary>
    internal static PlacementAcceptance.Unfit FindUnfit(
      IReadOnlyList<IPartPlacement> placements,
      double sheetWidth,
      double sheetHeight,
      double sheetEdgeMargin,
      Func<IPartPlacement, IPartPlacement, double> normalClearanceBetween,
      Func<IPartPlacement, IPartPlacement, double> sliverBetween,
      IReadOnlyList<EdgeReference> commonEdges,
      double commonLineGap,
      double geometryTolerance,
      bool stopAtFirst = false,
      CancellationToken cancel = default,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null,
      PairVerdictCache verdictCache = null)
    {
      cancel.ThrowIfCancellationRequested();
      var unfit = new PlacementAcceptance.Unfit();
      sliverBetween ??= (a, b) => PlacementCollision.PlacementNoise;
      double geometryTol = Math.Max(0, geometryTolerance);
      double numericalTol = Math.Max(PlacementCollision.PlacementNoise, geometryTol);
      IReadOnlyList<EdgeReference> valid = commonEdges == null || commonEdges.Count == 0
        ? Array.Empty<EdgeReference>()
        : ValidEdges(commonEdges, commonLineGap, geometryTol, sourceCatalogs);
      var boundaryCache = new Dictionary<IPartPlacement, BoundaryGeometry>();
      for (int i = 0; i < placements.Count; i++)
      {
        cancel.ThrowIfCancellationRequested();
        IPartPlacement a = placements[i];
        if (a?.PlacedPart == null)
        {
          continue;
        }

        if (PlacementAcceptance.IsOutsideUsableArea(a, sheetWidth, sheetHeight, sheetEdgeMargin))
        {
          unfit.OutsideMargin.Add(a);
          unfit.All.Add(a);
          if (stopAtFirst)
          {
            return unfit;
          }
        }

        for (int j = i + 1; j < placements.Count; j++)
        {
          IPartPlacement b = placements[j];
          double clearance = b?.PlacedPart == null ? 0 : Math.Max(0, normalClearanceBetween(a, b));
          double sliver = b?.PlacedPart == null ? 0 : sliverBetween(a, b);
          if (b?.PlacedPart != null
            && (clearance <= 0 || valid.Count == 0
              ? PlacementCollision.TooClose(a.PlacedPart, b.PlacedPart, clearance, sliver)
              : TooCloseWithValidEdges(
                a,
                b,
                clearance,
                sliver,
                valid,
                commonLineGap,
                geometryTol,
                numericalTol,
                boundaryCache,
                cancel,
                verdictCache)))
          {
            unfit.Overlapping.Add(a);
            unfit.Overlapping.Add(b);
            unfit.All.Add(a);
            unfit.All.Add(b);
            if (stopAtFirst)
            {
              return unfit;
            }
          }
        }
      }

      return unfit;
    }

    private static IReadOnlyList<EdgeReference> ValidEdges(
      IReadOnlyList<EdgeReference> commonEdges,
      double commonLineGap,
      double tolerance,
      IDictionary<string, DxfContourCatalog> sourceCatalogs = null)
    {
      var result = new List<EdgeReference>(commonEdges.Count);
      sourceCatalogs ??= new Dictionary<string, DxfContourCatalog>(StringComparer.OrdinalIgnoreCase);
      foreach (EdgeReference edge in commonEdges)
      {
        if (edge.PartA == null || edge.PartB == null)
        {
          continue;
        }

        if (edge.SpanA != null || edge.SpanB != null)
        {
          if (edge.SpanA == null
            || edge.SpanB == null)
          {
            continue;
          }

          bool sourceAddressed = edge.SpanA.ContourIndex >= 0 && edge.SpanB.ContourIndex >= 0;
          if (!sourceAddressed)
          {
            if (edge.SpanA.ContourIndex >= 0
              || edge.SpanB.ContourIndex >= 0
              || !CommonLineGeometryResolver.TryCreateCommonEdge(
                edge.PartA,
                edge.SpanA,
                edge.PartB,
                edge.SpanB,
                commonLineGap,
                tolerance,
                out _, sourceCatalogs))
            {
              continue;
            }

            result.Add(edge);
            continue;
          }

          if (!TryResolveExact(
              edge.PartA,
              edge.SpanA,
              sourceCatalogs,
              tolerance,
              out DxfResolvedBoundarySpan resolvedA,
              out DxfContourCatalog catalogA)
            || !TryResolveExact(
              edge.PartB,
              edge.SpanB,
              sourceCatalogs,
              tolerance,
              out DxfResolvedBoundarySpan resolvedB,
              out DxfContourCatalog catalogB)
            || !DxfCommonLineSpanResolver.TryCreateCenterCurve(
              resolvedA,
              resolvedB,
              commonLineGap,
              tolerance,
              out _,
              out _))
          {
            continue;
          }

          result.Add(new EdgeReference(
            edge.PartA,
            edge.EdgeAIndex,
            edge.PartB,
            edge.EdgeBIndex,
            edge.EdgeAStart,
            edge.EdgeAEnd,
            edge.EdgeBStart,
            edge.EdgeBEnd,
            edge.SpanA,
            edge.SpanB,
            resolvedA,
            resolvedB,
            catalogA,
            catalogB));
          continue;
        }

        if (CommonLineGeometryResolver.TryCreateCommonEdge(
          edge.PartA,
          edge.EdgeAIndex,
          edge.EdgeAStart,
          edge.EdgeAEnd,
          edge.PartB,
          edge.EdgeBIndex,
          edge.EdgeBStart,
          edge.EdgeBEnd,
          commonLineGap,
          tolerance,
          out CommonEdge validated, sourceCatalogs))
        {
          bool exactSource = validated.SpanA?.ContourIndex >= 0
            && validated.SpanB?.ContourIndex >= 0;
          if (exactSource)
          {
            if (!TryResolveExact(
                edge.PartA,
                validated.SpanA,
                sourceCatalogs,
                tolerance,
                out DxfResolvedBoundarySpan resolvedA,
                out DxfContourCatalog catalogA)
              || !TryResolveExact(
                edge.PartB,
                validated.SpanB,
                sourceCatalogs,
                tolerance,
                out DxfResolvedBoundarySpan resolvedB,
                out DxfContourCatalog catalogB)
              || !DxfCommonLineSpanResolver.TryCreateCenterCurve(
                resolvedA,
                resolvedB,
                commonLineGap,
                tolerance,
                out _,
                out _))
            {
              continue;
            }

            result.Add(new EdgeReference(
              edge.PartA,
              edge.EdgeAIndex,
              edge.PartB,
              edge.EdgeBIndex,
              edge.EdgeAStart,
              edge.EdgeAEnd,
              edge.EdgeBStart,
              edge.EdgeBEnd,
              validated.SpanA,
              validated.SpanB,
              resolvedA,
              resolvedB,
              catalogA,
              catalogB));
          }
          else
          {
            result.Add(edge);
          }
        }
      }

      return new PreparedEdges(result);
    }

    private static bool TryResolveExact(
      IPartPlacement placement,
      CommonBoundarySpan span,
      IDictionary<string, DxfContourCatalog> sourceCatalogs,
      double endpointTolerance,
      out DxfResolvedBoundarySpan resolved,
      out DxfContourCatalog sourceCatalog)
    {
      resolved = null;
      sourceCatalog = null;
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

        if (!sourceCatalogs.TryGetValue(path, out DxfContourCatalog catalog))
        {
          catalog = DxfContourCatalog.Load(path, endpointTolerance);
          sourceCatalogs.Add(path, catalog);
        }

        resolved = DxfCommonLineSpanResolver.Resolve(catalog, span, placement);
        sourceCatalog = catalog;
        return resolved != null;
      }
      catch
      {
        resolved = null;
        sourceCatalog = null;
        return false;
      }
    }

    /// <summary>
    /// Search-local memoization for immutable pose geometries. Keys use exact (not quantized) values,
    /// source fingerprints, both members' incident relationships and full-graph connectivity. The
    /// latter is the only nonlocal input to pair clearance. Never share this across nesting operations.
    /// </summary>
    internal sealed class PairVerdictCache
    {
      private readonly Dictionary<string, bool> values = new(StringComparer.Ordinal);
      private readonly Dictionary<INfp, int> geometryIds = new(ReferenceEqualityComparer.Instance);

      internal long Hits { get; private set; }

      internal long Misses { get; private set; }

      internal bool TryGet(string key, out bool result)
      {
        bool found = this.values.TryGetValue(key, out result);
        if (found)
        {
          this.Hits++;
        }
        else
        {
          this.Misses++;
        }

        return found;
      }

      internal void Store(string key, bool result)
      {
        if (this.values.Count >= 8192)
        {
          this.values.Clear();
        }

        this.values[key] = result;
      }

      internal string Key(IPartPlacement a, IPartPlacement b, IReadOnlyList<EdgeReference> edges,
        double clearance, double sliver, double gap, double tolerance, double numericalTolerance)
      {
        var key = new StringBuilder();
        Number(clearance);
        Number(sliver);
        Number(gap);
        Number(tolerance);
        Number(numericalTolerance);
        key.Append(AreConnected(a, b, edges)).Append('|');
        Part(a);
        Part(b);
        foreach (EdgeReference edge in edges)
        {
          if (!ReferenceEquals(edge.PartA, a) && !ReferenceEquals(edge.PartB, a)
            && !ReferenceEquals(edge.PartA, b) && !ReferenceEquals(edge.PartB, b))
          {
            continue;
          }

          Part(edge.PartA);
          Part(edge.PartB);
          Number(edge.EdgeAIndex);
          Number(edge.EdgeBIndex);
          Number(edge.EdgeAStart);
          Number(edge.EdgeAEnd);
          Number(edge.EdgeBStart);
          Number(edge.EdgeBEnd);
          Span(edge.SpanA);
          Span(edge.SpanB);
        }

        return key.ToString();

        void Number(double value) => key.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('|');
        void Part(IPartPlacement part)
        {
          if (!this.geometryIds.TryGetValue(part.Part, out int id))
          {
            this.geometryIds[part.Part] = id = this.geometryIds.Count;
          }

          Number(part.Id);
          Number(id);
          Number(part.X);
          Number(part.Y);
          Number(part.Rotation);
          key.Append(part.IsMirrored).Append('|');
        }

        void Span(CommonBoundarySpan span)
        {
          key.Append(span == null ? "none|" : "span|");
          if (span == null)
          {
            return;
          }

          Number(span.ContourIndex);
          Number(span.PrimitiveIndex);
          Number(span.StartParameter);
          Number(span.EndParameter);
          key.Append(span.SourceFingerprint).Append('|').Append(span.ContourFingerprint).Append('|');
        }
      }
    }

    /// <summary>Immutable relationship indices valid only for this acceptance pass.</summary>
    private sealed class PreparedEdges : IReadOnlyList<EdgeReference>
    {
      private readonly IReadOnlyList<EdgeReference> edges;
      private readonly Dictionary<(IPartPlacement, IPartPlacement), List<EdgeReference>> pairs = new();
      private readonly Dictionary<IPartPlacement, List<EdgeReference>> incident = new();
      private readonly Dictionary<IPartPlacement, IPartPlacement> roots = new();

      internal PreparedEdges(IReadOnlyList<EdgeReference> edges)
      {
        this.edges = edges;
        foreach (EdgeReference edge in edges)
        {
          this.roots.TryAdd(edge.PartA, edge.PartA);
          this.roots.TryAdd(edge.PartB, edge.PartB);
          this.roots[this.Root(edge.PartB)] = this.Root(edge.PartA);
          Add(edge.PartA, edge.PartB, edge);
          Add(edge.PartB, edge.PartA, edge);
        }

        void Add(IPartPlacement a, IPartPlacement b, EdgeReference edge)
        {
          if (!this.pairs.TryGetValue((a, b), out var list))
          {
            this.pairs[(a, b)] = list = new List<EdgeReference>();
          }

          list.Add(edge);
          if (!this.incident.TryGetValue(a, out var attached))
          {
            this.incident[a] = attached = new List<EdgeReference>();
          }

          attached.Add(edge);
        }
      }

      public int Count => this.edges.Count;

      public EdgeReference this[int index] => this.edges[index];

      public IEnumerator<EdgeReference> GetEnumerator() => this.edges.GetEnumerator();

      System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this.GetEnumerator();

      internal IReadOnlyList<EdgeReference> Between(IPartPlacement a, IPartPlacement b)
        => this.pairs.TryGetValue((a, b), out var list) ? list : Array.Empty<EdgeReference>();

      internal IReadOnlyList<EdgeReference> ForPart(IPartPlacement part)
        => this.incident.TryGetValue(part, out var list) ? list : Array.Empty<EdgeReference>();

      internal bool Connected(IPartPlacement a, IPartPlacement b)
        => this.roots.ContainsKey(a) && this.roots.ContainsKey(b) && ReferenceEquals(this.Root(a), this.Root(b));

      private IPartPlacement Root(IPartPlacement part)
      {
        while (!ReferenceEquals(this.roots[part], part))
        {
          part = this.roots[part];
        }

        return part;
      }
    }

    private static bool AreConnected(IPartPlacement a, IPartPlacement b, IReadOnlyList<EdgeReference> edges)
    {
      if (edges is PreparedEdges prepared)
      {
        return prepared.Connected(a, b);
      }

      var reached = new HashSet<IPartPlacement> { a };
      bool grew;
      do
      {
        grew = false;
        foreach (EdgeReference edge in edges)
        {
          if (reached.Contains(edge.PartA))
          {
            grew |= reached.Add(edge.PartB);
          }

          if (reached.Contains(edge.PartB))
          {
            grew |= reached.Add(edge.PartA);
          }
        }
      }
      while (grew);

      return reached.Contains(b);
    }

    private static bool IsRecordedPair(
      IPartPlacement a,
      BoundarySegment edgeA,
      IPartPlacement b,
      BoundarySegment edgeB,
      IReadOnlyList<EdgeReference> edges,
      double tolerance)
      => (edges is PreparedEdges prepared ? prepared.Between(a, b) : edges).Any(edge =>
        (MatchesReferenceSide(
            edge.PartA,
            edge.EdgeAIndex,
            edge.EdgeAStart,
            edge.EdgeAEnd,
            edge.ResolvedA,
            a,
            edgeA,
            tolerance)
          && MatchesReferenceSide(
            edge.PartB,
            edge.EdgeBIndex,
            edge.EdgeBStart,
            edge.EdgeBEnd,
            edge.ResolvedB,
            b,
            edgeB,
            tolerance))
        || (MatchesReferenceSide(
            edge.PartA,
            edge.EdgeAIndex,
            edge.EdgeAStart,
            edge.EdgeAEnd,
            edge.ResolvedA,
            b,
            edgeB,
            tolerance)
          && MatchesReferenceSide(
            edge.PartB,
            edge.EdgeBIndex,
            edge.EdgeBStart,
            edge.EdgeBEnd,
            edge.ResolvedB,
            a,
            edgeA,
            tolerance)));

    private static bool MatchesReferenceSide(
      IPartPlacement referencePart,
      int referenceIndex,
      double start,
      double end,
      DxfResolvedBoundarySpan exact,
      IPartPlacement testedPart,
      BoundarySegment testedSegment,
      double tolerance)
    {
      if (!ReferenceEquals(referencePart, testedPart))
      {
        return false;
      }

      return exact == null
        ? referenceIndex == testedSegment.Index && IsInside(testedSegment, start, end)
        : exact.GeometryKind == CommonLineGeometryKind.Line
          ? IsInsideExactLine(testedSegment, exact, tolerance)
          : IsInsideExactArc(testedSegment, exact, tolerance);
    }

    private static bool IsInsideExactLine(
      BoundarySegment segment,
      DxfResolvedBoundarySpan line,
      double tolerance)
    {
      if (segment.IsCurved || line?.GeometryKind != CommonLineGeometryKind.Line)
      {
        return false;
      }

      double dx = line.End.X - line.Start.X;
      double dy = line.End.Y - line.Start.Y;
      double length = Math.Sqrt((dx * dx) + (dy * dy));
      if (length <= tolerance)
      {
        return false;
      }

      double ux = dx / length;
      double uy = dy / length;
      return PointIsInside(segment.Start)
        && PointIsInside(segment.End)
        && PointIsInside(new Point(
          (segment.Start.X + segment.End.X) / 2.0,
          (segment.Start.Y + segment.End.Y) / 2.0));

      bool PointIsInside(Point point)
      {
        double fromStartX = point.X - line.Start.X;
        double fromStartY = point.Y - line.Start.Y;
        double perpendicular = Math.Abs((fromStartX * uy) - (fromStartY * ux));
        double along = (fromStartX * ux) + (fromStartY * uy);
        return perpendicular <= tolerance && along >= -tolerance && along <= length + tolerance;
      }
    }

    private static bool IsInsideExactArc(
      BoundarySegment segment,
      DxfResolvedBoundarySpan arc,
      double tolerance)
    {
      if (!segment.IsCurved
        || arc?.GeometryKind != CommonLineGeometryKind.CircularArc
        || arc.Center == null
        || !double.IsFinite(arc.Radius)
        || arc.Radius <= tolerance
        || !double.IsFinite(arc.SweepRadians)
        || Math.Abs(arc.SweepRadians) <= 0)
      {
        return false;
      }

      double radialTolerance = Math.Max(tolerance, PlacementCollision.PlacementNoise);
      double angularTolerance = radialTolerance / arc.Radius;
      if (!PointIsOnCircle(segment.CurveStart) || !PointIsOnCircle(segment.CurveEnd))
      {
        return false;
      }

      return AngleIsInside(segment.Start)
        && AngleIsInside(segment.End)
        && AngleIsInside(new Point(
          (segment.Start.X + segment.End.X) / 2.0,
          (segment.Start.Y + segment.End.Y) / 2.0));

      bool PointIsOnCircle(Point point)
      {
        double dx = point.X - arc.Center.X;
        double dy = point.Y - arc.Center.Y;
        double radius = Math.Sqrt((dx * dx) + (dy * dy));
        return Math.Abs(radius - arc.Radius) <= radialTolerance;
      }

      bool AngleIsInside(Point point)
      {
        if (Math.Abs(arc.SweepRadians) >= (2 * Math.PI) - angularTolerance)
        {
          return true;
        }

        double dx = point.X - arc.Center.X;
        double dy = point.Y - arc.Center.Y;
        double angle = Math.Atan2(dy, dx);
        double travel = arc.SweepRadians > 0
          ? PositiveAngle(angle - arc.StartAngleRadians)
          : PositiveAngle(arc.StartAngleRadians - angle);
        return travel <= Math.Abs(arc.SweepRadians) + angularTolerance;
      }
    }

    private static double PositiveAngle(double angle)
    {
      double turn = 2 * Math.PI;
      double result = angle % turn;
      return result < 0 ? result + turn : result;
    }

    private static bool IsInside(BoundarySegment segment, double start, double end)
    {
      double low = Math.Min(start, end);
      double high = Math.Max(start, end);
      double midpoint = (segment.StartParameter + segment.EndParameter) / 2.0;
      return midpoint >= low - 1e-9 && midpoint <= high + 1e-9;
    }

    /// <summary>The source catalog one relationship contributes for this part, or null.</summary>
    private static DxfContourCatalog ExactCatalogFor(EdgeReference reference, IPartPlacement part)
      => ReferenceEquals(reference.PartA, part)
        ? reference.CatalogA
        : ReferenceEquals(reference.PartB, part) ? reference.CatalogB : null;

    /// <summary>
    /// Every distinct drawing this part is measured against. More than one is a contradiction: the
    /// same placement cannot be two source contours at once, and the caller fails closed.
    /// </summary>
    private static DxfContourCatalog[] ExactCatalogs(
      IPartPlacement part,
      IReadOnlyList<EdgeReference> commonEdges)
      => (commonEdges ?? Array.Empty<EdgeReference>())
        .Select(reference => ExactCatalogFor(reference, part))
        .Where(catalog => catalog != null)
        .GroupBy(catalog => catalog.SourceName, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToArray();

    /// <summary>
    /// Answers the same question as the catalog lookup in <see cref="BoundarySegments"/> without
    /// rebuilding anything. Both must agree on which placements carry exact geometry: a part that is
    /// measured exactly must never be dismissed by the coarse broad phase, so this shares that one
    /// definition instead of restating it.
    /// </summary>
    private static bool HasExactBoundary(
      IPartPlacement part,
      IReadOnlyList<EdgeReference> commonEdges)
      => (commonEdges ?? Array.Empty<EdgeReference>())
        .Any(reference => ExactCatalogFor(reference, part) != null);

    /// <summary>
    /// One boundary rebuild per placement. The cache is only valid while <paramref name="commonEdges"/>
    /// and <paramref name="tolerance"/> hold still, so every public entry point owns a fresh one. A
    /// sheet-wide acceptance pass would otherwise rebuild the same exact contour once per neighbour.
    /// </summary>
    private static BoundaryGeometry GetBoundaryGeometry(
      IPartPlacement part,
      IReadOnlyList<EdgeReference> commonEdges,
      double tolerance,
      IDictionary<IPartPlacement, BoundaryGeometry> cache)
    {
      if (cache.TryGetValue(part, out BoundaryGeometry cached))
      {
        return cached;
      }

      List<BoundarySegment> segments = BoundarySegments(
        part,
        commonEdges,
        tolerance,
        out bool exactBoundaryInvalid,
        out double approximationError);
      var geometry = new BoundaryGeometry(exactBoundaryInvalid, segments, approximationError);
      cache[part] = geometry;
      return geometry;
    }

    private static List<BoundarySegment> BoundarySegments(
      IPartPlacement part,
      IReadOnlyList<EdgeReference> commonEdges,
      double tolerance,
      out bool exactBoundaryInvalid,
      out double approximationError)
    {
      exactBoundaryInvalid = false;
      approximationError = 0D;
      DxfContourCatalog[] exactCatalogs = ExactCatalogs(part, commonEdges);
      if (exactCatalogs.Length > 0)
      {
        if (exactCatalogs.Length != 1
          || !TryExactBoundarySegments(
            part,
            exactCatalogs[0],
            commonEdges,
            tolerance,
            out List<BoundarySegment> exactSegments,
            out approximationError))
        {
          exactBoundaryInvalid = true;
          return new List<BoundarySegment>();
        }

        return exactSegments;
      }

      SvgPoint[] points = part.PlacedPart.Points;
      int count = RingCount(points, tolerance);
      var result = new List<BoundarySegment>(count);
      if (count < 2)
      {
        return result;
      }

      bool[] curved = (part.PlacedPart as NoFitPolygon)?.GetCurvedSegments();
      DxfResolvedBoundarySpan[] exactSpans = (commonEdges ?? Array.Empty<EdgeReference>())
        .Select(reference => ReferenceEquals(reference.PartA, part)
          ? reference.ResolvedA
          : ReferenceEquals(reference.PartB, part) ? reference.ResolvedB : null)
        .Where(resolved => resolved != null)
        .ToArray();
      var splits = new Dictionary<int, SortedSet<double>>();
      foreach (EdgeReference reference in commonEdges ?? Array.Empty<EdgeReference>())
      {
        int edgeIndex;
        double start;
        double end;
        if (ReferenceEquals(reference.PartA, part))
        {
          edgeIndex = reference.EdgeAIndex;
          start = reference.EdgeAStart;
          end = reference.EdgeAEnd;
        }
        else if (ReferenceEquals(reference.PartB, part))
        {
          edgeIndex = reference.EdgeBIndex;
          start = reference.EdgeBStart;
          end = reference.EdgeBEnd;
        }
        else
        {
          continue;
        }

        if (edgeIndex < 0 || edgeIndex >= count)
        {
          continue;
        }

        if (!splits.TryGetValue(edgeIndex, out SortedSet<double> parameters))
        {
          splits[edgeIndex] = parameters = new SortedSet<double>();
        }

        parameters.Add(Math.Clamp(Math.Min(start, end), 0, 1));
        parameters.Add(Math.Clamp(Math.Max(start, end), 0, 1));
      }

      for (int i = 0; i < count; i++)
      {
        Point start = new Point(points[i].X, points[i].Y);
        Point end = new Point(points[(i + 1) % count].X, points[(i + 1) % count].Y);
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= tolerance)
        {
          continue;
        }

        var parameters = new SortedSet<double> { 0, 1 };
        if (splits.TryGetValue(i, out SortedSet<double> seamParameters))
        {
          parameters.UnionWith(seamParameters);
        }

        bool isCurved = curved != null && i < curved.Length && curved[i];
        if (isCurved)
        {
          foreach (DxfResolvedBoundarySpan arc in exactSpans.Where(span =>
            span.GeometryKind == CommonLineGeometryKind.CircularArc))
          {
            if (!ChordIsOnCircle(start, end, arc, tolerance))
            {
              continue;
            }

            AddAngularSplit(arc.Start);
            AddAngularSplit(arc.End);

            void AddAngularSplit(CommonLinePoint boundary)
            {
              double rayX = boundary.X - arc.Center.X;
              double rayY = boundary.Y - arc.Center.Y;
              double denominator = (rayX * dy) - (rayY * dx);
              if (Math.Abs(denominator) <= tolerance * Math.Max(1.0, length))
              {
                return;
              }

              double fromCenterX = start.X - arc.Center.X;
              double fromCenterY = start.Y - arc.Center.Y;
              double parameter = -((rayX * fromCenterY) - (rayY * fromCenterX)) / denominator;
              if (parameter > 1e-9 && parameter < 1.0 - 1e-9)
              {
                double hitX = fromCenterX + (parameter * dx);
                double hitY = fromCenterY + (parameter * dy);
                if ((hitX * rayX) + (hitY * rayY) > 0)
                {
                  parameters.Add(parameter);
                }
              }
            }
          }
        }
        else
        {
          foreach (DxfResolvedBoundarySpan line in exactSpans.Where(span =>
            span.GeometryKind == CommonLineGeometryKind.Line))
          {
            double lineDx = line.End.X - line.Start.X;
            double lineDy = line.End.Y - line.Start.Y;
            double lineLength = Math.Sqrt((lineDx * lineDx) + (lineDy * lineDy));
            if (lineLength <= tolerance
              || Math.Abs((dx * lineDy) - (dy * lineDx)) > tolerance * Math.Max(length, lineLength))
            {
              continue;
            }

            AddLineSplit(line.Start);
            AddLineSplit(line.End);

            void AddLineSplit(CommonLinePoint boundary)
            {
              double parameter = (((boundary.X - start.X) * dx) + ((boundary.Y - start.Y) * dy))
                / (length * length);
              if (parameter <= 1e-9 || parameter >= 1.0 - 1e-9)
              {
                return;
              }

              double projectedX = start.X + (parameter * dx);
              double projectedY = start.Y + (parameter * dy);
              if (Distance(new Point(projectedX, projectedY), new Point(boundary.X, boundary.Y)) <= tolerance)
              {
                parameters.Add(parameter);
              }
            }
          }
        }

        double[] ordered = parameters.ToArray();
        for (int parameterIndex = 0; parameterIndex + 1 < ordered.Length; parameterIndex++)
        {
          double from = ordered[parameterIndex];
          double to = ordered[parameterIndex + 1];
          if ((to - from) * length <= tolerance)
          {
            continue;
          }

          result.Add(new BoundarySegment(
            i,
            from,
            to,
            new Point(start.X + (from * dx), start.Y + (from * dy)),
            new Point(start.X + (to * dx), start.Y + (to * dy)),
            isCurved,
            start,
            end));
        }
      }

      return result;
    }

    /// <summary>
    /// Rebuilds the complete outside boundary from the same immutable LINE/ARC catalog that authorised
    /// the reduced gap. Sampling every source arc at a known sagitta prevents an unrelated convex arc
    /// from hiding behind the coarser nesting chord and inheriting a common-line clearance exception.
    /// Unsupported curves and excessive sampling fail closed.
    /// </summary>
    private static bool TryExactBoundarySegments(
      IPartPlacement part,
      DxfContourCatalog catalog,
      IReadOnlyList<EdgeReference> commonEdges,
      double tolerance,
      out List<BoundarySegment> result,
      out double approximationError)
    {
      result = new List<BoundarySegment>();
      approximationError = 0D;
      try
      {
        DxfSourceContour[] outside = catalog.Contours
          .Where(contour => !contour.IsChild && contour.IsClosed)
          .ToArray();
        if (outside.Length != 1
          || outside[0].Primitives.Count == 0
          || outside[0].Primitives.Any(primitive =>
            primitive.GeometryKind != CommonLineGeometryKind.Line
            && primitive.GeometryKind != CommonLineGeometryKind.CircularArc))
        {
          return false;
        }

        DxfSourceContour contour = outside[0];
        CommonBoundarySpan[] recordedSpans = (commonEdges ?? Array.Empty<EdgeReference>())
          .Select(reference => ReferenceEquals(reference.PartA, part)
            ? reference.SpanA
            : ReferenceEquals(reference.PartB, part) ? reference.SpanB : null)
          .Where(span => span != null
            && span.ContourIndex == contour.Index
            && string.Equals(
              span.SourceFingerprint,
              catalog.SourceFingerprint,
              StringComparison.Ordinal))
          .ToArray();
        int aggregateSegments = 0;
        double targetSagitta = Math.Max(tolerance, PlacementCollision.PlacementNoise) / 8D;
        foreach (DxfSourcePrimitive primitive in contour.Primitives)
        {
          CommonBoundarySpan fullSpan = DxfCommonLineSpanResolver.CreateSpan(
            catalog,
            contour.Index,
            primitive.Index);
          DxfResolvedBoundarySpan resolved = DxfCommonLineSpanResolver.Resolve(
            catalog,
            fullSpan,
            part);
          var parameters = new SortedSet<double> { 0D, 1D };
          foreach (CommonBoundarySpan recorded in recordedSpans.Where(span =>
            span.PrimitiveIndex == primitive.Index))
          {
            parameters.Add(Math.Clamp(recorded.StartParameter, 0D, 1D));
            parameters.Add(Math.Clamp(recorded.EndParameter, 0D, 1D));
          }

          if (primitive.GeometryKind == CommonLineGeometryKind.CircularArc)
          {
            if (resolved.Center == null
              || !double.IsFinite(resolved.Radius)
              || resolved.Radius <= tolerance
              || !double.IsFinite(resolved.SweepRadians))
            {
              return false;
            }

            double cosine = 1D - (targetSagitta / resolved.Radius);
            double maximumStep = 2D * Math.Acos(Math.Max(-1D, Math.Min(1D, cosine)));
            if (!double.IsFinite(maximumStep) || maximumStep <= 0D)
            {
              return false;
            }

            double required = Math.Ceiling(Math.Abs(resolved.SweepRadians) / maximumStep);
            if (!double.IsFinite(required) || required > MaxExactBoundarySegments)
            {
              return false;
            }

            int segmentCount = Math.Max(1, (int)required);
            for (int index = 1; index < segmentCount; index++)
            {
              parameters.Add((double)index / segmentCount);
            }
          }

          // A recorded interval endpoint can land on top of a tessellation sample, and a full-primitive
          // span comes back one ULP short of 1 after its round trip. Two parameters that resolve to the
          // same point are one point, not a zero-length chord, so they are collapsed here. A primitive
          // that collapses to nothing really is degenerate, and the caller still fails closed.
          double[] ordered = CollapseCoincidentParameters(parameters, resolved, tolerance);
          if (ordered.Length < 2
            || ordered.Length - 1 > MaxExactBoundarySegments - aggregateSegments)
          {
            return false;
          }

          aggregateSegments += ordered.Length - 1;
          for (int parameterIndex = 0; parameterIndex + 1 < ordered.Length; parameterIndex++)
          {
            double from = ordered[parameterIndex];
            double to = ordered[parameterIndex + 1];
            CommonLinePoint exactStart = resolved.PointAt(from);
            CommonLinePoint exactEnd = resolved.PointAt(to);
            var start = new Point(exactStart.X, exactStart.Y);
            var end = new Point(exactEnd.X, exactEnd.Y);
            if (Distance(start, end) <= tolerance)
            {
              return false;
            }

            bool isCurved = primitive.GeometryKind == CommonLineGeometryKind.CircularArc;
            if (isCurved)
            {
              double step = Math.Abs(resolved.SweepRadians) * (to - from);
              double sagitta = resolved.Radius * (1D - Math.Cos(step / 2D));
              if (!double.IsFinite(sagitta))
              {
                return false;
              }

              approximationError = Math.Max(approximationError, sagitta);
            }

            result.Add(new BoundarySegment(
              primitive.Index,
              from,
              to,
              start,
              end,
              isCurved,
              start,
              end));
          }
        }

        return result.Count >= 3;
      }
      catch
      {
        result.Clear();
        approximationError = 0D;
        return false;
      }
    }

    /// <summary>
    /// Keeps the first of any run of split parameters that resolve to the same point, and always keeps
    /// the primitive's own end so the next primitive still starts where this one finished. Returns
    /// fewer than two parameters only when the whole primitive is shorter than the tolerance.
    /// </summary>
    private static double[] CollapseCoincidentParameters(
      IReadOnlyCollection<double> parameters,
      DxfResolvedBoundarySpan resolved,
      double tolerance)
    {
      double last = parameters.Last();
      var kept = new List<double>();
      Point previous = default;
      foreach (double parameter in parameters.Take(parameters.Count - 1))
      {
        Point point = ResolvedPoint(resolved, parameter);
        if (kept.Count == 0 || Distance(previous, point) > tolerance)
        {
          kept.Add(parameter);
          previous = point;
        }
      }

      if (kept.Count == 0)
      {
        return Array.Empty<double>();
      }

      if (Distance(previous, ResolvedPoint(resolved, last)) > tolerance)
      {
        kept.Add(last);
        return kept.ToArray();
      }

      if (kept.Count < 2)
      {
        return Array.Empty<double>();
      }

      kept[kept.Count - 1] = last;
      return kept.ToArray();
    }

    private static Point ResolvedPoint(DxfResolvedBoundarySpan resolved, double parameter)
    {
      CommonLinePoint point = resolved.PointAt(parameter);
      return new Point(point.X, point.Y);
    }

    private static bool ChordIsOnCircle(
      Point start,
      Point end,
      DxfResolvedBoundarySpan arc,
      double tolerance)
    {
      if (arc?.Center == null || arc.Radius <= 0)
      {
        return false;
      }

      double radialTolerance = Math.Max(tolerance, PlacementCollision.PlacementNoise);
      return Math.Abs(Distance(start, new Point(arc.Center.X, arc.Center.Y)) - arc.Radius) <= radialTolerance
        && Math.Abs(Distance(end, new Point(arc.Center.X, arc.Center.Y)) - arc.Radius) <= radialTolerance;
    }

    private static HashSet<SegmentPair> EndpointConnectedPairs(
      IPartPlacement a,
      IPartPlacement b,
      IReadOnlyList<EdgeReference> edges,
      IReadOnlyList<BoundarySegment> aSegments,
      IReadOnlyList<BoundarySegment> bSegments,
      IReadOnlySet<SegmentPair> closePairs,
      IReadOnlyList<JunctionPointPair> junctionPairs,
      double tolerance,
      double requiredSquared,
      CancellationToken cancel)
    {
      var result = new HashSet<SegmentPair>();
      if (closePairs.Count == 0 || aSegments.Count == 0 || bSegments.Count == 0)
      {
        return result;
      }

      var pending = new Queue<SegmentPair>();
      void Seed(SegmentPair pair)
      {
        if (closePairs.Contains(pair) && result.Add(pair))
        {
          pending.Enqueue(pair);
        }
      }

      // Every split cell inside a recorded interval is the bridge between its two endpoint caps.
      foreach (SegmentPair pair in closePairs)
      {
        if (IsRecordedPair(
          a,
          aSegments[pair.APosition],
          b,
          bSegments[pair.BPosition],
          edges,
          tolerance))
        {
          Seed(pair);
        }
      }

      // Diagonal members of a T/+ cluster have no direct seam pair, but their recorded seam endpoints
      // still meet at the same intentional junction. Proximity alone is insufficient here: two remote
      // arms of a folded component can happen to pass within one slot width. Require both records to
      // identify the exact same endpoint on a shared neighbour before granting the local cap.
      foreach (JunctionPointPair junction in junctionPairs)
      {
        for (int aPosition = 0; aPosition < aSegments.Count; aPosition++)
        {
          if (!IsIncident(junction.A, aSegments[aPosition], tolerance))
          {
            continue;
          }

          for (int bPosition = 0; bPosition < bSegments.Count; bPosition++)
          {
            if (IsIncident(junction.B, bSegments[bPosition], tolerance))
            {
              Seed(new SegmentPair(aPosition, bPosition));
            }
          }
        }
      }

      while (pending.Count > 0)
      {
        cancel.ThrowIfCancellationRequested();
        SegmentPair pair = pending.Dequeue();
        int aPosition = pair.APosition;
        int bPosition = pair.BPosition;
        BoundarySegment edgeA = aSegments[aPosition];
        BoundarySegment edgeB = bSegments[bPosition];

        TryCrossA(-1, edgeA.Start);
        TryCrossA(1, edgeA.End);
        TryCrossB(-1, edgeB.Start);
        TryCrossB(1, edgeB.End);

        void TryCrossA(int direction, Point sharedVertex)
        {
          if (PointSegmentDistanceSquared(sharedVertex, edgeB.Start, edgeB.End) >= requiredSquared)
          {
            return;
          }

          int nextPosition = Mod(aPosition + direction, aSegments.Count);
          Seed(new SegmentPair(nextPosition, pair.BPosition));
        }

        void TryCrossB(int direction, Point sharedVertex)
        {
          if (PointSegmentDistanceSquared(sharedVertex, edgeA.Start, edgeA.End) >= requiredSquared)
          {
            return;
          }

          int nextPosition = Mod(bPosition + direction, bSegments.Count);
          Seed(new SegmentPair(pair.APosition, nextPosition));
        }
      }

      return result;
    }

    private static bool IsIncident(Point vertex, BoundarySegment segment, double tolerance)
    {
      double toleranceSquared = tolerance * tolerance;
      return DistanceSquared(vertex, segment.Start) <= toleranceSquared
        || DistanceSquared(vertex, segment.End) <= toleranceSquared;
    }

    private static int Mod(int value, int modulus)
    {
      int result = value % modulus;
      return result < 0 ? result + modulus : result;
    }

    private static bool HasCloseGeometryOutsideCaps(
      IPartPlacement a,
      IPartPlacement b,
      IReadOnlyList<EdgeReference> edges,
      IReadOnlyList<BoundarySegment> aSegments,
      IReadOnlyList<BoundarySegment> bSegments,
      IReadOnlyList<Point> aCaps,
      IReadOnlyList<Point> bCaps,
      double capRadius,
      double tolerance,
      double requiredSquared,
      SegmentIndex aIndex,
      SegmentIndex bIndex,
      CancellationToken cancel)
    {
      List<BoundarySegment> outsideASegments = OutsideCaps(aSegments, aCaps, capRadius, tolerance).ToList();
      List<BoundarySegment> outsideBSegments = OutsideCaps(bSegments, bCaps, capRadius, tolerance).ToList();
      foreach (BoundarySegment outsideA in outsideASegments)
      {
        cancel.ThrowIfCancellationRequested();
        foreach (int position in bIndex.Query(outsideA, Math.Sqrt(requiredSquared)))
        {
          BoundarySegment edgeB = bSegments[position];
          if (SegmentsWithin(outsideA, edgeB, tolerance, requiredSquared)
            && !IsRecordedPair(a, outsideA, b, edgeB, edges, tolerance))
          {
            return true;
          }
        }
      }

      foreach (BoundarySegment outsideB in outsideBSegments)
      {
        cancel.ThrowIfCancellationRequested();
        foreach (int position in aIndex.Query(outsideB, Math.Sqrt(requiredSquared)))
        {
          BoundarySegment edgeA = aSegments[position];
          if (SegmentsWithin(edgeA, outsideB, tolerance, requiredSquared)
            && !IsRecordedPair(a, edgeA, b, outsideB, edges, tolerance))
          {
            return true;
          }
        }
      }

      return false;
    }

    /// <summary>
    /// A cap only permits the two contour branches to fan away from an intentional seam endpoint. It
    /// must not hide a short concave hook that first opens and then turns back toward the neighbouring
    /// part, nor an unrecorded continuation that simply stays parallel at the common gap. Compare
    /// directed, equal-arclength tangents for the actual below-gap segment cells: every non-recorded
    /// branch must make positive progress toward recovering normal clearance.
    /// </summary>
    private static bool HasReentrantEndpointGeometry(
      IPartPlacement a,
      IPartPlacement b,
      IReadOnlyList<EdgeReference> edges,
      IReadOnlyList<BoundarySegment> aSegments,
      IReadOnlyList<BoundarySegment> bSegments,
      IReadOnlySet<SegmentPair> endpointPairs,
      IReadOnlyList<JunctionPointPair> junctionPairs,
      double capRadius,
      double tolerance,
      CancellationToken cancel)
    {
      if (endpointPairs.Count == 0 || capRadius <= tolerance)
      {
        return false;
      }

      var seen = new List<(Point A, Point B)>();
      foreach (JunctionPointPair junction in junctionPairs)
      {
        cancel.ThrowIfCancellationRequested();
        if (IsReentrant(junction.A, junction.B))
        {
          return true;
        }
      }

      return false;

      bool IsReentrant(Point aEndpoint, Point bEndpoint)
      {
        if (seen.Any(pair => Distance(pair.A, aEndpoint) <= tolerance
          && Distance(pair.B, bEndpoint) <= tolerance))
        {
          return false;
        }

        seen.Add((aEndpoint, bEndpoint));
        double gapX = bEndpoint.X - aEndpoint.X;
        double gapY = bEndpoint.Y - aEndpoint.Y;
        double gapLength = Math.Sqrt((gapX * gapX) + (gapY * gapY));
        if (gapLength <= tolerance)
        {
          return false;
        }

        double nx = gapX / gapLength;
        double ny = gapY / gapLength;
        Dictionary<int, Point> aDirections = EndpointBranchDirections(
          a,
          aSegments,
          endpointPairs.Select(pair => pair.APosition),
          aEndpoint,
          edges,
          capRadius,
          tolerance);
        Dictionary<int, Point> bDirections = EndpointBranchDirections(
          b,
          bSegments,
          endpointPairs.Select(pair => pair.BPosition),
          bEndpoint,
          edges,
          capRadius,
          tolerance);
        double directionTolerance = Math.Max(1e-9, tolerance / Math.Max(1D, capRadius));
        foreach (SegmentPair pair in endpointPairs)
        {
          if (!aDirections.TryGetValue(pair.APosition, out Point directionA)
            || !bDirections.TryGetValue(pair.BPosition, out Point directionB))
          {
            continue;
          }

          double relativeX = directionB.X - directionA.X;
          double relativeY = directionB.Y - directionA.Y;
          double relativeNormal = (relativeX * nx) + (relativeY * ny);
          double relativeMotion = Math.Sqrt((relativeX * relativeX) + (relativeY * relativeY));
          if (relativeNormal < -directionTolerance
            || (relativeNormal <= directionTolerance && relativeMotion <= directionTolerance))
          {
            return true;
          }
        }

        return false;
      }
    }

    private static double StraightEndpointRecoveryRadius(
      IPartPlacement a,
      IPartPlacement b,
      IReadOnlyList<EdgeReference> edges,
      IReadOnlyList<BoundarySegment> aSegments,
      IReadOnlyList<BoundarySegment> bSegments,
      IReadOnlyList<JunctionPointPair> junctions,
      double clearance,
      double radius,
      double tolerance)
    {
      foreach (JunctionPointPair junction in junctions)
      {
        foreach (BoundarySegment first in aSegments.Where(segment => !segment.IsCurved
          && IsIncident(junction.A, segment, tolerance)
          && !IsRecordedSide(a, segment, edges, tolerance)))
        {
          foreach (BoundarySegment second in bSegments.Where(segment => !segment.IsCurved
            && IsIncident(junction.B, segment, tolerance)
            && !IsRecordedSide(b, segment, edges, tolerance)))
          {
            Point endA = Distance(first.Start, junction.A) <= tolerance ? first.End : first.Start;
            Point endB = Distance(second.Start, junction.B) <= tolerance ? second.End : second.Start;
            double lengthA = Distance(junction.A, endA);
            double lengthB = Distance(junction.B, endB);
            if (Math.Min(lengthA, lengthB) <= tolerance)
            {
              continue;
            }

            double vx = ((endB.X - junction.B.X) / lengthB) - ((endA.X - junction.A.X) / lengthA);
            double vy = ((endB.Y - junction.B.Y) / lengthB) - ((endA.Y - junction.A.Y) / lengthA);
            double gx = junction.B.X - junction.A.X;
            double gy = junction.B.Y - junction.A.Y;
            double dot = (gx * vx) + (gy * vy);
            double speedSquared = (vx * vx) + (vy * vy);
            double deficit = (clearance * clearance) - (gx * gx) - (gy * gy);
            if (dot < 0D || speedSquared <= 1e-12D || deficit <= 0D)
            {
              continue;
            }

            // Solve |gap + travel * relativeDirection| = NormalPartGap. An acute corner may
            // legitimately need more than four clearances to open up. Both straight branches must
            // actually reach that distance before their next vertex; re-entry checks still apply.
            double recovery = deficit / (Math.Sqrt((dot * dot) + (speedSquared * deficit)) + dot);
            if (double.IsFinite(recovery) && recovery <= Math.Min(lengthA, lengthB))
            {
              radius = Math.Max(radius, recovery + clearance + Math.Sqrt((gx * gx) + (gy * gy)) + tolerance);
            }
          }
        }
      }

      return radius;
    }

    private static Dictionary<int, Point> EndpointBranchDirections(
      IPartPlacement part,
      IReadOnlyList<BoundarySegment> segments,
      IEnumerable<int> participatingPositions,
      Point endpoint,
      IReadOnlyList<EdgeReference> edges,
      double capRadius,
      double tolerance)
    {
      var participants = new HashSet<int>(participatingPositions);
      var result = new Dictionary<int, Point>();
      double capRadiusSquared = (capRadius + tolerance) * (capRadius + tolerance);
      for (int position = 0; position < segments.Count; position++)
      {
        if (!participants.Contains(position)
          || IsRecordedSide(part, segments[position], edges, tolerance))
        {
          continue;
        }

        bool fromStart = Distance(segments[position].Start, endpoint) <= tolerance;
        bool fromEnd = Distance(segments[position].End, endpoint) <= tolerance;
        if (!fromStart && !fromEnd)
        {
          continue;
        }

        Trace(position, fromStart ? 1 : -1);
      }

      return result;

      void Trace(int initialPosition, int step)
      {
        int position = initialPosition;
        Point expected = endpoint;
        for (int visited = 0; visited < segments.Count; visited++)
        {
          BoundarySegment segment = segments[position];
          Point from = step > 0 ? segment.Start : segment.End;
          Point to = step > 0 ? segment.End : segment.Start;
          if (!participants.Contains(position)
            || IsRecordedSide(part, segment, edges, tolerance)
            || Distance(from, expected) > tolerance)
          {
            break;
          }

          double dx = to.X - from.X;
          double dy = to.Y - from.Y;
          double length = Math.Sqrt((dx * dx) + (dy * dy));
          if (length > tolerance
            && Math.Min(DistanceSquared(from, endpoint), DistanceSquared(to, endpoint)) <= capRadiusSquared)
          {
            result[position] = new Point(dx / length, dy / length);
          }

          if (DistanceSquared(to, endpoint) > capRadiusSquared)
          {
            break;
          }

          expected = to;
          position = Mod(position + step, segments.Count);
        }
      }
    }

    private static bool IsRecordedSide(
      IPartPlacement part,
      BoundarySegment segment,
      IReadOnlyList<EdgeReference> edges,
      double tolerance)
      => (edges is PreparedEdges prepared ? prepared.ForPart(part) : edges).Any(edge =>
        MatchesReferenceSide(
          edge.PartA,
          edge.EdgeAIndex,
          edge.EdgeAStart,
          edge.EdgeAEnd,
          edge.ResolvedA,
          part,
          segment,
          tolerance)
        || MatchesReferenceSide(
          edge.PartB,
          edge.EdgeBIndex,
          edge.EdgeBStart,
          edge.EdgeBEnd,
          edge.ResolvedB,
          part,
          segment,
          tolerance));

    private static bool TryReferenceEndpoints(
      EdgeReference edge,
      bool useA,
      out Point start,
      out Point end)
    {
      IPartPlacement part = useA ? edge.PartA : edge.PartB;
      DxfResolvedBoundarySpan exact = useA ? edge.ResolvedA : edge.ResolvedB;
      if (exact != null)
      {
        start = new Point(exact.Start.X, exact.Start.Y);
        end = new Point(exact.End.X, exact.End.Y);
        return true;
      }

      int edgeIndex = useA ? edge.EdgeAIndex : edge.EdgeBIndex;
      double startParameter = useA ? edge.EdgeAStart : edge.EdgeBStart;
      double endParameter = useA ? edge.EdgeAEnd : edge.EdgeBEnd;
      SvgPoint[] points = part?.PlacedPart?.Points;
      int count = RingCount(points, PlacementCollision.PlacementNoise);
      if (edgeIndex < 0 || edgeIndex >= count)
      {
        start = default;
        end = default;
        return false;
      }

      SvgPoint a = points[edgeIndex];
      SvgPoint b = points[(edgeIndex + 1) % count];
      start = Interpolate(Math.Clamp(startParameter, 0, 1));
      end = Interpolate(Math.Clamp(endParameter, 0, 1));
      return true;

      Point Interpolate(double parameter)
        => new Point(
          a.X + (parameter * (b.X - a.X)),
          a.Y + (parameter * (b.Y - a.Y)));
    }

    private static IEnumerable<BoundarySegment> OutsideCaps(
      IReadOnlyList<BoundarySegment> segments,
      IReadOnlyList<Point> caps,
      double radius,
      double tolerance)
    {
      foreach (BoundarySegment segment in segments)
      {
        foreach (BoundarySegment piece in OutsideCaps(segment, caps, radius, tolerance))
        {
          yield return piece;
        }
      }
    }

    private static IEnumerable<BoundarySegment> OutsideCaps(
      BoundarySegment segment,
      IReadOnlyList<Point> caps,
      double radius,
      double tolerance)
    {
      if (caps.Count == 0 || radius <= 0)
      {
        yield return segment;
        yield break;
      }

      double dx = segment.End.X - segment.Start.X;
      double dy = segment.End.Y - segment.Start.Y;
      double lengthSquared = (dx * dx) + (dy * dy);
      if (lengthSquared <= tolerance * tolerance)
      {
        yield break;
      }

      var covered = new List<(double Start, double End)>();
      double radiusSquared = radius * radius;
      foreach (Point cap in caps)
      {
        double fromCapX = segment.Start.X - cap.X;
        double fromCapY = segment.Start.Y - cap.Y;
        double projection = -((fromCapX * dx) + (fromCapY * dy)) / lengthSquared;
        double closestX = fromCapX + (projection * dx);
        double closestY = fromCapY + (projection * dy);
        double perpendicularSquared = (closestX * closestX) + (closestY * closestY);
        if (perpendicularSquared >= radiusSquared)
        {
          continue;
        }

        double halfSpan = Math.Sqrt((radiusSquared - perpendicularSquared) / lengthSquared);
        double start = Math.Max(0, projection - halfSpan);
        double end = Math.Min(1, projection + halfSpan);
        if (end > start)
        {
          covered.Add((start, end));
        }
      }

      if (covered.Count == 0)
      {
        yield return segment;
        yield break;
      }

      covered.Sort((left, right) => left.Start.CompareTo(right.Start));
      double cursor = 0;
      foreach ((double start, double end) in covered)
      {
        if (start > cursor)
        {
          BoundarySegment piece = SegmentPortion(segment, cursor, start);
          if (Distance(piece.Start, piece.End) > tolerance)
          {
            yield return piece;
          }
        }

        cursor = Math.Max(cursor, end);
        if (cursor >= 1)
        {
          yield break;
        }
      }

      if (cursor < 1)
      {
        BoundarySegment piece = SegmentPortion(segment, cursor, 1);
        if (Distance(piece.Start, piece.End) > tolerance)
        {
          yield return piece;
        }
      }
    }

    private static BoundarySegment SegmentPortion(BoundarySegment segment, double start, double end)
    {
      double dx = segment.End.X - segment.Start.X;
      double dy = segment.End.Y - segment.Start.Y;
      return new BoundarySegment(
        segment.Index,
        segment.StartParameter + (start * (segment.EndParameter - segment.StartParameter)),
        segment.StartParameter + (end * (segment.EndParameter - segment.StartParameter)),
        new Point(segment.Start.X + (start * dx), segment.Start.Y + (start * dy)),
        new Point(segment.Start.X + (end * dx), segment.Start.Y + (end * dy)),
        segment.IsCurved,
        segment.CurveStart,
        segment.CurveEnd);
    }

    /// <summary>
    /// Returns only endpoint pairs backed by the common-edge graph. Direct neighbours inherit the
    /// endpoint correspondence of their own edge record. Indirect neighbours must each reference the
    /// same endpoint of the same third part, which is the topological evidence for a real T/+ node.
    /// </summary>
    private static List<JunctionPointPair> SharedJunctionPointPairs(
      IPartPlacement a,
      IPartPlacement b,
      IReadOnlyList<EdgeReference> edges,
      double commonLineGap,
      double tolerance)
    {
      double topologyTolerance = Math.Max(tolerance, PlacementCollision.PlacementNoise);
      double topologySquared = topologyTolerance * topologyTolerance;
      double reach = Math.Max(0, 2 * commonLineGap) + (2 * topologyTolerance);
      double reachSquared = reach * reach;
      var result = new List<JunctionPointPair>();

      void Add(Point aPoint, Point bPoint)
      {
        if (DistanceSquared(aPoint, bPoint) > reachSquared
          || result.Any(existing =>
            DistanceSquared(existing.A, aPoint) <= topologySquared
            && DistanceSquared(existing.B, bPoint) <= topologySquared))
        {
          return;
        }

        result.Add(new JunctionPointPair(aPoint, bPoint));
      }

      List<CommonEndpointLink> aLinks = CommonEndpointLinks(a, edges);
      List<CommonEndpointLink> bLinks = CommonEndpointLinks(b, edges);

      // A direct relationship carries its own unambiguous endpoint correspondence.
      foreach (CommonEndpointLink link in aLinks.Where(link => ReferenceEquals(link.Neighbor, b)))
      {
        Add(link.OwnerPoint, link.NeighborPoint);
      }

      // A and B may be diagonal members of a valid T/+ junction. They earn a cap only when both
      // relationships terminate at the same exact endpoint on the same common neighbour.
      foreach (CommonEndpointLink aLink in aLinks)
      {
        if (ReferenceEquals(aLink.Neighbor, b))
        {
          continue;
        }

        foreach (CommonEndpointLink bLink in bLinks)
        {
          if (ReferenceEquals(aLink.Neighbor, bLink.Neighbor)
            && DistanceSquared(aLink.NeighborPoint, bLink.NeighborPoint) <= topologySquared)
          {
            Add(aLink.OwnerPoint, bLink.OwnerPoint);
          }
        }
      }

      return result;
    }

    private static List<CommonEndpointLink> CommonEndpointLinks(
      IPartPlacement owner,
      IReadOnlyList<EdgeReference> edges)
    {
      var result = new List<CommonEndpointLink>();
      foreach (EdgeReference edge in edges)
      {
        bool ownerIsA;
        IPartPlacement neighbor;
        if (ReferenceEquals(edge.PartA, owner))
        {
          ownerIsA = true;
          neighbor = edge.PartB;
        }
        else if (ReferenceEquals(edge.PartB, owner))
        {
          ownerIsA = false;
          neighbor = edge.PartA;
        }
        else
        {
          continue;
        }

        if (neighbor == null
          || !TryReferenceEndpoints(edge, ownerIsA, out Point ownerStart, out Point ownerEnd)
          || !TryReferenceEndpoints(edge, !ownerIsA, out Point neighborStart, out Point neighborEnd))
        {
          continue;
        }

        double sameOrder = DistanceSquared(ownerStart, neighborStart)
          + DistanceSquared(ownerEnd, neighborEnd);
        double reverseOrder = DistanceSquared(ownerStart, neighborEnd)
          + DistanceSquared(ownerEnd, neighborStart);
        if (reverseOrder < sameOrder)
        {
          (neighborStart, neighborEnd) = (neighborEnd, neighborStart);
        }

        result.Add(new CommonEndpointLink(neighbor, ownerStart, neighborStart));
        result.Add(new CommonEndpointLink(neighbor, ownerEnd, neighborEnd));
      }

      return result;
    }

    private static int RingCount(SvgPoint[] points, double tolerance)
    {
      int count = points?.Length ?? 0;
      if (count > 1
        && Math.Abs(points[0].X - points[count - 1].X) <= tolerance
        && Math.Abs(points[0].Y - points[count - 1].Y) <= tolerance)
      {
        count--;
      }

      return count;
    }

    private static double SegmentDistanceSquared(Point a, Point b, Point c, Point d, double tolerance)
    {
      if (SegmentsIntersect(a, b, c, d, tolerance))
      {
        return 0;
      }

      return Math.Min(
        Math.Min(PointSegmentDistanceSquared(a, c, d), PointSegmentDistanceSquared(b, c, d)),
        Math.Min(PointSegmentDistanceSquared(c, a, b), PointSegmentDistanceSquared(d, a, b)));
    }

    private static bool SegmentsIntersect(Point a, Point b, Point c, Point d, double tolerance)
    {
      double abC = Cross(a, b, c);
      double abD = Cross(a, b, d);
      double cdA = Cross(c, d, a);
      double cdB = Cross(c, d, b);
      double scale = Math.Max(1, Math.Max(Distance(a, b), Distance(c, d)));
      double epsilon = tolerance * scale;
      if (((abC > epsilon && abD < -epsilon) || (abC < -epsilon && abD > epsilon))
        && ((cdA > epsilon && cdB < -epsilon) || (cdA < -epsilon && cdB > epsilon)))
      {
        return true;
      }

      return Math.Abs(abC) <= epsilon && OnSegment(c, a, b, tolerance)
        || Math.Abs(abD) <= epsilon && OnSegment(d, a, b, tolerance)
        || Math.Abs(cdA) <= epsilon && OnSegment(a, c, d, tolerance)
        || Math.Abs(cdB) <= epsilon && OnSegment(b, c, d, tolerance);
    }

    private static bool OnSegment(Point p, Point a, Point b, double tolerance)
      => p.X >= Math.Min(a.X, b.X) - tolerance
        && p.X <= Math.Max(a.X, b.X) + tolerance
        && p.Y >= Math.Min(a.Y, b.Y) - tolerance
        && p.Y <= Math.Max(a.Y, b.Y) + tolerance;

    private static double PointSegmentDistanceSquared(Point p, Point a, Point b)
    {
      double dx = b.X - a.X;
      double dy = b.Y - a.Y;
      double lengthSquared = (dx * dx) + (dy * dy);
      if (lengthSquared <= 0)
      {
        return DistanceSquared(p, a);
      }

      double t = (((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared;
      t = Math.Max(0, Math.Min(1, t));
      return DistanceSquared(p, new Point(a.X + (t * dx), a.Y + (t * dy)));
    }

    private static double Cross(Point a, Point b, Point p)
      => ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

    private static double Distance(Point a, Point b) => Math.Sqrt(DistanceSquared(a, b));

    private static double DistanceSquared(Point a, Point b)
    {
      double dx = b.X - a.X;
      double dy = b.Y - a.Y;
      return (dx * dx) + (dy * dy);
    }

    internal static bool SegmentsWithin(
      BoundarySegment first,
      BoundarySegment second,
      double tolerance,
      double requiredSquared)
    {
      double dx = Math.Max(0D, Math.Max(
        Math.Min(first.Start.X, first.End.X) - Math.Max(second.Start.X, second.End.X),
        Math.Min(second.Start.X, second.End.X) - Math.Max(first.Start.X, first.End.X)));
      double dy = Math.Max(0D, Math.Max(
        Math.Min(first.Start.Y, first.End.Y) - Math.Max(second.Start.Y, second.End.Y),
        Math.Min(second.Start.Y, second.End.Y) - Math.Max(first.Start.Y, first.End.Y)));
      // Bounds provide a conservative distance lower bound and reject distant fine-arc chords before
      // the more expensive intersection/projection calculation.
      return (dx * dx) + (dy * dy) < requiredSquared
        && SegmentDistanceSquared(first.Start, first.End, second.Start, second.End, tolerance)
          < requiredSquared;
    }

    /// <summary>
    /// A placement's rebuilt outside boundary, kept for the whole acceptance pass. Invalid means the
    /// exact source geometry could not be rebuilt; that fails closed rather than falling back to the
    /// coarser nesting polygon, which is the very approximation the reduced gap was granted against.
    /// </summary>
    private sealed class BoundaryGeometry
    {
      internal BoundaryGeometry(
        bool invalid,
        List<BoundarySegment> segments,
        double approximationError)
      {
        this.Invalid = invalid;
        this.Segments = segments;
        this.ApproximationError = approximationError;
        this.Bounds = SegmentBounds.Of(segments);
        this.Index = new SegmentIndex(segments);
      }

      internal bool Invalid { get; }

      internal List<BoundarySegment> Segments { get; }

      /// <summary>
      /// How far the true curve may lie outside the chord that stands in for it. Added to the required
      /// distance, so a chord can never report more clearance than the arc itself leaves.
      /// </summary>
      internal double ApproximationError { get; }

      internal SegmentBounds Bounds { get; }

      internal SegmentIndex Index { get; }
    }

    // Conservative broad phase only: every returned candidate still passes the unchanged exact
    // distance predicate. Original contour positions are retained for endpoint topology walks.
    internal sealed class SegmentIndex
    {
      private readonly (int Position, double MinX, double MaxX, double MinY, double MaxY)[] entries;

      internal SegmentIndex(IReadOnlyList<BoundarySegment> segments)
      {
        this.entries = segments.Select((segment, position) => (
          Position: position,
          MinX: Math.Min(segment.Start.X, segment.End.X),
          MaxX: Math.Max(segment.Start.X, segment.End.X),
          MinY: Math.Min(segment.Start.Y, segment.End.Y),
          MaxY: Math.Max(segment.Start.Y, segment.End.Y)))
          .OrderBy(entry => entry.MinX).ThenBy(entry => entry.Position).ToArray();
      }

      internal IEnumerable<int> Query(BoundarySegment segment, double distance)
      {
        double minX = Math.Min(segment.Start.X, segment.End.X) - distance;
        double maxX = Math.Max(segment.Start.X, segment.End.X) + distance;
        double minY = Math.Min(segment.Start.Y, segment.End.Y) - distance;
        double maxY = Math.Max(segment.Start.Y, segment.End.Y) + distance;
        foreach (var entry in this.entries)
        {
          if (entry.MinX > maxX)
          {
            yield break;
          }

          if (entry.MaxX >= minX && entry.MaxY >= minY && entry.MinY <= maxY)
          {
            yield return entry.Position;
          }
        }
      }
    }

    private readonly struct SegmentBounds
    {
      private SegmentBounds(double minX, double minY, double maxX, double maxY)
      {
        this.MinX = minX;
        this.MinY = minY;
        this.MaxX = maxX;
        this.MaxY = maxY;
      }

      internal double MinX { get; }

      internal double MinY { get; }

      internal double MaxX { get; }

      internal double MaxY { get; }

      /// <summary>
      /// A boundary with no segments keeps the inverted infinite box, so it reads as out of reach of
      /// everything. That matches what the pairwise loop already does with an empty segment list.
      /// </summary>
      internal static SegmentBounds Of(IReadOnlyList<BoundarySegment> segments)
      {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        foreach (BoundarySegment segment in segments)
        {
          minX = Math.Min(minX, Math.Min(segment.Start.X, segment.End.X));
          minY = Math.Min(minY, Math.Min(segment.Start.Y, segment.End.Y));
          maxX = Math.Max(maxX, Math.Max(segment.Start.X, segment.End.X));
          maxY = Math.Max(maxY, Math.Max(segment.Start.Y, segment.End.Y));
        }

        return new SegmentBounds(minX, minY, maxX, maxY);
      }

      internal bool MayBeWithin(SegmentBounds other, double distance)
        => !PlacementCollision.BoxesApart(
          this.MinX,
          this.MinY,
          this.MaxX,
          this.MaxY,
          other.MinX,
          other.MinY,
          other.MaxX,
          other.MaxY,
          distance);
    }

    internal readonly struct BoundarySegment
    {
      internal BoundarySegment(
        int index,
        double startParameter,
        double endParameter,
        Point start,
        Point end,
        bool isCurved = false)
        : this(index, startParameter, endParameter, start, end, isCurved, start, end)
      {
      }

      internal BoundarySegment(
        int index,
        double startParameter,
        double endParameter,
        Point start,
        Point end,
        bool isCurved,
        Point curveStart,
        Point curveEnd)
      {
        this.Index = index;
        this.StartParameter = startParameter;
        this.EndParameter = endParameter;
        this.Start = start;
        this.End = end;
        this.IsCurved = isCurved;
        this.CurveStart = curveStart;
        this.CurveEnd = curveEnd;
      }

      internal int Index { get; }

      internal double StartParameter { get; }

      internal double EndParameter { get; }

      internal Point Start { get; }

      internal Point End { get; }

      internal bool IsCurved { get; }

      internal Point CurveStart { get; }

      internal Point CurveEnd { get; }
    }

    private readonly struct SegmentPair : IEquatable<SegmentPair>
    {
      internal SegmentPair(int aPosition, int bPosition)
      {
        this.APosition = aPosition;
        this.BPosition = bPosition;
      }

      internal int APosition { get; }

      internal int BPosition { get; }

      public bool Equals(SegmentPair other)
        => this.APosition == other.APosition && this.BPosition == other.BPosition;

      public override bool Equals(object? obj) => obj is SegmentPair other && this.Equals(other);

      public override int GetHashCode() => HashCode.Combine(this.APosition, this.BPosition);
    }

    private readonly struct CommonEndpointLink
    {
      internal CommonEndpointLink(IPartPlacement neighbor, Point ownerPoint, Point neighborPoint)
      {
        this.Neighbor = neighbor;
        this.OwnerPoint = ownerPoint;
        this.NeighborPoint = neighborPoint;
      }

      internal IPartPlacement Neighbor { get; }

      internal Point OwnerPoint { get; }

      internal Point NeighborPoint { get; }
    }

    private readonly struct JunctionPointPair
    {
      internal JunctionPointPair(Point a, Point b)
      {
        this.A = a;
        this.B = b;
      }

      internal Point A { get; }

      internal Point B { get; }
    }

    internal readonly struct Point
    {
      internal Point(double x, double y)
      {
        this.X = x;
        this.Y = y;
      }

      internal double X { get; }

      internal double Y { get; }
    }
  }
}
