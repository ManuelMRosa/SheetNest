namespace DeepNestSharp.RasterNest
{
  using System;
  using DeepNestLib.NestProject;

  /// <summary>
  /// The one common-cutting compatibility rule used by loading, compaction, exact direct planning, SheetCam
  /// measured-kerf snapping and final placement acceptance. A non-empty matching domain is the outer
  /// gate; only then do the per-part modes and SamePart identity decide whether a pair may share.
  /// </summary>
  internal static class CommonCuttingCompatibility
  {
    internal static bool MayShare(
      string firstDomain,
      CommonCuttingMode firstMode,
      int firstShareKey,
      string secondDomain,
      CommonCuttingMode secondMode,
      int secondShareKey)
    {
      if (string.IsNullOrWhiteSpace(firstDomain)
        || string.IsNullOrWhiteSpace(secondDomain)
        || !string.Equals(firstDomain, secondDomain, StringComparison.OrdinalIgnoreCase))
      {
        return false;
      }

      if (firstMode == CommonCuttingMode.None || secondMode == CommonCuttingMode.None)
      {
        return false;
      }

      if (firstMode == CommonCuttingMode.SamePart || secondMode == CommonCuttingMode.SamePart)
      {
        return firstShareKey == secondShareKey;
      }

      return true;
    }

    /// <summary>The deliberately separate SheetCam contract: compatible parts from one .nest job keep
    /// their already-offset toolpaths one measured kerf apart. Direct nominal contours never enter it.</summary>
    internal static bool TryLegacySheetCamClearance(
      string firstDomain,
      CommonCuttingMode firstMode,
      int firstShareKey,
      double firstKerf,
      string secondDomain,
      CommonCuttingMode secondMode,
      int secondShareKey,
      double secondKerf,
      out double clearance)
    {
      clearance = 0;
      if (string.IsNullOrWhiteSpace(firstDomain)
        || string.IsNullOrWhiteSpace(secondDomain)
        || !firstDomain.StartsWith("sheetcam:", StringComparison.OrdinalIgnoreCase)
        || !secondDomain.StartsWith("sheetcam:", StringComparison.OrdinalIgnoreCase)
        || !MayShare(firstDomain, firstMode, firstShareKey, secondDomain, secondMode, secondShareKey))
      {
        return false;
      }

      clearance = Math.Max(Math.Max(0, firstKerf), Math.Max(0, secondKerf));
      return clearance > 0;
    }
  }
}
