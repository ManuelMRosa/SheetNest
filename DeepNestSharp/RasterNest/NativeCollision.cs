namespace DeepNestSharp.RasterNest
{
  using System;
  using System.Buffers;
  using System.Runtime.InteropServices;
  using DeepNestLib;

  /// <summary>
  /// The C++ translation of <see cref="PlacementCollision.TooCloseManaged"/> in sheetnest_core.dll (native/),
  /// which gives the same verdict on every pair. When the DLL is missing or cannot load, or SHEETNEST_NATIVE=off,
  /// nothing here runs and the C# code decides as it always has.
  /// </summary>
  internal static class NativeCollision
  {
    private const int ErrorResult = -1;

    /// <summary>
    /// The verdict of <see cref="PlacementCollision.TooCloseManaged"/> in <paramref name="tooClose"/>. False when the
    /// native code is switched off, not available, or refuses the input (where the C# code would throw); the
    /// caller then asks the C# code.
    /// </summary>
    internal static bool TryTooClose(INfp placedA, INfp placedB, double clearance, double sliver, out bool tooClose)
    {
      tooClose = false;
      return !NativeCore.SwitchedOff && TryTooCloseNative(placedA, placedB, clearance, sliver, out tooClose);
    }

    /// <summary><see cref="TryTooClose"/> whatever SHEETNEST_NATIVE says, for the parity tests.</summary>
    internal static bool TryTooCloseNative(INfp placedA, INfp placedB, double clearance, double sliver, out bool tooClose)
    {
      tooClose = false;
      if (NativeCore.Missing || placedA?.Points == null || placedB?.Points == null)
      {
        return false; // a null part throws in the C# code, so it is the C# code that answers
      }

      int aContours = ContourCount(placedA);
      int bContours = ContourCount(placedB);
      int aPoints = PointCount(placedA);
      int bPoints = PointCount(placedB);
      var counts = ArrayPool<int>.Shared.Rent(aContours + bContours);
      var points = ArrayPool<double>.Shared.Rent(2 * (aPoints + bPoints));
      try
      {
        Flatten(placedA, counts, 0, points, 0);
        Flatten(placedB, counts, aContours, points, 2 * aPoints);
        int result;
        try
        {
          result = SncTooClose(counts, aContours, points, 0, bContours, clearance, sliver, aContours, 2 * aPoints);
        }
        catch (Exception ex) when (NativeCore.IsLoadFailure(ex))
        {
          return false;
        }

        if (result == ErrorResult)
        {
          return false;
        }

        tooClose = result == 1;
        return true;
      }
      finally
      {
        ArrayPool<int>.Shared.Return(counts);
        ArrayPool<double>.Shared.Return(points);
      }
    }

    /// <summary>The outline and each first-level hole, the contours the C# code reads.</summary>
    private static int ContourCount(INfp nfp) => 1 + (nfp.Children?.Count ?? 0);

    private static int PointCount(INfp nfp)
    {
      int total = nfp.Points.Length;
      if (nfp.Children != null)
      {
        foreach (var child in nfp.Children)
        {
          total += child?.Points?.Length ?? 0;
        }
      }

      return total;
    }

    private static void Flatten(INfp nfp, int[] counts, int countAt, double[] points, int pointAt)
    {
      counts[countAt++] = nfp.Points.Length;
      foreach (var p in nfp.Points)
      {
        points[pointAt++] = p.X;
        points[pointAt++] = p.Y;
      }

      if (nfp.Children != null)
      {
        foreach (var child in nfp.Children)
        {
          // A missing hole is skipped by the C# code, as one of fewer than three points is: send it empty.
          var childPoints = child?.Points ?? Array.Empty<SvgPoint>();
          counts[countAt++] = childPoints.Length;
          foreach (var p in childPoints)
          {
            points[pointAt++] = p.X;
            points[pointAt++] = p.Y;
          }
        }
      }
    }

    /// <summary>Both parts' arrays sit in one rented buffer each; the offsets say where B starts.</summary>
    private static int SncTooClose(int[] counts, int aContours, double[] points, int aPointsAt, int bContours, double clearance, double sliver, int bCountsAt, int bPointsAt)
    {
      var countsHandle = GCHandle.Alloc(counts, GCHandleType.Pinned);
      var pointsHandle = GCHandle.Alloc(points, GCHandleType.Pinned);
      try
      {
        IntPtr countsBase = countsHandle.AddrOfPinnedObject();
        IntPtr pointsBase = pointsHandle.AddrOfPinnedObject();
        return SncTooCloseRaw(
          aContours,
          countsBase,
          pointsBase + (aPointsAt * sizeof(double)),
          bContours,
          countsBase + (bCountsAt * sizeof(int)),
          pointsBase + (bPointsAt * sizeof(double)),
          clearance,
          sliver);
      }
      finally
      {
        countsHandle.Free();
        pointsHandle.Free();
      }
    }

    [DllImport(NativeCore.Library, EntryPoint = "snc_too_close", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int SncTooCloseRaw(
      int aContours,
      IntPtr aPointCounts,
      IntPtr aPoints,
      int bContours,
      IntPtr bPointCounts,
      IntPtr bPoints,
      double clearance,
      double sliver);
  }
}
