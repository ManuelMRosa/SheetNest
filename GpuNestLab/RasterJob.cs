namespace GpuNestLab
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;

  /// <summary>A part geometry to nest: its polygon, how many, and which rotations are allowed.</summary>
  internal sealed class PartType
  {
    public int Source;
    public INfp Poly;
    public int Quantity;
    public int[] RotationsDeg;
    public RasterMask[] Masks; // one rasterized mask per allowed rotation (filled by the nester)
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
    public double Utilization;
    public bool NoOverlap;
    public Dictionary<int, int> RotationUse; // rotationDeg -> count
  }

  /// <summary>
  /// Generalized raster nester: multiple part geometries, each with a quantity and allowed rotations,
  /// packed across as many sheets as needed (bottom-left fill, biggest-first, best rotation per part).
  /// Collision search is the same bitmap test proven on the GPU in Phase 3 — here on CPU to verify the
  /// nesting LOGIC (rotations / multi-part / multi-sheet); the GPU path plugs into the same search.
  /// </summary>
  internal static class RasterJobNester
  {
    public static JobResult Nest(List<PartType> types, int sheetW, int sheetH, double pxPerInch)
    {
      foreach (var t in types)
      {
        t.Masks = t.RotationsDeg
          .Select(r => RasterUtil.Rasterize(r == 0 ? t.Poly : t.Poly.Rotate(r), pxPerInch))
          .ToArray();
      }

      // Place the biggest parts first (by net area).
      var instances = new List<int>();
      for (int ti = 0; ti < types.Count; ti++)
      {
        for (int q = 0; q < types[ti].Quantity; q++)
        {
          instances.Add(ti);
        }
      }

      instances.Sort((a, b) => types[b].Masks[0].SolidCount.CompareTo(types[a].Masks[0].SolidCount));

      var sheets = new List<byte[]>();
      var placements = new List<JobPlacement>();
      var rotUse = new Dictionary<int, int>();
      int notPlaced = 0;
      long solidPlaced = 0;

      foreach (int ti in instances)
      {
        var t = types[ti];
        bool done = false;

        // Try every open sheet, then (if needed) a fresh one.
        for (int si = 0; si <= sheets.Count && !done; si++)
        {
          byte[] occ = si < sheets.Count ? sheets[si] : new byte[sheetW * sheetH];
          if (TryPlaceBest(occ, sheetW, sheetH, t, out int x, out int y, out int rotIdx))
          {
            if (si == sheets.Count)
            {
              sheets.Add(occ);
            }

            var m = t.Masks[rotIdx];
            Stamp(occ, sheetW, m, x, y);
            int rotDeg = t.RotationsDeg[rotIdx];
            placements.Add(new JobPlacement { Source = t.Source, Sheet = si, Xpx = x, Ypx = y, RotationDeg = rotDeg });
            rotUse.TryGetValue(rotDeg, out int rc);
            rotUse[rotDeg] = rc + 1;
            solidPlaced += m.SolidCount;
            done = true;
          }
        }

        if (!done)
        {
          notPlaced++;
        }
      }

      long occSum = sheets.Sum(s => (long)s.Count(b => b != 0));
      double util = sheets.Count > 0 ? (double)solidPlaced / ((double)sheets.Count * sheetW * sheetH) : 0;

      return new JobResult
      {
        Placements = placements,
        Sheets = sheets.Count,
        NotPlaced = notPlaced,
        Utilization = util,
        NoOverlap = occSum == solidPlaced,
        RotationUse = rotUse,
      };
    }

    /// <summary>Bottom-left over all allowed rotations; pick the rotation+position that sits lowest-left.</summary>
    private static bool TryPlaceBest(byte[] occ, int sheetW, int sheetH, PartType t, out int bx, out int by, out int bRot)
    {
      bx = -1;
      by = -1;
      bRot = -1;
      long bestIndex = long.MaxValue;

      for (int ri = 0; ri < t.Masks.Length; ri++)
      {
        var m = t.Masks[ri];
        if (m.W > sheetW || m.H > sheetH)
        {
          continue;
        }

        if (FindBottomLeft(occ, sheetW, sheetH, m, out int x, out int y))
        {
          long idx = ((long)y * sheetW) + x; // lower y, then lower x
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

    private static bool FindBottomLeft(byte[] occ, int sheetW, int sheetH, RasterMask m, out int ox, out int oy)
    {
      for (int y = 0; y + m.H <= sheetH; y++)
      {
        for (int x = 0; x + m.W <= sheetW; x++)
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
