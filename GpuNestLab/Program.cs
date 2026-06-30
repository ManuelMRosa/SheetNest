namespace GpuNestLab
{
  using System;
  using System.Diagnostics;
  using System.Linq;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.Placement;
  using ILGPU;
  using ILGPU.Runtime;

  /// <summary>
  /// GPU Nest Lab — isolated R&amp;D for a raster/bitmap nesting engine.
  /// Phase 1: GPU foundation (ILGPU/CUDA on the RTX 3060).
  /// Phase 2: CPU raster nester (rasterize parts → bitmask, bottom-left-fill placement).
  /// </summary>
  internal static class Program
  {
    private static void Main()
    {
      PrintGpu();
      Console.WriteLine();
      RasterNestPhase2();
      RasterNestPhase3();
      RasterNestPhase4a();
      VerifyConversion();
      OptimizationBenchmark();
      VerifyDilate();
    }

    private static void VerifyDilate()
    {
      Console.WriteLine("\n=== Spacing prep: verify Dilate (part footprint for spacing) ===");
      var holed = LoadHoledPart();
      var m = RasterUtil.Rasterize(holed, 8.0);
      const int r = 4; // e.g. spacing 1" at 8px/in → halo 4px each side → 1" gap between parts
      var d = RasterUtil.Dilate(m, r);

      int fx = -1;
      int fy = -1;
      for (int yy = 0; yy < m.H && fy < 0; yy++)
      {
        for (int xx = 0; xx < m.W; xx++)
        {
          if (m.Bits[(yy * m.W) + xx])
          {
            fx = xx;
            fy = yy;
            break;
          }
        }
      }

      bool dimOk = d.W == m.W + (2 * r) && d.H == m.H + (2 * r);
      bool insetOk = fx >= 0 && d.Bits[((fy + r) * d.W) + (fx + r)];        // orig pixel → inset by r
      bool haloOk = fx >= 0 && d.Bits[(fy * d.W) + fx];                      // halo grown outward
      Console.WriteLine($"orig {m.W}×{m.H} → dilated {d.W}×{d.H} (expect {m.W + 2 * r}×{m.H + 2 * r})");
      Console.WriteLine($"dimensions: {(dimOk ? "PASS ✓" : "FAIL ✗")}   part inset by r: {(insetOk ? "PASS ✓" : "FAIL ✗")}   halo present: {(haloOk ? "PASS ✓" : "FAIL ✗")}");
      Console.WriteLine($"→ 2 parts placed adjacent will sit 2r = {2 * r}px = {2 * r / 8.0:F3}\" apart (the spacing).");
    }

    private static void OptimizationBenchmark()
    {
      Console.WriteLine("\n=== Phase 4c: optimization (bit-packing + skip-full-sheets) ===");
      var holed = LoadPart(ScratchDir + @"\holed_test\holed.dxf", 0);
      var strip = LoadPart(ScratchDir + @"\strip_test\strip.dxf", 1);
      if (holed == null || strip == null)
      {
        Console.WriteLine("Failed to load parts.");
        return;
      }

      const double px = 8.0;
      int sheetW = (int)(48 * px);
      int sheetH = (int)(96 * px);

      System.Collections.Generic.List<PartType> MakeTypes() => new System.Collections.Generic.List<PartType>
      {
        new PartType { Source = 0, Poly = holed, Quantity = 640, RotationsDeg = new[] { 0, 90 } },
        new PartType { Source = 1, Poly = strip, Quantity = 160, RotationsDeg = new[] { 0, 90 } },
      };

      var sw = System.Diagnostics.Stopwatch.StartNew();
      var slow = RasterJobNester.Nest(MakeTypes(), sheetW, sheetH, px);
      sw.Stop();
      long slowMs = sw.ElapsedMilliseconds;

      sw.Restart();
      var fast = BitNester.Nest(MakeTypes(), sheetW, sheetH, px);
      sw.Stop();
      long fastMs = sw.ElapsedMilliseconds;

      bool same = slow.Placements.Count == fast.Placements.Count && slow.Sheets == fast.Sheets;
      for (int i = 0; i < System.Math.Min(slow.Placements.Count, fast.Placements.Count) && same; i++)
      {
        var a = slow.Placements[i];
        var b = fast.Placements[i];
        if (a.Source != b.Source || a.Sheet != b.Sheet || a.Xpx != b.Xpx || a.Ypx != b.Ypx || a.RotationDeg != b.RotationDeg)
        {
          same = false;
        }
      }

      Console.WriteLine($"800 parts → {slow.Sheets} sheets");
      Console.WriteLine($"current (byte):        {slowMs,6} ms");
      Console.WriteLine($"optimized (bits+skip): {fastMs,6} ms     speedup {(double)slowMs / System.Math.Max(fastMs, 1),5:F1}×");
      Console.WriteLine($"identical result: {(same ? "PASS ✓ (same perfect nest, just faster)" : "FAIL ✗")}");
    }

    private static void VerifyConversion()
    {
      Console.WriteLine("\n=== Phase 4b prep: verify raster→PartPlacement coordinate mapping ===");
      var holed = LoadHoledPart();
      const double px = 8.0;
      double xIn = 80 / px; // pixel 80 -> 10 in
      double yIn = 160 / px; // pixel 160 -> 20 in

      foreach (int rot in new[] { 0, 90 })
      {
        var rotated = rot == 0 ? holed : holed.Rotate(rot);
        var pp = new PartPlacement(rotated)
        {
          X = xIn - rotated.MinX,
          Y = yIn - rotated.MinY,
          Rotation = rot,
          Source = 0,
        };
        var placed = pp.PlacedPart;
        bool ok = Math.Abs(placed.MinX - xIn) < 1e-6 && Math.Abs(placed.MinY - yIn) < 1e-6;
        Console.WriteLine($"rot {rot,3}°: PlacedPart min=({placed.MinX:F2},{placed.MinY:F2}) size={placed.MaxX - placed.MinX:F1}×{placed.MaxY - placed.MinY:F1}  expect min=({xIn:F2},{yIn:F2})  {(ok ? "PASS ✓" : "FAIL ✗")}  holes={placed.Children?.Count ?? 0}");
      }
    }

    private const string ScratchDir = @"C:\Users\rosam\AppData\Local\Temp\claude\C--Users-rosam\94fe7ba7-a467-4d74-99f9-d3d1b446f8b4\scratchpad";

    private static INfp LoadPart(string dxf, int source)
    {
      var raw = DxfParser.LoadDxfFile(dxf).GetAwaiter().GetResult();
      return raw != null && raw.TryConvertToNfp(source, out INfp part) ? part : null;
    }

    private static INfp LoadHoledPart() => LoadPart(ScratchDir + @"\holed_test\holed.dxf", 0);

    private static void RasterNestPhase4a()
    {
      Console.WriteLine("\n=== Phase 4a: generalized nester (rotations + multi-part + multi-sheet) ===");
      var holed = LoadPart(ScratchDir + @"\holed_test\holed.dxf", 0);
      var strip = LoadPart(ScratchDir + @"\strip_test\strip.dxf", 1);
      if (holed == null || strip == null)
      {
        Console.WriteLine("Failed to load parts.");
        return;
      }

      const double px = 8.0;
      var types = new System.Collections.Generic.List<PartType>
      {
        new PartType { Source = 0, Poly = holed, Quantity = 40, RotationsDeg = new[] { 0, 90 } },
        new PartType { Source = 1, Poly = strip, Quantity = 8, RotationsDeg = new[] { 0, 90 } },
      };

      int sheetW = (int)(48 * px);
      int sheetH = (int)(96 * px);

      var sw = System.Diagnostics.Stopwatch.StartNew();
      var r = RasterJobNester.Nest(types, sheetW, sheetH, px);
      sw.Stop();

      Console.WriteLine($"Job: 40× holed (10×6\") + 8× strip (5×50\") = 48 parts · sheet 48×96\" @ {px}px/in");
      Console.WriteLine($"Placed {r.Placements.Count}/48 (notPlaced {r.NotPlaced}) across {r.Sheets} sheet(s) in {sw.ElapsedMilliseconds} ms");
      Console.WriteLine($"Utilization: {r.Utilization:P1}");
      Console.WriteLine($"Rotations used: {string.Join(", ", r.RotationUse.OrderBy(k => k.Key).Select(k => $"{k.Key}°×{k.Value}"))}");
      Console.WriteLine($"No-overlap: {(r.NoOverlap ? "PASS ✓" : "FAIL ✗")}");
      Console.WriteLine(r.NoOverlap
        ? "\nPhase 4a OK — generalized nester handles a real multi-part job. Next: wire into the app (Phase 4b)."
        : "\nPhase 4a FAILED — overlap detected.");
    }

    private static void RasterNestPhase3()
    {
      Console.WriteLine("\n=== Phase 3: GPU-accelerated placement search ===");
      var part = LoadHoledPart();
      if (part == null)
      {
        Console.WriteLine("Failed to load part.");
        return;
      }

      const double pxPerInch = 8.0;
      const int copies = 24;
      var mask = RasterUtil.Rasterize(part, pxPerInch);
      int sheetW = (int)(48 * pxPerInch);
      int sheetH = (int)(96 * pxPerInch);
      long posPerPart = (long)(sheetW - mask.W + 1) * (sheetH - mask.H + 1);
      Console.WriteLine($"Resolution {pxPerInch}px/in · mask {mask.W}×{mask.H} (solid {mask.SolidCount}px) · {copies} parts · {posPerPart:N0} positions/part");
      Console.WriteLine($"Work ≈ {posPerPart * mask.SolidCount * copies / 1_000_000_000.0:F1} billion overlap tests\n");

      using var context = Context.Create(b => b.Default());
      var cudaDev = context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda)
                    ?? context.GetPreferredDevice(preferCPU: false);
      using var accel = cudaDev.CreateAccelerator(context);
      using var gpu = new RasterNesterGpu(accel);

      // Warm up the GPU kernel (ILGPU JIT-compiles on first use) so the timing is fair.
      gpu.Nest(mask, 1, sheetW, sheetH, useGpu: true, out _);

      var cpu = gpu.Nest(mask, copies, sheetW, sheetH, useGpu: false, out double cpuMs);
      var g = gpu.Nest(mask, copies, sheetW, sheetH, useGpu: true, out double gpuMs);

      bool samePlacement = cpu.Placed.Count == g.Placed.Count;
      for (int i = 0; i < System.Math.Min(cpu.Placed.Count, g.Placed.Count); i++)
      {
        if (cpu.Placed[i].X != g.Placed[i].X || cpu.Placed[i].Y != g.Placed[i].Y)
        {
          samePlacement = false;
          break;
        }
      }

      int occCount = 0;
      foreach (var bb in g.Occupancy)
      {
        if (bb)
        {
          occCount++;
        }
      }

      bool noOverlap = occCount == g.Placed.Count * mask.SolidCount;

      Console.WriteLine($"Placed {g.Placed.Count}/{copies}");
      Console.WriteLine($"Collision-map compute:   CPU (multi-core) {cpuMs,7:F0} ms     GPU (RTX 3060) {gpuMs,7:F1} ms     speedup {cpuMs / System.Math.Max(gpuMs, 0.01),5:F1}×");
      Console.WriteLine($"Correctness:   same placements CPU==GPU: {(samePlacement ? "PASS ✓" : "FAIL ✗")}     no-overlap: {(noOverlap ? "PASS ✓" : "FAIL ✗")}");
    }

    private static void PrintGpu()
    {
      using var context = Context.Create(builder => builder.Default());
      var cuda = context.Devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda);
      Console.WriteLine(cuda != null
        ? $"GPU available: {cuda.Name} (CUDA) — will accelerate the placement search in Phase 3."
        : "No CUDA GPU found; Phase 3 would fall back to CPU/OpenCL.");
    }

    private static void RasterNestPhase2()
    {
      Console.WriteLine("=== Phase 2: CPU raster nester ===");

      string dxf = @"C:\Users\rosam\AppData\Local\Temp\claude\C--Users-rosam\94fe7ba7-a467-4d74-99f9-d3d1b446f8b4\scratchpad\holed_test\holed.dxf";
      var raw = DxfParser.LoadDxfFile(dxf).GetAwaiter().GetResult();
      if (raw == null || !raw.TryConvertToNfp(0, out INfp part))
      {
        Console.WriteLine($"Failed to load part from {dxf}");
        return;
      }

      const double pxPerInch = 6.0;        // 6 px/inch ≈ 0.167" pixels
      const int copies = 30;
      const int sheetInW = 48;
      const int sheetInH = 96;

      var mask = RasterUtil.Rasterize(part, pxPerInch);
      double maskInArea = mask.SolidCount / (pxPerInch * pxPerInch);
      Console.WriteLine($"Part rasterized: {mask.W}×{mask.H}px, solid {mask.SolidCount}px ≈ {maskInArea:F1} in² (net, holes excluded)");

      var parts = Enumerable.Repeat(mask, copies).ToArray();
      int sheetW = (int)(sheetInW * pxPerInch);
      int sheetH = (int)(sheetInH * pxPerInch);

      var sw = Stopwatch.StartNew();
      var result = RasterNester.Nest(parts, sheetW, sheetH);
      sw.Stop();

      int occCount = 0;
      for (int i = 0; i < result.Occupancy.Length; i++)
      {
        if (result.Occupancy[i])
        {
          occCount++;
        }
      }

      double usedArea = (double)sheetW * result.UsedHeight;
      double util = usedArea > 0 ? occCount / usedArea : 0;
      bool noOverlap = occCount == result.Placed.Count * mask.SolidCount;

      Console.WriteLine($"Placed {result.Placed.Count}/{copies} (notPlaced {result.NotPlaced}) in {sw.Elapsed.TotalMilliseconds:F0} ms on CPU");
      Console.WriteLine($"Used height: {result.UsedHeight / pxPerInch:F1} in  ·  area utilization within used region: {util:P1}");
      Console.WriteLine($"No-overlap check: {(noOverlap ? "PASS ✓" : $"FAIL ✗ (occ {occCount} vs expected {result.Placed.Count * mask.SolidCount})")}");
      Console.WriteLine(noOverlap
        ? "\nPhase 2 OK — raster nesting places parts correctly. Next: GPU-accelerate the position search (Phase 3)."
        : "\nPhase 2 FAILED — overlap detected.");
    }
  }
}
