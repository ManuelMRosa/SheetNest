namespace DeepNestSharp.RasterNest
{
  /// <summary>
  /// Permitted-orientation codes → explicit angle sets. Legacy count codes keep their historical
  /// meaning (1 = as drawn, 2 = 0/180, 4 = four square orientations, 8 = 45° steps, bigger = any); the
  /// 100x codes are the orientation choices a plain count cannot express. "Any" maps to 15° steps.
  /// Shared by the rotation picker and the nesting engine (kept out of any single engine so it survives
  /// engine changes).
  /// </summary>
  internal static class RotationCodes
  {
    internal const int RotOnly90 = 1001;      // only 90° — always turned once
    internal const int RotZeroAnd90 = 1002;   // 0° and 90°
    internal const int Rot90And270 = 1003;    // 90° and 270°

    internal static int[] PermittedSet(int code)
    {
      switch (code)
      {
        case RotOnly90: return new[] { 90 };
        case RotZeroAnd90: return new[] { 0, 90 };
        case Rot90And270: return new[] { 90, 270 };
      }

      if (code <= 1)
      {
        return new[] { 0 };
      }

      if (code == 2)
      {
        return new[] { 0, 180 };
      }

      if (code <= 7)
      {
        return new[] { 0, 90, 180, 270 };
      }

      return code == 8 ? AnglesN(8) : AnglesN(24);
    }

    /// <summary>Evenly spaced rotation angles: AnglesN(8) = {0, 45, 90, ...}.</summary>
    internal static int[] AnglesN(int n)
    {
      var a = new int[n];
      for (int i = 0; i < n; i++)
      {
        a[i] = i * 360 / n;
      }

      return a;
    }
  }
}
