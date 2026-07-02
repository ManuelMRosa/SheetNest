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
    public int Priority = 5; // 0-10, higher nests first (Radan-style)
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
    public static JobResult Nest(List<PartType> types, int sheetW, int sheetH, double pxPerInch, int haloPx, int marginPx)
    {
      // Dilate each part by spacing/2 (haloPx) so footprints kept `spacing` apart → the real parts are
      // `spacing` apart. The real part sits inset by (haloPx, haloPx) inside the dilated mask.
      foreach (var t in types)
      {
        t.Masks = t.RotationsDeg
          .Select(r => RasterUtil.Dilate(RasterUtil.Rasterize(r == 0 ? t.Poly : t.Poly.Rotate(r), pxPerInch), haloPx))
          .ToArray();
      }

      // Pad the grid by haloPx on every side so a part's spacing-halo can extend PAST the sheet edge into a
      // virtual border (no neighbour to keep clear of out there). The real part then reaches the true edge at
      // margin 0 (verified in GpuNestLab: flush, no overlap). insetPx = margin; real-part corner nets to x.
      int pad = haloPx;
      int gw = sheetW + (2 * pad);
      int gh = sheetH + (2 * pad);
      int insetPx = marginPx;

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

      var sheets = new List<byte[]>();
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
          byte[] occ;
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
            occ = new byte[gw * gh];
          }

          if (TryPlaceBest(occ, gw, gh, insetPx, t, out int x, out int y, out int rotIdx))
          {
            if (si == sheets.Count)
            {
              sheets.Add(occ);
              sheetFree.Add(gw * gh);
              closed.Add(new bool[types.Count]);
              si = sheets.Count - 1;
            }

            var m = t.Masks[rotIdx];
            Stamp(occ, gw, m, x, y);
            sheetFree[si] -= m.SolidCount;

            // Real part corner in TRUE-sheet coords = mask corner x (real part sits +haloPx in the mask; the
            // haloPx pad subtracts back out → x). Verified flush at margin 0 with no overlap.
            placements.Add(new JobPlacement { Source = t.Source, Sheet = si, Xpx = x, Ypx = y, RotationDeg = t.RotationsDeg[rotIdx] });
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

      long occSum = sheets.Sum(s => (long)s.Count(b => b != 0));
      return new JobResult
      {
        Placements = placements,
        Sheets = sheets.Count,
        NotPlaced = notPlaced,
        NoOverlap = occSum == solidPlaced,
      };
    }

    private static bool TryPlaceBest(byte[] occ, int sheetW, int sheetH, int insetPx, PartType t, out int bx, out int by, out int bRot)
    {
      bx = -1;
      by = -1;
      bRot = -1;
      long bestIndex = long.MaxValue;

      for (int ri = 0; ri < t.Masks.Length; ri++)
      {
        var m = t.Masks[ri];
        if (m.W + (2 * insetPx) > sheetW || m.H + (2 * insetPx) > sheetH)
        {
          continue;
        }

        if (FindBottomLeft(occ, sheetW, sheetH, insetPx, m, out int x, out int y))
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

    private static bool FindBottomLeft(byte[] occ, int sheetW, int sheetH, int insetPx, RasterMask m, out int ox, out int oy)
    {
      int hiY = sheetH - m.H - insetPx;
      int hiX = sheetW - m.W - insetPx;
      for (int y = insetPx; y <= hiY; y++)
      {
        for (int x = insetPx; x <= hiX; x++)
        {
          if (Fits(occ, sheetW, m, x, y))
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

    private static bool Fits(byte[] occ, int sheetW, RasterMask m, int ox, int oy)
    {
      for (int py = 0; py < m.H; py++)
      {
        int occRow = ((oy + py) * sheetW) + ox;
        int maskRow = py * m.W;
        for (int px = 0; px < m.W; px++)
        {
          if (m.Bits[maskRow + px] && occ[occRow + px] != 0)
          {
            return false;
          }
        }
      }

      return true;
    }

    private static void Stamp(byte[] occ, int sheetW, RasterMask m, int ox, int oy)
    {
      for (int py = 0; py < m.H; py++)
      {
        int occRow = ((oy + py) * sheetW) + ox;
        int maskRow = py * m.W;
        for (int px = 0; px < m.W; px++)
        {
          if (m.Bits[maskRow + px])
          {
            occ[occRow + px] = 1;
          }
        }
      }
    }
  }
}
