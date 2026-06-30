namespace GpuNestLab
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using DeepNestLib;

  /// <summary>A part rasterized to a solid/empty bitmask (row-major, true = material).</summary>
  internal sealed class RasterMask
  {
    public bool[] Bits;
    public int W;
    public int H;
    public int SolidCount;
  }

  internal static class RasterUtil
  {
    /// <summary>
    /// Rasterize a polygon (outer contour + holes) to a solid mask at <paramref name="pxPerUnit"/>
    /// resolution, via scanline even-odd fill — holes turn the interior off automatically.
    /// </summary>
    public static RasterMask Rasterize(INfp poly, double pxPerUnit)
    {
      double minX = poly.Points.Min(p => p.X);
      double minY = poly.Points.Min(p => p.Y);
      double maxX = poly.Points.Max(p => p.X);
      double maxY = poly.Points.Max(p => p.Y);

      int w = Math.Max(1, (int)Math.Ceiling((maxX - minX) * pxPerUnit));
      int h = Math.Max(1, (int)Math.Ceiling((maxY - minY) * pxPerUnit));
      var bits = new bool[w * h];

      var contours = new List<double[]> { ToPixels(poly, minX, minY, pxPerUnit) };
      if (poly.Children != null)
      {
        foreach (var c in poly.Children)
        {
          contours.Add(ToPixels(c, minX, minY, pxPerUnit));
        }
      }

      int solid = 0;
      var xs = new List<double>();
      for (int py = 0; py < h; py++)
      {
        double sy = py + 0.5; // sample at pixel centre
        xs.Clear();
        foreach (var c in contours)
        {
          int n = c.Length / 2;
          for (int i = 0; i < n; i++)
          {
            double ay = c[(2 * i) + 1];
            int j = (i + 1) % n;
            double by = c[(2 * j) + 1];
            if ((ay <= sy && by > sy) || (by <= sy && ay > sy))
            {
              double ax = c[2 * i];
              double bx = c[2 * j];
              double t = (sy - ay) / (by - ay);
              xs.Add(ax + (t * (bx - ax)));
            }
          }
        }

        xs.Sort();
        for (int k = 0; k + 1 < xs.Count; k += 2)
        {
          int x0 = Math.Max(0, (int)Math.Ceiling(xs[k] - 0.5));
          int x1 = Math.Min(w - 1, (int)Math.Floor(xs[k + 1] - 0.5));
          int rowBase = py * w;
          for (int px = x0; px <= x1; px++)
          {
            if (!bits[rowBase + px])
            {
              bits[rowBase + px] = true;
              solid++;
            }
          }
        }
      }

      return new RasterMask { Bits = bits, W = w, H = h, SolidCount = solid };
    }

    /// <summary>
    /// Dilate (grow) a mask by <paramref name="r"/> pixels on every side — the part's keep-clear
    /// footprint for part spacing. Two parts each dilated by spacing/2 px end up exactly `spacing`
    /// apart. The original part sits inset by (r, r) inside the returned mask.
    /// </summary>
    public static RasterMask Dilate(RasterMask m, int r)
    {
      if (r <= 0)
      {
        return m;
      }

      int nw = m.W + (2 * r);
      int nh = m.H + (2 * r);
      var bits = new bool[nw * nh];
      int solid = 0;
      int span = 2 * r;
      for (int oy = 0; oy < m.H; oy++)
      {
        for (int ox = 0; ox < m.W; ox++)
        {
          if (!m.Bits[(oy * m.W) + ox])
          {
            continue;
          }

          for (int dy = 0; dy <= span; dy++)
          {
            int rowBase = (oy + dy) * nw;
            for (int dx = 0; dx <= span; dx++)
            {
              int ni = rowBase + ox + dx;
              if (!bits[ni])
              {
                bits[ni] = true;
                solid++;
              }
            }
          }
        }
      }

      return new RasterMask { Bits = bits, W = nw, H = nh, SolidCount = solid };
    }

    private static double[] ToPixels(INfp contour, double minX, double minY, double pxPerUnit)
    {
      var pts = contour.Points;
      var arr = new double[pts.Length * 2];
      for (int i = 0; i < pts.Length; i++)
      {
        arr[2 * i] = (pts[i].X - minX) * pxPerUnit;
        arr[(2 * i) + 1] = (pts[i].Y - minY) * pxPerUnit;
      }

      return arr;
    }
  }
}
