namespace DeepNestLib.Placement
{
  /// <summary>Canonical physical defaults for common-line cutting.</summary>
  public static class CommonLineDefaults
  {
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
  }
}
