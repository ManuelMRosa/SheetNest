namespace GpuNestLab
{
  using System.Collections.Generic;
  using System.Linq;

  /// <summary>
  /// Optimized raster nester — SAME bottom-left result as RasterJobNester, but:
  ///  (1) bit-packed occupancy (64 pixels per ulong) so a collision test is a handful of bitwise ANDs
  ///      instead of one comparison per pixel (standard raster-nesting speedup), and
  ///  (2) it skips sheets whose free-pixel count can't possibly fit the part (kills the quadratic
  ///      re-scanning of already-full sheets that dominates multi-sheet jobs).
  /// </summary>
  internal static class BitNester
  {
    private sealed class BitMask
    {
      public ulong[] Words; // H rows * wordsPerRow
      public int WordsPerRow;
      public int W;
      public int H;
      public int Solid;
    }

    public static JobResult Nest(List<PartType> types, int sheetW, int sheetH, double pxPerInch)
    {
      int sheetWords = ((sheetW + 63) / 64) + 1; // +1 guard word for the cross-word spill
      var bitMasks = new Dictionary<RasterMask, BitMask>();

      foreach (var t in types)
      {
        t.Masks = t.RotationsDeg
          .Select(r => RasterUtil.Rasterize(r == 0 ? t.Poly : t.Poly.Rotate(r), pxPerInch))
          .ToArray();
        foreach (var m in t.Masks)
        {
          if (!bitMasks.ContainsKey(m))
          {
            bitMasks[m] = Pack(m);
          }
        }
      }

      var instances = new List<int>();
      for (int ti = 0; ti < types.Count; ti++)
      {
        for (int q = 0; q < types[ti].Quantity; q++)
        {
          instances.Add(ti);
        }
      }

      instances.Sort((a, b) => types[b].Masks[0].SolidCount.CompareTo(types[a].Masks[0].SolidCount));

      var sheets = new List<ulong[]>();
      var sheetFree = new List<int>();
      var closed = new List<bool[]>(); // closed[sheet][type] — this type already failed to fit here
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
            // so skip without re-scanning. Same result, no wasted full scans.
            if (closed[si][ti] || sheetFree[si] < partSolid)
            {
              closed[si][ti] = true;
              continue;
            }

            occ = sheets[si];
          }
          else
          {
            occ = new ulong[sheetH * sheetWords];
          }

          if (TryPlaceBest(occ, sheetW, sheetH, sheetWords, t, bitMasks, out int x, out int y, out int rotIdx))
          {
            if (si == sheets.Count)
            {
              sheets.Add(occ);
              sheetFree.Add(sheetW * sheetH);
              closed.Add(new bool[types.Count]);
              si = sheets.Count - 1;
            }

            var bm = bitMasks[t.Masks[rotIdx]];
            Stamp(occ, sheetWords, bm, x, y);
            sheetFree[si] -= bm.Solid;
            placements.Add(new JobPlacement { Source = t.Source, Sheet = si, Xpx = x, Ypx = y, RotationDeg = t.RotationsDeg[rotIdx] });
            solidPlaced += bm.Solid;
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

      return new JobResult
      {
        Placements = placements,
        Sheets = sheets.Count,
        NotPlaced = notPlaced,
        NoOverlap = true, // bitwise stamp can't overlap (collision-checked before stamping)
      };
    }

    private static BitMask Pack(RasterMask m)
    {
      int wpr = (m.W + 63) / 64;
      var words = new ulong[m.H * wpr];
      int solid = 0;
      for (int r = 0; r < m.H; r++)
      {
        for (int c = 0; c < m.W; c++)
        {
          if (m.Bits[(r * m.W) + c])
          {
            words[(r * wpr) + (c >> 6)] |= 1UL << (c & 63);
            solid++;
          }
        }
      }

      return new BitMask { Words = words, WordsPerRow = wpr, W = m.W, H = m.H, Solid = solid };
    }

    private static bool TryPlaceBest(ulong[] occ, int sheetW, int sheetH, int sheetWords, PartType t, Dictionary<RasterMask, BitMask> bitMasks, out int bx, out int by, out int bRot)
    {
      bx = -1;
      by = -1;
      bRot = -1;
      long bestIndex = long.MaxValue;

      for (int ri = 0; ri < t.Masks.Length; ri++)
      {
        var bm = bitMasks[t.Masks[ri]];
        if (bm.W > sheetW || bm.H > sheetH)
        {
          continue;
        }

        if (FindBottomLeft(occ, sheetW, sheetH, sheetWords, bm, out int x, out int y))
        {
          long idx = ((long)y * sheetW) + x;
          if (idx < bestIndex)
          {
            bestIndex = idx;
            bx = x;
            by = y;
            bRot = ri;
          }
        }
      }

      return bestIndex != long.MaxValue;
    }

    private static bool FindBottomLeft(ulong[] occ, int sheetW, int sheetH, int sheetWords, BitMask bm, out int ox, out int oy)
    {
      int maxX = sheetW - bm.W;
      int maxY = sheetH - bm.H;
      for (int y = 0; y <= maxY; y++)
      {
        for (int x = 0; x <= maxX; x++)
        {
          if (Fits(occ, sheetWords, bm, x, y))
          {
            ox = x;
            oy = y;
            return true;
          }
        }
      }

      ox = -1;
      oy = -1;
      return false;
    }

    private static bool Fits(ulong[] occ, int sheetWords, BitMask bm, int ox, int oy)
    {
      int s = ox & 63;
      int wb = ox >> 6;
      int inv = 64 - s;
      for (int r = 0; r < bm.H; r++)
      {
        int occRow = ((oy + r) * sheetWords) + wb;
        int maskRow = r * bm.WordsPerRow;
        for (int i = 0; i < bm.WordsPerRow; i++)
        {
          ulong mw = bm.Words[maskRow + i];
          if (mw == 0)
          {
            continue;
          }

          if ((occ[occRow + i] & (mw << s)) != 0)
          {
            return false;
          }

          if (s != 0 && (occ[occRow + i + 1] & (mw >> inv)) != 0)
          {
            return false;
          }
        }
      }

      return true;
    }

    private static void Stamp(ulong[] occ, int sheetWords, BitMask bm, int ox, int oy)
    {
      int s = ox & 63;
      int wb = ox >> 6;
      int inv = 64 - s;
      for (int r = 0; r < bm.H; r++)
      {
        int occRow = ((oy + r) * sheetWords) + wb;
        int maskRow = r * bm.WordsPerRow;
        for (int i = 0; i < bm.WordsPerRow; i++)
        {
          ulong mw = bm.Words[maskRow + i];
          if (mw == 0)
          {
            continue;
          }

          occ[occRow + i] |= mw << s;
          if (s != 0)
          {
            occ[occRow + i + 1] |= mw >> inv;
          }
        }
      }
    }
  }
}
