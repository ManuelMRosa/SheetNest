namespace DeepNestSharp.CiTests.RasterNest
{
  using Xunit;

  /// <summary>
  /// Serialises every test that drives the nesting engine.
  /// <para>
  /// <see cref="DeepNestSharp.RasterNest.SparrowNestService"/> reports what a nest did through statics
  /// (DiagSeamsSnapped, DiagSeamsGivenUp, DiagDeferred and the timings), and Nest RESETS them on entry.
  /// xUnit runs different test classes in parallel, so two engine tests overlapping means one clears and
  /// adds to the other's totals. It is not theoretical: the weld test read 48 shared cuts on a 26-part
  /// sheet whose ceiling is 25, purely because another engine test was nesting at the same time, and
  /// alone it reads 25 every time.
  /// </para>
  /// <para>
  /// A shared collection is the cheap fix and it pays for itself twice: these tests each spawn their own
  /// pack of sparrow processes, so running them at the same time only oversubscribes the machine.
  /// </para>
  /// </summary>
  [CollectionDefinition(Name)]
  public class SparrowEngineCollection
  {
    public const string Name = "SparrowEngine";
  }
}
