namespace DeepNestLib.NestProject
{
  /// <summary>
  /// When common-line cutting is real, and when it would only be a claim.
  /// <para>Common line nests copies of a part TOUCHING so the shared edge is cut ONCE. That only happens if
  /// whatever cuts the sheet knows the edge is one cut and not two. On a SheetCam round trip it does: the
  /// arrangement is written back into that same nest document, which keeps its own leads and loops. A plain
  /// DXF carries no such notion, and the exporter has none either, so both parts would be cut in full and
  /// the shared line run twice, ruining the edge between them.</para>
  /// <para>ONE definition, said in one place, because the part list, the Edit Part dialog and the nester all
  /// have to agree: a caption that says "common line" over a job that will not cut that way is worse than
  /// no caption at all.</para>
  /// </summary>
  public static class CommonLineRule
  {
    /// <summary>Whether this part came in from a SheetCam nest file, which is the only route back out to
    /// something that understands a shared cut.</summary>
    public static bool CameFromSheetCamNest(IDetailLoadInfo part)
      => !string.IsNullOrWhiteSpace(part?.NestSourcePath);

    /// <summary>Whether this part really will be nested common-line: it was asked for AND there is a cutter
    /// on the other end that can honour it.</summary>
    public static bool Applies(IDetailLoadInfo part)
      => part != null && part.CommonLine && CameFromSheetCamNest(part);
  }
}
