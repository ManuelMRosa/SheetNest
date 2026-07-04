namespace DeepNestSharp.RasterNest
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

  /// <summary>
  /// Experimental raster (bitmap) nesting — research path for GPU acceleration (see GpuNestLab).
  /// Rasterizes a polygon (outer contour + holes) via scanline even-odd fill; holes turn the interior
  /// off automatically.
  /// </summary>
  internal static class RasterUtil
  {
    public static RasterMask Rasterize(INfp poly, double pxPerUnit)
    {
      double minX = poly.Points.Min(p => p.X);
      double minY = poly.Points.Min(p => p.Y);

      var contours = new List<double[]> { ToPixels(poly, minX, minY, pxPerUnit) };
      if (poly.Children != null)
      {
        foreach (var c in poly.Children)
        {
          contours.Add(ToPixels(c, minX, minY, pxPerUnit));
        }
      }

      double maxX = poly.Points.Max(p => p.X);
      double maxY = poly.Points.Max(p => p.Y);
      int w = Math.Max(1, (int)Math.Ceiling((maxX - minX) * pxPerUnit));
      int h = Math.Max(1, (int)Math.Ceiling((maxY - minY) * pxPerUnit));
      return Fill(contours, w, h);
    }

    /// <summary>
    /// Rasterize the polygon TRANSLATED by (<paramref name="offX"/>, <paramref name="offY"/>) with the
    /// mask's pixel edges aligned to the SHEET grid (origin returned in <paramref name="gx"/>/<paramref name="gy"/>,
    /// in sheet pixels). <see cref="Rasterize"/> aligns pixel 0 to the part's own bbox corner, so a mask
    /// stamped at a fractional position under-covers by up to a pixel; this variant bakes the exact
    /// offset into the contours instead, keeping the mask a true superset of the placed part. Used by
    /// the post-compaction refill to rebuild sheet occupancy from exact (sub-pixel) positions.
    /// </summary>
    public static RasterMask RasterizeAligned(INfp poly, double offX, double offY, double pxPerUnit, out int gx, out int gy)
    {
      double minX = (poly.Points.Min(p => p.X) + offX) * pxPerUnit;
      double minY = (poly.Points.Min(p => p.Y) + offY) * pxPerUnit;
      double maxX = (poly.Points.Max(p => p.X) + offX) * pxPerUnit;
      double maxY = (poly.Points.Max(p => p.Y) + offY) * pxPerUnit;
      gx = (int)Math.Floor(minX);
      gy = (int)Math.Floor(minY);
      int w = Math.Max(1, (int)Math.Ceiling(maxX) - gx);
      int h = Math.Max(1, (int)Math.Ceiling(maxY) - gy);

      var contours = new List<double[]> { ToGridPixels(poly, offX, offY, gx, gy, pxPerUnit) };
      if (poly.Children != null)
      {
        foreach (var c in poly.Children)
        {
          contours.Add(ToGridPixels(c, offX, offY, gx, gy, pxPerUnit));
        }
      }

      return Fill(contours, w, h);
    }

    private static RasterMask Fill(List<double[]> contours, int w, int h)
    {
      var bits = new bool[w * h];
      int solid = 0;
      var xs = new List<double>();
      for (int py = 0; py < h; py++)
      {
        double sy = py + 0.5;
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

      // Conservative outline stamp: the scanline fill above only sets pixels whose CENTRE is inside, so
      // features thinner than one pixel (acute tips, narrow slivers) can rasterize to NOTHING and vanish
      // from the collision mask — another part could then be placed straight through real material. Also
      // stamp every pixel the contour itself passes through, so the mask always covers the true outline.
      // This makes the mask a superset of the real part, which is what lets spacing 0 pack parts touching
      // WITHOUT any risk of real material overlap (no safety halo needed). 4 samples per pixel of segment
      // length so a segment can't clip a pixel corner between two samples.
      foreach (var c in contours)
      {
        int n = c.Length / 2;
        for (int i = 0; i < n; i++)
        {
          int j = (i + 1) % n;
          double ax = c[2 * i];
          double ay = c[(2 * i) + 1];
          double bx = c[2 * j];
          double by = c[(2 * j) + 1];
          int steps = Math.Max(1, (int)Math.Ceiling(4 * Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay))));
          for (int s = 0; s <= steps; s++)
          {
            double t = (double)s / steps;
            int px = Math.Min(w - 1, Math.Max(0, (int)Math.Floor(ax + (t * (bx - ax)))));
            int py2 = Math.Min(h - 1, Math.Max(0, (int)Math.Floor(ay + (t * (by - ay)))));
            int bi = (py2 * w) + px;
            if (!bits[bi])
            {
              bits[bi] = true;
              solid++;
            }
          }
        }
      }

      return new RasterMask { Bits = bits, W = w, H = h, SolidCount = solid };
    }

    /// <summary>
    /// Dilate (grow) a mask by <paramref name="r"/> pixels using a EUCLIDEAN (circular) structuring element,
    /// NOT a square one. A square (Chebyshev) kernel over-protects diagonal edges by up to 1.41×, so parts
    /// with angled edges end up spaced far more than asked (e.g. 0.5" instead of the set spacing). A circular
    /// kernel keeps the clearance uniform = the set spacing regardless of edge angle. Two parts each dilated
    /// by spacing/2 end up `spacing` apart; the original sits inset by (r, r) inside the returned mask.
    /// </summary>
    public static RasterMask Dilate(RasterMask m, int r)
    {
      if (r <= 0)
      {
        return m;
      }

      // Precompute the circular structuring element offsets once. The bound is r² + r (≈ (r+0.5)²), NOT
      // a plain r²: with r=1 (the enforced minimum halo) a plain ≤ r² disk degenerates to a plus shape
      // with ZERO diagonal reach, so 45° corners of adjacent parts could land essentially touching. The
      // extra half pixel keeps the reach uniform in every direction, erring on the safe side.
      var disk = new List<(int Dx, int Dy)>();
      int r2 = (r * r) + r;
      for (int dy = -r; dy <= r; dy++)
      {
        for (int dx = -r; dx <= r; dx++)
        {
          if ((dx * dx) + (dy * dy) <= r2)
          {
            disk.Add((dx, dy));
          }
        }
      }

      int nw = m.W + (2 * r);
      int nh = m.H + (2 * r);
      var bits = new bool[nw * nh];

      // Pass 1: the dilation always contains the original body — copy it straight across.
      for (int oy = 0; oy < m.H; oy++)
      {
        for (int ox = 0; ox < m.W; ox++)
        {
          if (m.Bits[(oy * m.W) + ox])
          {
            bits[((oy + r) * nw) + ox + r] = true;
          }
        }
      }

      // Pass 2: stamp the disk only around BOUNDARY pixels (solid with an empty 4-neighbour or on the
      // mask edge). Every dilated cell is within r of some boundary pixel, so the result is identical to
      // stamping every solid pixel — but O(perimeter·r²) instead of O(area·r²), so a large spacing no
      // longer makes the nest appear to hang.
      for (int oy = 0; oy < m.H; oy++)
      {
        for (int ox = 0; ox < m.W; ox++)
        {
          if (!m.Bits[(oy * m.W) + ox])
          {
            continue;
          }

          bool boundary = ox == 0 || ox == m.W - 1 || oy == 0 || oy == m.H - 1
            || !m.Bits[(oy * m.W) + ox - 1]
            || !m.Bits[(oy * m.W) + ox + 1]
            || !m.Bits[((oy - 1) * m.W) + ox]
            || !m.Bits[((oy + 1) * m.W) + ox];
          if (!boundary)
          {
            continue;
          }

          int cx = ox + r;
          int cy = oy + r;
          foreach (var (dx, dy) in disk)
          {
            bits[((cy + dy) * nw) + cx + dx] = true;
          }
        }
      }

      int solid = 0;
      for (int i = 0; i < bits.Length; i++)
      {
        if (bits[i])
        {
          solid++;
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

    private static double[] ToGridPixels(INfp contour, double offX, double offY, int gx, int gy, double pxPerUnit)
    {
      var pts = contour.Points;
      var arr = new double[pts.Length * 2];
      for (int i = 0; i < pts.Length; i++)
      {
        arr[2 * i] = ((pts[i].X + offX) * pxPerUnit) - gx;
        arr[(2 * i) + 1] = ((pts[i].Y + offY) * pxPerUnit) - gy;
      }

      return arr;
    }
  }
}
