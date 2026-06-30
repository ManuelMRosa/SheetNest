namespace GpuNestLab
{
  using System.Collections.Generic;

  internal struct Placement
  {
    public int X;
    public int Y;
    public int PartIndex;
  }

  internal sealed class NestResult
  {
    public List<Placement> Placed;
    public int NotPlaced;
    public bool[] Occupancy;
    public int SheetW;
    public int SheetH;
    public int UsedHeight;
  }

  /// <summary>
  /// CPU baseline: bottom-left-fill placement on a bitmap occupancy grid. The inner position search
  /// (Fits over every candidate position) is the hot loop we will move to the GPU in Phase 3.
  /// </summary>
  internal static class RasterNester
  {
    public static NestResult Nest(RasterMask[] parts, int sheetW, int sheetH)
    {
      var occ = new bool[sheetW * sheetH];
      var placed = new List<Placement>();
      int notPlaced = 0;
      int usedHeight = 0;

      for (int pi = 0; pi < parts.Length; pi++)
      {
        var m = parts[pi];
        if (TryFindBottomLeft(occ, sheetW, sheetH, m, out int ox, out int oy))
        {
          Stamp(occ, sheetW, m, ox, oy);
          placed.Add(new Placement { X = ox, Y = oy, PartIndex = pi });
          if (oy + m.H > usedHeight)
          {
            usedHeight = oy + m.H;
          }
        }
        else
        {
          notPlaced++;
        }
      }

      return new NestResult { Placed = placed, NotPlaced = notPlaced, Occupancy = occ, SheetW = sheetW, SheetH = sheetH, UsedHeight = usedHeight };
    }

    private static bool TryFindBottomLeft(bool[] occ, int sheetW, int sheetH, RasterMask m, out int ox, out int oy)
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

    private static bool Fits(bool[] occ, int sheetW, RasterMask m, int ox, int oy)
    {
      for (int py = 0; py < m.H; py++)
      {
        int occRow = ((oy + py) * sheetW) + ox;
        int maskRow = py * m.W;
        for (int px = 0; px < m.W; px++)
        {
          if (m.Bits[maskRow + px] && occ[occRow + px])
          {
            return false;
          }
        }
      }

      return true;
    }

    private static void Stamp(bool[] occ, int sheetW, RasterMask m, int ox, int oy)
    {
      for (int py = 0; py < m.H; py++)
      {
        int occRow = ((oy + py) * sheetW) + ox;
        int maskRow = py * m.W;
        for (int px = 0; px < m.W; px++)
        {
          if (m.Bits[maskRow + px])
          {
            occ[occRow + px] = true;
          }
        }
      }
    }
  }
}
