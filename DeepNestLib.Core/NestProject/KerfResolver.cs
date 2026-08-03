namespace DeepNestLib.NestProject
{
  /// <summary>
  /// Where a part's kerf comes from. Common cutting is built on the kerf: it is the gap the parts are
  /// placed at and the width of the single cut that severs both, so with no kerf there is no seam to
  /// find and the whole feature quietly does nothing.
  /// </summary>
  /// <remarks>
  /// Until this existed the kerf could only be INFERRED, from the lead geometry of a SheetCam .nest, so
  /// a plain DXF always came through as zero and could never common cut at all.
  /// </remarks>
  public static class KerfResolver
  {
    /// <summary>
    /// Below this a number is not a cut width, it is noise, and counts as "not set".
    /// </summary>
    /// <remarks>
    /// One micron. No cutting process has a kerf anywhere near it, so the threshold cannot reject a
    /// figure anybody meant; what it catches is floating point residue. Stepping a spinner up and back
    /// down in hundredths does not land on exactly zero, and a job arrived carrying 1.39e-17 mm as a
    /// per-part kerf because of it.
    /// </remarks>
    public const double MinimumMeaningfulKerfMm = 0.001;

    private static bool IsSet(double kerfMm) => kerfMm >= MinimumMeaningfulKerfMm;

    /// <summary>
    /// The kerf to use, in millimetres, most specific source first: this part, then the job, then
    /// whatever was measured off a SheetCam nest file. Zero when nobody has said.
    /// </summary>
    /// <param name="partKerfMm">Per-part override; not positive = not set.</param>
    /// <param name="jobKerfMm">Job-wide setting; not positive = not set.</param>
    /// <param name="derivedKerfMm">Measured from a .nest file's leads; not positive = none available.</param>
    /// <remarks>
    /// A typed-in number beats the derived one on purpose. <c>SheetCamNestFile.DeriveKerfMm</c> takes the
    /// MAXIMUM over the leads and caps at 2 mm, and its own notes admit that tessellated curves bias the
    /// readings, so it errs high. It is also a measurement of a file that may have been posted for a
    /// different tool. A number the operator wrote is a statement about the machine running today.
    /// </remarks>
    public static double ResolveMm(double partKerfMm, double jobKerfMm, double derivedKerfMm)
      => IsSet(partKerfMm) ? partKerfMm
        : IsSet(jobKerfMm) ? jobKerfMm
        : derivedKerfMm > 0 ? derivedKerfMm
        : 0;

    /// <summary>
    /// The same precedence, but answering in the units the drawing is in.
    /// </summary>
    /// <param name="derivedDrawingUnits">
    /// The derived kerf ALREADY in drawing units, which is the form it is carried in everywhere it is
    /// used. Taking it in that form avoids converting it back to millimetres only to convert it again.
    /// </param>
    /// <param name="mmToDrawingUnits">1 when the drawing is in millimetres, 1/25.4 when it is in inches.</param>
    public static double ResolveDrawingUnits(double partKerfMm, double jobKerfMm, double derivedDrawingUnits, double mmToDrawingUnits)
      => IsSet(partKerfMm) ? partKerfMm * mmToDrawingUnits
        : IsSet(jobKerfMm) ? jobKerfMm * mmToDrawingUnits
        : derivedDrawingUnits > 0 ? derivedDrawingUnits
        : 0;
  }
}
