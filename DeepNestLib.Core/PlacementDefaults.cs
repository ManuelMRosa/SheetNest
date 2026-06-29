namespace DeepNestLib
{
  using System.Runtime.CompilerServices;
  using DeepNestLib.GeneticAlgorithm;
  using DeepNestLib.Placement;

  /// <summary>
  /// Production defaults applied when the assembly loads: the proven nesting improvements are
  /// turned on so any host (the standalone app, or SheetCam if this build replaces its
  /// DeepNestLib.dll) gets them without code changes. Hosts that drive the toggles explicitly
  /// (e.g. the NestBench A/B harness) override these per run, so their behaviour is unaffected.
  /// </summary>
  internal static class PlacementDefaults
  {
    [ModuleInitializer]
    internal static void Apply()
    {
      GridNesting.Enabled = true;          // identical rectangular blanks -> tight common-line grid (~99%)
      Procreant.SmartRotationSeed = true;  // irregular parts -> seed at minimum bounding-box orientation (+~3%)
    }
  }
}
