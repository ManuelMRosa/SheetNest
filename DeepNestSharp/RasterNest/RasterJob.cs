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
    public static JobResult Nest(List<PartType> types, int sheetW, int sheetH, double pxPerInch, int marginPx)
    {
      // Dilate each part by ITS OWN spacing halo (PartType.HaloPx) so two parts end up exactly
      // (haloA + haloB) apart — per-part spacing / common-line (halo 0 = copies pack touching).
      // The real part sits inset by (halo, halo) inside its dilated mask.
      foreach (var t in types)
      {
        t.Masks = t.RotationsDeg
          .Select(r => RasterUtil.Dilate(RasterUtil.Rasterize(r == 0 ? t.Poly : t.Poly.Rotate(r), pxPerInch), t.HaloPx))
          .ToArray();
        t.Packed = t.Masks.Select(PackedMask.From).ToArray();
      }

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

      foreach (int ti in instances)
      {
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

          if (si == sheets.Count && rowHints.Count == si)
          {
            // Prospective new sheet: hints start at the margin row for every type/rotation. Kept even if
            // this attempt fails (a part that doesn't fit an EMPTY sheet never fits one).
            rowHints.Add(types.Select(tt => Enumerable.Repeat(marginInset + pad - tt.HaloPx, tt.Masks.Length).ToArray()).ToArray());
          }

          if (TryPlaceBest(occ, wpr, gw, gh, insetPx, t, rowHints[si][ti], growX, out int x, out int y, out int rotIdx))
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

            // Real part corner in TRUE-sheet coords = mask corner + halo − pad (the real part sits
            // +halo inside its dilated mask; the grid is padded by the largest halo).
            placements.Add(new JobPlacement { Source = t.Source, Sheet = si, Xpx = x + t.HaloPx - pad, Ypx = y + t.HaloPx - pad, RotationDeg = t.RotationsDeg[rotIdx] });
            solidPlaced += m.SolidCount;
            done = true;
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

      return new JobResult
      {
        Placements = placements,
        Sheets = sheets.Count,
        NotPlaced = notPlaced,
        NoOverlap = occSum == solidPlaced,
      };
    }

    private static bool TryPlaceBest(ulong[] occ, int wpr, int sheetW, int sheetH, int insetPx, PartType t, int[] rowHints, bool growX, out int bx, out int by, out int bRot)
    {
      bx = -1;
      by = -1;
      bRot = -1;
      long bestIndex = long.MaxValue;

      for (int ri = 0; ri < t.Packed.Length; ri++)
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
          if (idx < bestIndex)
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

    private static bool FindBottomLeft(ulong[] occ, int wpr, int sheetW, int sheetH, int insetPx, PackedMask m, int start, bool growX, out int ox, out int oy)
    {
      int hiY = sheetH - m.H - insetPx;
      int hiX = sheetW - m.W - insetPx;

      if (growX)
      {
        // Fill full columns across the short (Y) dimension, growing along X — the remnant stays a
        // full-height strip at the right end of the sheet.
        for (int x = System.Math.Max(insetPx, start); x <= hiX; x++)
        {
          for (int y = insetPx; y <= hiY; y++)
          {
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

    // Word-packed collision test: AND 64 columns at a time (the mask row is shifted to the candidate
    // x on the fly, carrying bits across word boundaries). Same answer as the cell-by-cell scan.
    private static bool Fits(ulong[] occ, int wpr, PackedMask m, int ox, int oy)
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

    private static void Stamp(ulong[] occ, int wpr, PackedMask m, int ox, int oy)
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
