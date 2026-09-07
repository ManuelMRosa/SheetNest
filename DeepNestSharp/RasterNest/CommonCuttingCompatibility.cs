namespace DeepNestSharp.RasterNest
{
  using System;
  using DeepNestLib.NestProject;

  /// <summary>
  /// The one common-cutting compatibility rule used by loading, compaction, exact direct planning and
  /// final placement acceptance. A non-empty matching domain is the outer gate; only then do the
  /// per-part modes and SamePart identity decide whether a pair may share.
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
  }
}
