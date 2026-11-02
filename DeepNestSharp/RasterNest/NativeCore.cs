namespace DeepNestSharp.RasterNest
{
  using System;

  /// <summary>
  /// What every call into sheetnest_core.dll (native/) shares: the library's name, SHEETNEST_NATIVE=off to run
  /// the C# code instead, and the note that the DLL could not be loaded, after which only the C# code runs.
  /// </summary>
  internal static class NativeCore
  {
    internal const string Library = "sheetnest_core";

    /// <summary>SHEETNEST_NATIVE=off: the C# code runs, to compare the two.</summary>
    internal static readonly bool SwitchedOff = string.Equals(
      Environment.GetEnvironmentVariable("SHEETNEST_NATIVE"),
      "off",
      StringComparison.OrdinalIgnoreCase);

    private static volatile bool missing;

    /// <summary>The DLL failed to load once; it is not tried again.</summary>
    internal static bool Missing => missing;

    /// <summary>Whether a native call can still be made (the DLL may yet fail to load on first use).</summary>
    internal static bool Enabled => !SwitchedOff && !missing;

    /// <summary>True, and the DLL marked missing, when <paramref name="ex"/> says it could not be loaded.</summary>
    internal static bool IsLoadFailure(Exception ex)
    {
      if (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
      {
        missing = true;
        return true;
      }

      return false;
    }
  }
}
