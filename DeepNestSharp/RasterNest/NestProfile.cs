namespace DeepNestSharp.RasterNest
{
  using System;

  /// <summary>
  /// Opt-in nest-engine phase profiler: set SHEETNEST_NEST_PROFILE=1 and timings append to
  /// %TEMP%\sheetnest_profile.log. Zero cost when off (single static bool check).
  /// </summary>
  internal static class NestProfile
  {
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("SHEETNEST_NEST_PROFILE") == "1";

    private static readonly object Sync = new object();

    public static void Log(string msg)
    {
      if (!Enabled)
      {
        return;
      }

      lock (Sync)
      {
        System.IO.File.AppendAllText(
          System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sheetnest_profile.log"),
          $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
      }
    }
  }
}
