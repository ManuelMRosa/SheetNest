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
    /// <summary>For a job being roughed out, where the wait matters more than the last few percent.</summary>
    Fast = 0,

    /// <summary>The default: the knee of the curve above, where the pack stops getting meaningfully
    /// tighter and the clock has not yet run away.</summary>
    Normal = 1,

    /// <summary>For expensive material, where waiting is cheaper than the offcut. Buys the tail of the
    /// curve, which costs many times what the rest of it did.</summary>
    Best = 2,
  }
}
