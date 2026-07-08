namespace DeepNestSharp.RasterNest
{
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;

  internal sealed class PartType
  {
    public int Source;
    public INfp Poly;
    public int Quantity;
    public int[] RotationsDeg;
    public RasterMask[] Masks;
    public PackedMask[] Packed; // 64-bit packed rows of Masks, built by the nester
    public int Priority = 5;    // 0-10, higher nests first (Radan-style)
    public int HaloPx = 0;      // PER-PART spacing halo (px): masks dilate by this, so two parts end up (haloA + haloB) apart

    // PAIR CLUSTERING (industry "pairwise nesting"): parts that interlock with their own 180°
    // rotation — a triangle plus its flip is a parallelogram — get extra PAIR MODULE entries
    // appended to Masks/Packed/RotationsDeg. The scanner places a module like any rotation (so it
    // competes on position with singles) and the placement loop then emits TWO placements.
    // Dropping the buddy AFTER seating the seed was tried first and failed: the buddy's slot is
    // usually already occupied (measured 23 fits out of 77 tries on 100 triangles).
    public int FirstPairEntry = -1; // index into Masks where pair modules start; -1 = none
    public int[] PairSubA;          // per pair entry: the two REAL rotation indices…
    public int[] PairSubB;
    public int[] PairAx;            // …and each sub-part's offset inside the module frame
    public int[] PairAy;
    public int[] PairBx;
    public int[] PairBy;
  }

  /// <summary>
  /// A mask packed 64 columns per word (bit j of word w = column w*64+j), so the collision test ANDs
  /// whole words instead of walking cells one by one. Purely a faster representation — the fit/collide
  /// answer is bit-for-bit the same as the byte scan it replaces (which took minutes on real parts:
  /// mixed rotations leave a jagged frontier where near-fit positions scanned thousands of cells).
  /// </summary>
  internal sealed class PackedMask
  {
    public int W;
    public int H;
    public int WordsPerRow;
    public ulong[] Words; // H * WordsPerRow

    public static PackedMask From(RasterMask m)
    {
      int wpr = (m.W + 63) / 64;
      var words = new ulong[m.H * wpr];
      for (int y = 0; y < m.H; y++)
      {
        int rowBase = y * m.W;
        int wordBase = y * wpr;
        for (int x = 0; x < m.W; x++)
        {
          if (m.Bits[rowBase + x])
          {
            words[wordBase + (x >> 6)] |= 1UL << (x & 63);
          }
        }
      }

      return new PackedMask { W = m.W, H = m.H, WordsPerRow = wpr, Words = words };
    }
  }

  internal struct JobPlacement
  {
    public int Source;
    public int Sheet;
    public int Xpx;
    public int Ypx;
    public int RotationDeg;
  }

  internal sealed class JobResult
  {
    public List<JobPlacement> Placements;
    public int Sheets;
    public int NotPlaced;
    public bool NoOverlap;
  }

  /// <summary>
  /// Generalized raster nester: multiple part geometries (each with a quantity and allowed rotations),
  /// packed bottom-left across as many sheets as needed, biggest-first, best rotation per part. The
  /// collision search is the same bitmap test proven on the GPU (GpuNestLab); here on CPU.
  /// </summary>
  internal static class RasterJobNester
  {
    public static JobResult Nest(List<PartType> types, int sheetW, int sheetH, double pxPerInch, int marginPx, int maxSheets = int.MaxValue)
    {
      var profSw = NestProfile.Enabled ? System.Diagnostics.Stopwatch.StartNew() : null;

      // Dilate each part by ITS OWN spacing halo (PartType.HaloPx) so two parts end up exactly
      // (haloA + haloB) apart — per-part spacing / common-line (halo 0 = copies pack touching).
      // The real part sits inset by (halo, halo) inside its dilated mask.
      foreach (var t in types)
      {
        t.Masks = t.RotationsDeg
          .Select(r => RasterUtil.Dilate(RasterUtil.Rasterize(r == 0 ? t.Poly : t.Poly.Rotate(r), pxPerInch), t.HaloPx))
          .ToArray();
        t.Packed = t.Masks.Select(PackedMask.From).ToArray();

        // Rotation-symmetric parts (circles, squares…) yield IDENTICAL masks for several rotations —
        // scanning the duplicates is pure waste (4× for a circle). Keep one rotation per distinct mask.
        var keep = new List<int>();
        for (int i = 0; i < t.Packed.Length; i++)
        {
          bool dup = false;
          for (int j = 0; j < keep.Count && !dup; j++)
          {
            dup = MasksEqual(t.Packed[i], t.Packed[keep[j]]);
          }

          if (!dup)
          {
            keep.Add(i);
          }
        }

        if (keep.Count < t.Packed.Length)
        {
          t.RotationsDeg = keep.Select(i => t.RotationsDeg[i]).ToArray();
          t.Masks = keep.Select(i => t.Masks[i]).ToArray();
          t.Packed = keep.Select(i => t.Packed[i]).ToArray();
        }

        ComputePairing(t);
      }

      long profMaskMs = profSw?.ElapsedMilliseconds ?? 0;

      // Pad the grid by the LARGEST halo on every side so any part's spacing-halo can extend PAST the
      // sheet edge into a virtual border (no neighbour to keep clear of out there). The real part then
      // reaches the true edge at margin 0. Per part: true-sheet corner = grid corner + halo − pad, and
      // the scan inset keeps the REAL part (not the halo) at the margin.
      int pad = types.Count == 0 ? 0 : types.Max(t => t.HaloPx);
      int gw = sheetW + (2 * pad);
      int gh = sheetH + (2 * pad);
      int marginInset = marginPx;

      // Grow the pack along the sheet's LONGER axis and fill across the shorter one, so the leftover
      // remnant is a clean full-width strip of the SHORT dimension (e.g. on a 120x60 sheet the offcut is
      // 60 x whatever-is-left, not a skinny 120-long sliver — what the shop actually wants to keep).
      bool growX = sheetW > sheetH;

      int wpr = (gw + 63) / 64; // occupancy words per grid row (64 columns per word)

      var instances = new List<int>();
      for (int ti = 0; ti < types.Count; ti++)
      {
        for (int q = 0; q < types[ti].Quantity; q++)
        {
          instances.Add(ti);
        }
      }

      // Higher-priority part types nest first (they get the best sheet positions and are never the ones
      // left unplaced); within a priority tier, biggest-first as before.
      instances.Sort((a, b) =>
      {
        int byPriority = types[b].Priority.CompareTo(types[a].Priority);
        return byPriority != 0 ? byPriority : types[b].Masks[0].SolidCount.CompareTo(types[a].Masks[0].SolidCount);
      });

      var sheets = new List<ulong[]>();
      var sheetFree = new List<int>();
      var closed = new List<bool[]>(); // closed[sheet][type] — this type already failed to fit here

      // rowHints[sheet][type][rotation] = lowest grid row where this mask could still fit. A sheet only
      // ever FILLS UP, so once a mask has no fit with its corner below row Y, it never will — the next
      // identical part resumes scanning at Y instead of re-scanning the whole sheet from row 0. Exact
      // same placements (rows below the hint are proven impossible), massively fewer scans: the dominant
      // cost was every part re-walking the full occupied region (minutes for real production parts).
      var rowHints = new List<int[][]>();
      var placements = new List<JobPlacement>();
      int notPlaced = 0;
      long solidPlaced = 0;

      var used = new bool[instances.Count];
      for (int idx = 0; idx < instances.Count; idx++)
      {
        if (used[idx])
        {
          continue; // consumed as a pair buddy
        }

        int ti = instances[idx];
        var t = types[ti];
        int partSolid = t.Masks[0].SolidCount;
        bool done = false;

        for (int si = 0; si <= sheets.Count && !done; si++)
        {
          ulong[] occ;
          if (si < sheets.Count)
          {
            // Once a part type fails on a sheet it never fits there again (the sheet only fills up),
            // so skip it without re-scanning. Same result, no wasted full scans (the big speedup).
            if (closed[si][ti] || sheetFree[si] < partSolid)
            {
              closed[si][ti] = true;
              continue;
            }

            occ = sheets[si];
          }
          else
          {
            occ = new ulong[wpr * gh];
          }

          // The scan inset keeps the REAL part at the margin: real corner = grid + halo − pad, so the
          // mask corner must stay ≥ margin + pad − halo from the grid edge (per type).
          int insetPx = marginInset + pad - t.HaloPx;

          if (si == sheets.Count && sheets.Count >= maxSheets)
          {
            break; // no more sheets available — this part stays unplaced
          }

          if (si == sheets.Count && rowHints.Count == si)
          {
            // Prospective new sheet: hints start at the margin row for every type/rotation. Kept even if
            // this attempt fails (a part that doesn't fit an EMPTY sheet never fits one).
            rowHints.Add(types.Select(tt => Enumerable.Repeat(marginInset + pad - tt.HaloPx, tt.Masks.Length).ToArray()).ToArray());
          }

          // A pair module may only be chosen when a SECOND unused instance of this type exists.
          int pairMate = -1;
          if (t.FirstPairEntry >= 0)
          {
            for (int k = idx + 1; k < instances.Count; k++)
            {
              if (!used[k] && instances[k] == ti)
              {
                pairMate = k;
                break;
              }
            }
          }

          if (TryPlaceBest(occ, wpr, gw, gh, insetPx, t, rowHints[si][ti], growX, pairMate >= 0, out int x, out int y, out int rotIdx))
          {
            if (si == sheets.Count)
            {
              sheets.Add(occ);
              sheetFree.Add(gw * gh);
              closed.Add(new bool[types.Count]);
              si = sheets.Count - 1;
            }

            var m = t.Masks[rotIdx];
            Stamp(occ, wpr, t.Packed[rotIdx], x, y);
            sheetFree[si] -= m.SolidCount;
            solidPlaced += m.SolidCount;
            done = true;

            if (t.FirstPairEntry >= 0 && rotIdx >= t.FirstPairEntry)
            {
              // PAIR MODULE placed (e.g. two triangles as a parallelogram): one stamped union mask,
              // TWO real placements, and the mate instance is consumed from the queue.
              int pi = rotIdx - t.FirstPairEntry;
              placements.Add(new JobPlacement { Source = t.Source, Sheet = si, Xpx = x + t.PairAx[pi] + t.HaloPx - pad, Ypx = y + t.PairAy[pi] + t.HaloPx - pad, RotationDeg = t.RotationsDeg[t.PairSubA[pi]] });
              placements.Add(new JobPlacement { Source = t.Source, Sheet = si, Xpx = x + t.PairBx[pi] + t.HaloPx - pad, Ypx = y + t.PairBy[pi] + t.HaloPx - pad, RotationDeg = t.RotationsDeg[t.PairSubB[pi]] });
              used[pairMate] = true;
            }
            else
            {
              // Real part corner in TRUE-sheet coords = mask corner + halo − pad (the real part sits
              // +halo inside its dilated mask; the grid is padded by the largest halo).
              placements.Add(new JobPlacement { Source = t.Source, Sheet = si, Xpx = x + t.HaloPx - pad, Ypx = y + t.HaloPx - pad, RotationDeg = t.RotationsDeg[rotIdx] });
            }
          }
          else if (si < sheets.Count)
          {
            closed[si][ti] = true; // failed here — don't try this type on this sheet again
          }
        }

        if (!done)
        {
          notPlaced++;
        }
      }

      long occSum = 0;
      foreach (var s in sheets)
      {
        foreach (var w in s)
        {
          occSum += System.Numerics.BitOperations.PopCount(w);
        }
      }

      if (profSw != null)
      {
        long probes = System.Threading.Interlocked.Exchange(ref ProfFitsCalls, 0);
        NestProfile.Log($"RasterJob px={pxPerInch} types={types.Count} inst={instances.Count} rots={string.Join("/", types.Select(t => t.RotationsDeg.Length))} maskMs={profMaskMs} placeMs={profSw.ElapsedMilliseconds - profMaskMs} sheets={sheets.Count} fitsProbes={probes}");
      }

      return new JobResult
      {
        Placements = placements,
        Sheets = sheets.Count,
        NotPlaced = notPlaced,
        NoOverlap = occSum == solidPlaced,
      };
    }

    /// <summary>
    /// One-shot placement for the service's post-compaction REFILL: find the bottom-left position for
    /// one copy of <paramref name="t"/> (its Masks/Packed already built) on an existing occupancy grid.
    /// Exactly the nester's scan and collision test; row hints start at the inset because the occupancy
    /// was rebuilt fresh from exact positions.
    /// </summary>
    internal static bool TryPlaceOne(ulong[] occ, int wpr, int gw, int gh, int insetPx, PartType t, bool growX, out int x, out int y, out int rotIdx)
    {
      var hints = new int[t.Packed.Length];
      for (int i = 0; i < hints.Length; i++)
      {
        hints[i] = insetPx;
      }

      return TryPlaceBest(occ, wpr, gw, gh, insetPx, t, hints, growX, false, out x, out y, out rotIdx);
    }

    private static bool MasksEqual(PackedMask a, PackedMask b)
    {
      return a.W == b.W && a.H == b.H && System.MemoryExtensions.SequenceEqual<ulong>(a.Words, b.Words);
    }

    private static bool TryPlaceBest(ulong[] occ, int wpr, int sheetW, int sheetH, int insetPx, PartType t, int[] rowHints, bool growX, bool allowPair, out int bx, out int by, out int bRot)
    {
      // MODULE-FIRST (pairwise nesting): when a pair module fits it wins outright — a single always
      // fits at a position <= the module's (it is a subset), so scoring them together means the
      // module NEVER gets picked and the interlock is lost. Singles remain the fallback (end of
      // sheet, odd part out).
      if (allowPair && t.FirstPairEntry >= 0
          && TryPlaceRange(occ, wpr, sheetW, sheetH, insetPx, t, rowHints, growX, t.FirstPairEntry, t.Packed.Length, out bx, out by, out bRot))
      {
        return true;
      }

      int singleEnd = t.FirstPairEntry >= 0 ? t.FirstPairEntry : t.Packed.Length;
      return TryPlaceRange(occ, wpr, sheetW, sheetH, insetPx, t, rowHints, growX, 0, singleEnd, out bx, out by, out bRot);
    }

    private static bool TryPlaceRange(ulong[] occ, int wpr, int sheetW, int sheetH, int insetPx, PartType t, int[] rowHints, bool growX, int riFrom, int riTo, out int bx, out int by, out int bRot)
    {
      bx = -1;
      by = -1;
      bRot = -1;
      long bestIndex = long.MaxValue;

      for (int ri = riFrom; ri < riTo; ri++)
      {
        var m = t.Packed[ri];
        if (m.W + (2 * insetPx) > sheetW || m.H + (2 * insetPx) > sheetH)
        {
          continue;
        }

        // Resume where the previous identical mask left off — positions below the hint (along the
        // growth axis) are proven full.
        int start = rowHints[ri];
        if (start > (growX ? sheetW - m.W - insetPx : sheetH - m.H - insetPx))
        {
          continue; // proven: no fit anywhere on this sheet for this rotation, ever again
        }

        if (FindBottomLeft(occ, wpr, sheetW, sheetH, insetPx, m, start, growX, out int x, out int y))
        {
          rowHints[ri] = growX ? x : y; // the same row/column can still hold more copies
          long idx = growX ? ((long)x * sheetH) + y : ((long)y * sheetW) + x;

          // Tie on position: prefer the rotation that is NARROWER along the growth axis, so the part
          // tucks into the pack instead of sticking out sideways (e.g. a slim bracket placed on top of
          // a tall part goes vertical, keeping the used strip — and the remnant — at the pack's width).
          int growthDim = growX ? m.W : m.H;
          int bestGrowthDim = bRot >= 0 ? (growX ? t.Packed[bRot].W : t.Packed[bRot].H) : int.MaxValue;
          if (idx < bestIndex || (idx == bestIndex && growthDim < bestGrowthDim))
          {
            bestIndex = idx;
            bx = x;
            by = y;
            bRot = ri;
          }
        }
        else
        {
          rowHints[ri] = int.MaxValue; // the sheet only fills up — this rotation is done here
        }
      }

      return bestIndex != long.MaxValue;
    }

    /// <summary>
    /// PAIR CLUSTERING setup: for every rotation that has its 180° complement available, find the
    /// buddy offset that packs part + flipped copy into the smallest union bounding box (a triangle
    /// pair = a parallelogram). Column min/max profiles make the search O(W²) — exact for convex
    /// parts, conservative (never overlapping) for concave ones. When the pair is >= 15% denser
    /// than the single part, a PAIR MODULE mask (the union) is appended as an extra scan entry —
    /// rectangles and other non-interlocking shapes are untouched. Masks are the DILATED ones, so
    /// the spacing/halo clearance is built in.
    /// </summary>
    internal static void ComputePairing(PartType t)
    {
      int n = t.RotationsDeg.Length;
      if (t.Quantity < 2)
      {
        return;
      }

      var pairMasks = new List<RasterMask>();
      var pairRotLabel = new List<int>();
      var subA = new List<int>();
      var subB = new List<int>();
      var aOffX = new List<int>();
      var aOffY = new List<int>();
      var bOffX = new List<int>();
      var bOffY = new List<int>();

      (int[] Top, int[] Bot) Profiles(RasterMask m)
      {
        var top = new int[m.W];
        var bot = new int[m.W];
        for (int x = 0; x < m.W; x++)
        {
          top[x] = -1;
          bot[x] = int.MaxValue;
          for (int y = 0; y < m.H; y++)
          {
            if (m.Bits[(y * m.W) + x])
            {
              if (y < bot[x])
              {
                bot[x] = y;
              }

              if (y > top[x])
              {
                top[x] = y;
              }
            }
          }
        }

        return (top, bot);
      }

      for (int ri = 0; ri < n; ri++)
      {
        int want = (t.RotationsDeg[ri] + 180) % 360;
        int bi = System.Array.IndexOf(t.RotationsDeg, want);
        if (bi < 0)
        {
          continue; // complement not allowed (or deduped away for a symmetric part — pairing is pointless there)
        }

        var a = t.Masks[ri];
        var b = t.Masks[bi];
        var (topA, botA) = Profiles(a);
        var (topB, botB) = Profiles(b);

        long bestArea = long.MaxValue;
        int bestDx = 0, bestDy = 0;
        for (int dx = -b.W + 1; dx < a.W; dx++)
        {
          // Contact positions: B dropped from above until its columns rest on A, and pushed up from
          // below until its columns press under A (interval profiles => guaranteed no overlap).
          int dyAbove = int.MinValue;
          int dyBelow = int.MaxValue;
          bool anyColumn = false;
          int lo = System.Math.Max(0, dx);
          int hi = System.Math.Min(a.W, dx + b.W);
          for (int x = lo; x < hi; x++)
          {
            int xb = x - dx;
            if (topA[x] < 0 || topB[xb] < 0)
            {
              continue; // an empty column constrains nothing
            }

            anyColumn = true;
            dyAbove = System.Math.Max(dyAbove, topA[x] - botB[xb] + 1);
            dyBelow = System.Math.Min(dyBelow, botA[x] - topB[xb] - 1);
          }

          foreach (int dy in anyColumn ? new[] { dyAbove, dyBelow } : new[] { 0 })
          {
            long w = System.Math.Max(a.W, dx + b.W) - System.Math.Min(0, dx);
            long h = System.Math.Max(a.H, dy + b.H) - System.Math.Min(0, dy);
            long area = w * h;
            if (area < bestArea)
            {
              bestArea = area;
              bestDx = dx;
              bestDy = dy;
            }
          }
        }

        if (bestArea == long.MaxValue)
        {
          continue;
        }

        double singleDensity = (double)a.SolidCount / ((long)a.W * a.H);
        double pairDensity = (double)(a.SolidCount + b.SolidCount) / bestArea;
        NestProfile.Log($"pairing src={t.Source} rot={t.RotationsDeg[ri]} best=({bestDx},{bestDy}) single={singleDensity:0.000} pair={pairDensity:0.000} gate={(pairDensity >= singleDensity * 1.15 ? "ON" : "off")}");
        if (pairDensity < singleDensity * 1.15)
        {
          continue;
        }

        // Build the module mask: both dilated masks blitted into the union frame.
        int ax = System.Math.Max(0, -bestDx);
        int ay = System.Math.Max(0, -bestDy);
        int bx = System.Math.Max(0, bestDx);
        int by = System.Math.Max(0, bestDy);
        int pw = System.Math.Max(ax + a.W, bx + b.W);
        int ph = System.Math.Max(ay + a.H, by + b.H);
        var bits = new bool[pw * ph];
        int solid = 0;
        void Blit(RasterMask m, int ox, int oy)
        {
          for (int y = 0; y < m.H; y++)
          {
            for (int x = 0; x < m.W; x++)
            {
              if (m.Bits[(y * m.W) + x] && !bits[((oy + y) * pw) + ox + x])
              {
                bits[((oy + y) * pw) + ox + x] = true;
                solid++;
              }
            }
          }
        }

        Blit(a, ax, ay);
        Blit(b, bx, by);
        pairMasks.Add(new RasterMask { Bits = bits, W = pw, H = ph, SolidCount = solid });
        pairRotLabel.Add(t.RotationsDeg[ri]);
        subA.Add(ri);
        subB.Add(bi);
        aOffX.Add(ax);
        aOffY.Add(ay);
        bOffX.Add(bx);
        bOffY.Add(by);
      }

      if (pairMasks.Count == 0)
      {
        return;
      }

      t.FirstPairEntry = n;
      t.RotationsDeg = t.RotationsDeg.Concat(pairRotLabel).ToArray();
      t.Masks = t.Masks.Concat(pairMasks).ToArray();
      t.Packed = t.Packed.Concat(pairMasks.Select(PackedMask.From)).ToArray();
      t.PairSubA = subA.ToArray();
      t.PairSubB = subB.ToArray();
      t.PairAx = aOffX.ToArray();
      t.PairAy = aOffY.ToArray();
      t.PairBx = bOffX.ToArray();
      t.PairBy = bOffY.ToArray();
    }

    /// <summary>Profiling only (SHEETNEST_NEST_PROFILE=1): total Fits probes across a job.</summary>
    internal static long ProfFitsCalls;

    private static bool FindBottomLeft(ulong[] occ, int wpr, int sheetW, int sheetH, int insetPx, PackedMask m, int start, bool growX, out int ox, out int oy)
    {
      int hiY = sheetH - m.H - insetPx;
      int hiX = sheetW - m.W - insetPx;
      long probes = 0;

      try
      {
      if (growX)
      {
        // Fill full columns across the short (Y) dimension, growing along X — the remnant stays a
        // full-height strip at the right end of the sheet.
        for (int x = System.Math.Max(insetPx, start); x <= hiX; x++)
        {
          for (int y = insetPx; y <= hiY; y++)
          {
            probes++;
            if (Fits(occ, wpr, m, x, y))
            {
              ox = x;
              oy = y;
              return true;
            }
          }
        }
      }
      else
      {
        for (int y = System.Math.Max(insetPx, start); y <= hiY; y++)
        {
          for (int x = insetPx; x <= hiX; x++)
          {
            probes++;
            if (Fits(occ, wpr, m, x, y))
            {
              ox = x;
              oy = y;
              return true;
            }
          }
        }
      }

      ox = -1;
      oy = -1;
      return false;
      }
      finally
      {
        if (NestProfile.Enabled)
        {
          System.Threading.Interlocked.Add(ref ProfFitsCalls, probes);
        }
      }
    }

    // Word-packed collision test: AND 64 columns at a time (the mask row is shifted to the candidate
    // x on the fly, carrying bits across word boundaries). Same answer as the cell-by-cell scan.
    private static bool Fits(ulong[] occ, int wpr, PackedMask m, int ox, int oy)
    {
      int wordX = ox >> 6;
      int s = ox & 63;

      // DEEP-FAILURE GUARD: probes over mostly-empty space that collide only hundreds of rows up
      // used to walk every row below the obstruction first — for large (3D-unfolded) parts that made
      // one 4-rotation candidate take ~18 s while the 2-rotation ones took ~0.2 s (measured: same
      // probe count, ~100x the cost per probe). Sample a few spread rows first: any colliding sample
      // is an EXACT reject; obstructions are whole parts (hundreds of px tall), so a band taller
      // than H/4 can never hide between the samples. All samples clean -> full definitive scan.
      if (m.H >= 32)
      {
        if (RowCollides(occ, wpr, m, wordX, s, oy, 0)
            || RowCollides(occ, wpr, m, wordX, s, oy, m.H - 1)
            || RowCollides(occ, wpr, m, wordX, s, oy, m.H >> 1)
            || RowCollides(occ, wpr, m, wordX, s, oy, m.H >> 2)
            || RowCollides(occ, wpr, m, wordX, s, oy, (3 * m.H) >> 2))
        {
          return false;
        }
      }

      for (int py = 0; py < m.H; py++)
      {
        int mBase = py * m.WordsPerRow;
        int oBase = ((oy + py) * wpr) + wordX;
        ulong carry = 0;
        for (int wi = 0; wi < m.WordsPerRow; wi++)
        {
          ulong mw = m.Words[mBase + wi];
          ulong shifted = (mw << s) | carry;
          carry = s == 0 ? 0UL : mw >> (64 - s);
          if ((shifted & occ[oBase + wi]) != 0)
          {
            return false;
          }
        }

        if (carry != 0 && (carry & occ[oBase + m.WordsPerRow]) != 0)
        {
          return false;
        }
      }

      return true;
    }

    /// <summary>Collision test for a single mask row (same shifting as the full scan).</summary>
    private static bool RowCollides(ulong[] occ, int wpr, PackedMask m, int wordX, int s, int oy, int py)
    {
      int mBase = py * m.WordsPerRow;
      int oBase = ((oy + py) * wpr) + wordX;
      ulong carry = 0;
      for (int wi = 0; wi < m.WordsPerRow; wi++)
      {
        ulong mw = m.Words[mBase + wi];
        ulong shifted = (mw << s) | carry;
        carry = s == 0 ? 0UL : mw >> (64 - s);
        if ((shifted & occ[oBase + wi]) != 0)
        {
          return true;
        }
      }

      return carry != 0 && (carry & occ[oBase + m.WordsPerRow]) != 0;
    }

    internal static void Stamp(ulong[] occ, int wpr, PackedMask m, int ox, int oy)
    {
      int wordX = ox >> 6;
      int s = ox & 63;

      for (int py = 0; py < m.H; py++)
      {
        int mBase = py * m.WordsPerRow;
        int oBase = ((oy + py) * wpr) + wordX;
        ulong carry = 0;
        for (int wi = 0; wi < m.WordsPerRow; wi++)
        {
          ulong mw = m.Words[mBase + wi];
          occ[oBase + wi] |= (mw << s) | carry;
          carry = s == 0 ? 0UL : mw >> (64 - s);
        }

        if (carry != 0)
        {
          occ[oBase + m.WordsPerRow] |= carry;
        }
      }
    }
  }
}
