namespace DeepNestSharp.RasterNest
{
  /// <summary>
  /// How hard the engine searches each sheet. What it buys is EXTENT, not parts placed.
  /// </summary>
  /// <remarks>
  /// <para>The distinction is the whole reason this exists. The number of parts that fit on a sheet
  /// saturates after a handful of iterations and never moves again; how tightly they are packed keeps
  /// improving long after that. The budget that shipped was calibrated against the part count, so it was
  /// read as "converged" while the pack was still a sixth too wide, and the leftover material came back to
  /// the operator scattered instead of as one usable strip.</para>
  /// <para>Measured on the fourteen part reference job (2650 x 2070 sheet, 3.32 million mm2 of parts), best
  /// of eight races exactly as the app runs it, reporting the width the winner packs them into:</para>
  /// <code>
  ///   iterations     width      race
  ///           15   3158 mm     0.2 s   &lt;- what shipped
  ///          300   2527 mm     1.4 s
  ///          600   2212 mm    10.5 s
  ///          800   2147 mm    53 s
  /// </code>
  /// <para>Another nester leaves 2247.7 mm on that job, so the middle setting clears the bar. Early
  /// termination (-x) was measured on and off across the whole range and changed nothing, to the decimal, so
  /// it was never the limiter; the budget always was.</para>
  /// </remarks>
  internal enum NestEffort
  {
    /// <summary>For when an answer straight away is worth more than the last of the tightness. Still packs
    /// far tighter than what shipped before this setting existed.</summary>
    Fast = 0,

    /// <summary>The default: the knee of the curve above, as tight as the pack meaningfully gets. On a
    /// common-line job that tightness is what decides whether the last part of a row shares its cut.</summary>
    Normal = 1,

    /// <summary>For expensive material, where waiting is cheaper than the offcut. Buys the tail of the
    /// curve, which costs many times what the rest of it did.</summary>
    Best = 2,
  }

  /// <summary>One source of truth for a new install and for a session with no saved choice.</summary>
  internal static class NestEffortDefaults
  {
    internal const NestEffort Default = NestEffort.Normal;

    /// <summary>What a session holds when the operator has never picked an effort.</summary>
    internal const int Unset = -1;

    internal static NestEffort FromPersisted(int value)
      => value >= (int)NestEffort.Fast && value <= (int)NestEffort.Best
        ? (NestEffort)value
        : Default;

    /// <summary>
    /// Whether a stored effort has to be forgotten once, so the operator lands on the current default.
    /// <para>
    /// Builds before this one wrote whatever was in memory over the "never chosen" marker every time the
    /// app closed, and what was in memory on an untouched install was the old default, Fast. So a stored
    /// Fast from one of those builds does not say whether anyone chose it. It is reinterpreted ONCE, and
    /// only Fast: Normal and Best could only ever have got there by someone picking them, because the
    /// in-memory value was never either of those on its own.
    /// </para>
    /// <para>The version stamp is what makes it happen once. Without it, picking Fast on purpose would be
    /// undone again on the next start.</para>
    /// </summary>
    internal static bool NeedsEffortReset(int persisted, int settingsVersion)
      => settingsVersion < SessionState.CurrentSettingsVersion && persisted == (int)NestEffort.Fast;
  }
}
