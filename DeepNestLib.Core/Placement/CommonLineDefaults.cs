namespace DeepNestLib.Placement
{
  /// <summary>Canonical physical defaults for common-line cutting.</summary>
  public static class CommonLineDefaults
  {
    /// <summary>
    /// Automatic common-line cluster sizing. A positive value is a hard part-count limit instead.
    /// </summary>
    public const int AutoMaxClusterParts = 0;

    public const int DefaultMaxClusterParts = AutoMaxClusterParts;

    public const int MinimumMaxClusterParts = 2;

    public const int MaximumMaxClusterParts = 500;

    /// <summary>The one canonical default gap, stored in inches.</summary>
    public const double DefaultGapInches = 0.010D;

    /// <summary>
    /// The independent geometry comparison tolerance, stored in inches. It is deliberately an order
    /// of magnitude smaller than the default common-line gap and must never be substituted for it.
    /// </summary>
    public const double DefaultGeometryToleranceInches = 0.0001D;

    public const double InchesToMillimeters = 25.4D;

    public static double DefaultGapMillimeters => DefaultGapInches * InchesToMillimeters;

    public static double DefaultGeometryToleranceMillimeters
      => DefaultGeometryToleranceInches * InchesToMillimeters;

    public static double ToActiveUnits(double inches, bool unitsAreMillimeters)
      => unitsAreMillimeters ? inches * InchesToMillimeters : inches;

    /// <summary>
    /// Keeps the persisted contract deliberately narrow: zero means Auto; manual limits are 2..500.
    /// Missing, negative, one, and out-of-range values safely fall back to Auto.
    /// </summary>
    public static int NormalizeMaxClusterParts(int value)
      => value == AutoMaxClusterParts
        || (value >= MinimumMaxClusterParts && value <= MaximumMaxClusterParts)
          ? value
          : AutoMaxClusterParts;
  }
}
