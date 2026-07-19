namespace DeepNestLib.IO
{
  /// <summary>
  /// The offcut separation cut: one straight segment in sheet coordinates that frees the clean
  /// leftover rectangle a rectangular-offcut nest kept beyond the packed strip. Exporters emit it
  /// as ordinary cut geometry alongside the parts.
  /// </summary>
  public class OffcutLine
  {
    public double X1 { get; set; }

    public double Y1 { get; set; }

    public double X2 { get; set; }

    public double Y2 { get; set; }
  }
}
