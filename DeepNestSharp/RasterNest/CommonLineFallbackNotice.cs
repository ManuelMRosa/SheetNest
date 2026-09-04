namespace DeepNestSharp.RasterNest
{
  using System;

  /// <summary>
  /// Explains why a drawing that requested direct common-line cutting was left at the ordinary part gap.
  /// These notices belong to the candidate that was actually kept; losing parallel searches must not
  /// leak their failures into the operator's result.
  /// </summary>
  internal sealed class CommonLineFallbackNotice
  {
    internal const string ContourFormatReason = "outside contour format is not supported by exact Common Line geometry";
    internal const string TopologyReason = "common-line cluster topology was invalid";

    public CommonLineFallbackNotice(string drawing, string reason)
    {
      this.Drawing = string.IsNullOrWhiteSpace(drawing) ? "(unnamed)" : drawing;
      this.Reason = string.IsNullOrWhiteSpace(reason) ? TopologyReason : reason;
    }

    public string Drawing { get; }

    public string Reason { get; }

    public static string ReasonFromValidation(string validationMessage)
    {
      // Resolver messages deliberately carry more context (for example, "curved common-line outside
      // geometry"). Match the meaning instead of one exact sentence so every unsupported curve
      // container is reported as a format limitation rather than as an unexplained topology failure.
      bool curvedOutside = validationMessage?.IndexOf("curved", StringComparison.OrdinalIgnoreCase) >= 0
        && validationMessage.IndexOf("outside geometry", StringComparison.OrdinalIgnoreCase) >= 0;
      return curvedOutside ? ContourFormatReason : TopologyReason;
    }
  }
}
