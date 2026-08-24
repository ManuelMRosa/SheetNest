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
    /// How far a part may be slid ALONG a seam to line the two faces' ends up, in kerfs. It does not
    /// bound the perpendicular move onto the seam, which is the shared cut itself and is already held
    /// under 0.6 of a kerf by the gap window. Travel past this is clamped, not rejected: a partly
    /// aligned seam is still a seam over the overlap, and throwing the candidate away loses the cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ THIS DEFAULTED TO INFINITY AND THAT DESTROYED EVERY SEAM ON A FULL SHEET. Lining two faces up
    /// end to end also slides a part ALONG the seam, and with no bound that slide can be arbitrarily
    /// long. On a packed sheet a long slide runs into some other neighbour, the tooling check fails, and
    /// because a group is applied all-or-nothing the WHOLE chain is reverted. One bad candidate pair
    /// anywhere took every shared cut on the sheet with it.
    /// </para>
    /// <para>
    /// Measured on the shop's own common-line job (26 parts, 120x60in sheet, kerf 0.0134in), sweeping
    /// this value and counting the seams the kept sheet actually has:
    /// </para>
    /// <code>
    /// maxTravel   placed   seams   givenup
    /// infinite    26           0         1
    /// 8 kerfs     26          25         0
    /// 4 kerfs     26          25         0
    /// 2 kerfs     26          25         0
    /// 1 kerf      26          25         0
    /// </code>
    /// <para>
    /// 25 seams over 26 parts is a spanning forest with nothing left out: the whole sheet welded into one
    /// chain, which is the point of common-line cutting. Same 26 parts placed either way, so the bound
    /// costs no material. It held whether every part carried a lead or only one did, which also refuted
    /// the guess that the leads were what blocked the seams.
    /// </para>
    /// <para>
    /// ⚠️ 1, 2, 4 and 8 measured IDENTICAL, so 4 is a judgement inside a measured-safe band and not a
    /// tuned optimum. Only one common-line job exists in the corpus to measure on. What the bound is for
    /// is rejecting a candidate whose faces barely overlap, where lining the ends up would drag the part
    /// a long way; a real seam between parts the packer already put side by side needs a nudge, not a
    /// journey.
    /// </para>
    /// </remarks>
    public double MaxSnapTravelKerfs { get; set; } = 4.0;

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
