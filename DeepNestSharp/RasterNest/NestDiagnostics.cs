namespace DeepNestSharp.RasterNest
{
  using System.Collections.Concurrent;
  using System.Diagnostics;

  /// <summary>
  /// Where a nest spends its time, and how often it repeats its heavier steps, for the stages the per-seed
  /// timing line does not split out. Milliseconds and counts are summed across the seeds that race in
  /// parallel, so they are work done, not wall time. The benches read and clear them; the result of the
  /// nest never depends on them.
  /// </summary>
  internal static class NestDiagnostics
  {
    internal static readonly ConcurrentDictionary<string, long> Totals = new();

    internal static void Add(string name, long amount = 1)
      => Totals.AddOrUpdate(name, amount, (_, total) => total + amount);

    internal static void AddElapsed(string name, Stopwatch clock) => Add(name + ".ms", clock.ElapsedMilliseconds);
  }
}
