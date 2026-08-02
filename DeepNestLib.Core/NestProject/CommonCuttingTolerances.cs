namespace DeepNestLib.NestProject
{
  using System;

  /// <summary>
  /// The numbers that decide whether two edges are parallel enough, close enough and long enough to be
  /// cut as one. Most are expressed in KERFS rather than absolute units, because that is what they
  /// actually scale with.
  /// </summary>
  /// <remarks>
  /// <para>
  /// ⚠️ CALIBRATE THESE. Every default here is a starting value typical of the trade, not a measurement.
  /// The only way to set them properly is against the kerf MEASURED on the machine: cut a coupon, mic
  /// the slot, and work from that number. A tolerance that is too loose welds parts that should not
  /// touch; too tight and common cutting quietly does nothing.
  /// </para>
  /// <para>
  /// ⚠️ These are NOT Radan's values. Radan does not publish its internal tolerances and none of this
  /// was derived from it. Do not describe them, in code or in docs, as if they were.
  /// </para>
  /// </remarks>
  public sealed class CommonCuttingTolerances
  {
    /// <summary>
    /// The angular tolerance the shipped default reproduces, in degrees.
    /// </summary>
    /// <remarks>
    /// This is the exact equivalent of the test that used to be hard-coded, and the conversion is the
    /// easy thing to get wrong. The old code read <c>Math.Abs(dx) &lt; 1e-3 * len</c>, which is
    /// <c>sin θ &lt; 1e-3</c>, so θ is <b>0.0572958°</b> and NOT 0.001°. Reading it as 0.001° makes the
    /// tolerance fifty-seven times tighter and common cutting stops finding anything.
    /// </remarks>
    public const double LegacyAngleToleranceDeg = 0.057295779513082325; // asin(1e-3) in degrees

    /// <summary>How far from parallel two edges may be and still be cut as one (degrees).</summary>
    public double AngleToleranceDeg { get; set; } = LegacyAngleToleranceDeg;

    /// <summary>Shortest edge worth sharing, in kerfs. Below this it is a chamfer, not a seam.</summary>
    public double MinEdgeLengthKerfs { get; set; } = 2.0;

    /// <summary>
    /// Shortest edge worth sharing in absolute drawing units, whatever the kerf says.
    /// </summary>
    /// <remarks>
    /// A floor is needed because the parser works in single-precision floats: the DIRECTION of a very
    /// short segment is mostly noise, so a fine kerf would otherwise let a 0.005 long edge through
    /// carrying a fraction of a degree of junk. Zero here keeps the behaviour that shipped before this
    /// setting existed.
    /// </remarks>
    public double MinEdgeLengthAbsolute { get; set; } = 0.0;

    /// <summary>Closest two edges may sit and still be recognised as a seam, in kerfs.</summary>
    public double GapMinKerfs { get; set; } = 0.4;

    /// <summary>Furthest two edges may sit and still be pulled onto a seam, in kerfs.</summary>
    public double GapMaxKerfs { get; set; } = 1.6;

    /// <summary>
    /// How far two edges must run alongside each other, in kerfs. This is what rejects a corner touch:
    /// two edges meeting at a vertex overlap by nothing.
    /// </summary>
    public double MinOverlapKerfs { get; set; } = 2.0;

    /// <summary>
    /// How far a part may be moved to close a seam, in kerfs. Infinity keeps the behaviour that
    /// shipped before this setting existed, where the only thing stopping a long slide was the
    /// tooling-clearance check afterwards.
    /// </summary>
    public double MaxSnapTravelKerfs { get; set; } = double.PositiveInfinity;

    /// <summary>The shipped defaults. Callers that pass null get this.</summary>
    public static CommonCuttingTolerances Default => new CommonCuttingTolerances();

    /// <summary>The angular tolerance as a SINE, which is the form the edge tests actually use.</summary>
    public double AngleToleranceSin => Math.Sin(this.AngleToleranceDeg * Math.PI / 180.0);

    /// <summary>Throws when a value cannot describe a usable seam, rather than silently finding none.</summary>
    public void Validate()
    {
      if (this.AngleToleranceDeg <= 0 || this.AngleToleranceDeg >= 90)
      {
        throw new ArgumentOutOfRangeException(nameof(this.AngleToleranceDeg), this.AngleToleranceDeg, "Angle tolerance must be between 0 and 90 degrees.");
      }

      if (this.GapMinKerfs < 0 || this.GapMaxKerfs <= this.GapMinKerfs)
      {
        throw new ArgumentOutOfRangeException(nameof(this.GapMaxKerfs), this.GapMaxKerfs, "The gap window must be a positive range (GapMin < GapMax).");
      }

      if (this.MinEdgeLengthKerfs < 0 || this.MinEdgeLengthAbsolute < 0 || this.MinOverlapKerfs < 0)
      {
        throw new ArgumentOutOfRangeException(nameof(this.MinEdgeLengthKerfs), "Lengths cannot be negative.");
      }
    }
  }
}
