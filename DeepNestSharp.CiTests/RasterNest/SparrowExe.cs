namespace DeepNestSharp.CiTests.RasterNest
{
  using System;
  using System.IO;

  /// <summary>
  /// Finds the nesting engine for the end-to-end tests.
  /// </summary>
  /// <remarks>
  /// Those tests return early and pass when SPARROW_EXE is not set, which is right on a machine that
  /// has no engine and wrong everywhere it matters: a test that did not run is not a green test, it is
  /// no test, and it looks identical in the output. Set SHEETNEST_REQUIRE_SPARROW=1 (CI does) and a
  /// missing engine fails loudly instead of quietly reporting success.
  /// </remarks>
  internal static class SparrowExe
  {
    internal static string Resolve()
    {
      string exe = Environment.GetEnvironmentVariable("SPARROW_EXE");
      bool usable = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);

      if (!usable && Environment.GetEnvironmentVariable("SHEETNEST_REQUIRE_SPARROW") == "1")
      {
        throw new InvalidOperationException(
          "SHEETNEST_REQUIRE_SPARROW=1 but SPARROW_EXE does not point at the nesting engine, so every "
          + "end-to-end test here would have skipped and reported green. Point it at SparrowEngine\\sparrow.exe.");
      }

      return usable ? exe : null;
    }
  }
}
