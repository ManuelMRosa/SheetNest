namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Collections.Generic;
  using System.Runtime.InteropServices;
  using System.Threading;

  /// <summary>
  /// The C++ translation of <see cref="RasterCompact.CompactManaged"/> in sheetnest_core.dll (native/), which
  /// gives the same positions bit for bit. When the DLL is missing or cannot load, or SHEETNEST_NATIVE=off,
  /// nothing here runs and the C# code compacts as it always has.
  /// </summary>
  internal static class NativeCompact
  {
    private const int Ok = 0;
    private const int CancelledResult = 1;

    /// <summary>
    /// Compacts <paramref name="items"/> in place as <see cref="RasterCompact.CompactManaged"/> would. False,
    /// with the items untouched, when the native code is switched off, not available, or refuses the input
    /// (where the C# code would throw); the caller then runs the C# code. A cancel throws, as the C# code does.
    /// </summary>
    internal static bool TryCompact(
      IList<CompactItem> items,
      double sheetW,
      double sheetH,
      double margin,
      int[] groups,
      CancellationToken cancel,
      double commonLineGap,
      CancellationToken stop = default)
      => !NativeCore.SwitchedOff && TryCompactNative(items, sheetW, sheetH, margin, groups, cancel, commonLineGap, stop);

    /// <summary><see cref="TryCompact"/> whatever SHEETNEST_NATIVE says, for the parity tests.</summary>
    /// <param name="stop">The nest's clock: the pass ends early and keeps the positions reached (see
    /// <see cref="RasterCompact.Compact"/>).</param>
    internal static bool TryCompactNative(
      IList<CompactItem> items,
      double sheetW,
      double sheetH,
      double margin,
      int[] groups,
      CancellationToken cancel,
      double commonLineGap,
      CancellationToken stop = default)
    {
      if (NativeCore.Missing)
      {
        return false;
      }

      int n = items.Count;
      var contourCounts = new int[n];
      var pointCounts = new List<int>();
      var points = new List<double>();
      var xs = new double[n];
      var ys = new double[n];
      var spacings = new double[n];
      var hasPeers = new byte[n];
      bool anyPeers = false;
      for (int i = 0; i < n; i++)
      {
        CompactItem it = items[i];
        contourCounts[i] = 1;
        AddContour(it.Poly.Points, pointCounts, points);
        if (it.Poly.Children != null)
        {
          foreach (var hole in it.Poly.Children)
          {
            contourCounts[i]++;
            AddContour(hole.Points, pointCounts, points);
          }
        }

        xs[i] = it.X;
        ys[i] = it.Y;
        spacings[i] = it.Spacing;
        bool peers = it.CommonEdgePeers != null && it.CommonEdgePeers.Count > 0;
        hasPeers[i] = peers ? (byte)1 : (byte)0;
        anyPeers |= peers;
      }

      // Which pairs share a planned common edge, decided here by the same rule the C# code asks, so no
      // strings or sets cross into the native code. Without peers no pair can.
      byte[] shares = null;
      if (anyPeers)
      {
        shares = new byte[n * n];
        for (int i = 0; i < n; i++)
        {
          for (int j = 0; j < n; j++)
          {
            shares[(i * n) + j] = RasterCompact.SharesCommonEdge(items[i], items[j]) ? (byte)1 : (byte)0;
          }
        }
      }

      var flag = new int[2]; // [0] cancel, [1] stop, polled by the native code
      var pinned = GCHandle.Alloc(flag, GCHandleType.Pinned);
      try
      {
        int result;
        using (cancel.Register(() => Volatile.Write(ref flag[0], 1)))
        using (stop.Register(() => Volatile.Write(ref flag[1], 1)))
        {
          try
          {
            result = SncCompact(
              n,
              contourCounts,
              pointCounts.ToArray(),
              points.ToArray(),
              xs,
              ys,
              spacings,
              hasPeers,
              shares,
              groups,
              groups?.Length ?? 0,
              sheetW,
              sheetH,
              margin,
              commonLineGap,
              pinned.AddrOfPinnedObject(),
              pinned.AddrOfPinnedObject() + sizeof(int));
          }
          catch (Exception ex) when (NativeCore.IsLoadFailure(ex))
          {
            return false;
          }
        }

        if (result == CancelledResult)
        {
          cancel.ThrowIfCancellationRequested();
          throw new OperationCanceledException(cancel);
        }

        if (result != Ok)
        {
          return false;
        }

        for (int i = 0; i < n; i++)
        {
          items[i].X = xs[i];
          items[i].Y = ys[i];
        }

        return true;
      }
      finally
      {
        pinned.Free();
      }
    }

    private static void AddContour(DeepNestLib.SvgPoint[] contour, List<int> pointCounts, List<double> points)
    {
      pointCounts.Add(contour.Length);
      foreach (var p in contour)
      {
        points.Add(p.X);
        points.Add(p.Y);
      }
    }

    [DllImport(NativeCore.Library, EntryPoint = "snc_compact", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int SncCompact(
      int n,
      int[] contourCounts,
      int[] pointCounts,
      double[] points,
      [In, Out] double[] xs,
      [In, Out] double[] ys,
      double[] spacings,
      byte[] hasPeers,
      byte[] shares,
      int[] groups,
      int groupsLength,
      double sheetW,
      double sheetH,
      double margin,
      double commonLineGap,
      IntPtr cancel,
      IntPtr stop);
  }
}
