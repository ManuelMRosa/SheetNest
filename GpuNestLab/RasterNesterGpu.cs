namespace GpuNestLab
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Threading.Tasks;
  using ILGPU;
  using ILGPU.Runtime;

  /// <summary>
  /// Phase 3: the placement-search hot loop on the GPU. For a part it computes the full COLLISION MAP
  /// — for every candidate position, does the part's mask overlap the occupancy? — in parallel on the
  /// RTX 3060. The same map computed on multi-core CPU (Parallel.For) is the baseline. Computing the
  /// whole map (not first-fit) is what a thorough best-position / multi-rotation search needs, and is
  /// exactly the embarrassingly-parallel work GPUs are good at.
  /// </summary>
  internal sealed class RasterNesterGpu : IDisposable
  {
    private readonly Accelerator accel;
    private readonly Action<Index1D, ArrayView<byte>, ArrayView<int>, int, int, ArrayView<byte>> kernel;

    public RasterNesterGpu(Accelerator accel)
    {
      this.accel = accel;
      this.kernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<int>, int, int, ArrayView<byte>>(CollisionKernel);
    }

    private static void CollisionKernel(Index1D idx, ArrayView<byte> occ, ArrayView<int> offs, int sheetW, int validW, ArrayView<byte> outMap)
    {
      int py = idx / validW;
      int px = idx - (py * validW);
      int basePos = (py * sheetW) + px;
      byte collide = 0;
      for (int k = 0; k < offs.Length; k++)
      {
        if (occ[basePos + offs[k]] != 0)
        {
          collide = 1;
          break;
        }
      }

      outMap[idx] = collide;
    }

    public NestResult Nest(RasterMask mask, int copies, int sheetW, int sheetH, bool useGpu, out double mapMillis)
    {
      // Solid mask pixels as occupancy offsets relative to a placement's base index.
      var offsList = new List<int>(mask.SolidCount);
      for (int my = 0; my < mask.H; my++)
      {
        for (int mx = 0; mx < mask.W; mx++)
        {
          if (mask.Bits[(my * mask.W) + mx])
          {
            offsList.Add((my * sheetW) + mx);
          }
        }
      }

      int[] offs = offsList.ToArray();
      int validW = sheetW - mask.W + 1;
      int validH = sheetH - mask.H + 1;
      int positions = validW * validH;

      var occ = new byte[sheetW * sheetH];
      var map = new byte[positions];

      MemoryBuffer1D<byte, Stride1D.Dense> occBuf = null;
      MemoryBuffer1D<int, Stride1D.Dense> offBuf = null;
      MemoryBuffer1D<byte, Stride1D.Dense> mapBuf = null;
      if (useGpu)
      {
        occBuf = this.accel.Allocate1D<byte>(occ.Length);
        offBuf = this.accel.Allocate1D(offs);
        mapBuf = this.accel.Allocate1D<byte>(positions);
      }

      var placed = new List<Placement>();
      int notPlaced = 0;
      int usedHeight = 0;
      double mt = 0;
      var sw = new Stopwatch();

      for (int c = 0; c < copies; c++)
      {
        sw.Restart();
        if (useGpu)
        {
          occBuf.CopyFromCPU(occ);
          this.kernel(positions, occBuf.View, offBuf.View, sheetW, validW, mapBuf.View);
          this.accel.Synchronize();
          mapBuf.CopyToCPU(map);
        }
        else
        {
          Parallel.For(0, positions, i =>
          {
            int py = i / validW;
            int px = i - (py * validW);
            int basePos = (py * sheetW) + px;
            byte collide = 0;
            for (int k = 0; k < offs.Length; k++)
            {
              if (occ[basePos + offs[k]] != 0)
              {
                collide = 1;
                break;
              }
            }

            map[i] = collide;
          });
        }

        sw.Stop();
        mt += sw.Elapsed.TotalMilliseconds;

        int found = -1;
        for (int i = 0; i < positions; i++)
        {
          if (map[i] == 0)
          {
            found = i;
            break;
          }
        }

        if (found < 0)
        {
          notPlaced++;
          continue;
        }

        int fy = found / validW;
        int fx = found - (fy * validW);
        int b = (fy * sheetW) + fx;
        for (int k = 0; k < offs.Length; k++)
        {
          occ[b + offs[k]] = 1;
        }

        placed.Add(new Placement { X = fx, Y = fy, PartIndex = c });
        if (fy + mask.H > usedHeight)
        {
          usedHeight = fy + mask.H;
        }
      }

      occBuf?.Dispose();
      offBuf?.Dispose();
      mapBuf?.Dispose();

      var occBool = new bool[occ.Length];
      for (int i = 0; i < occ.Length; i++)
      {
        occBool[i] = occ[i] != 0;
      }

      mapMillis = mt;
      return new NestResult { Placed = placed, NotPlaced = notPlaced, Occupancy = occBool, SheetW = sheetW, SheetH = sheetH, UsedHeight = usedHeight };
    }

    public void Dispose()
    {
    }
  }
}
