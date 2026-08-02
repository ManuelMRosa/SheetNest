namespace DeepNestLib.IO
{
  using System.Collections.Generic;
  using System.Drawing;

  public interface ILocalContour
  {
    List<PointF> Points { get; }

    /// <summary>
    /// Gets, per segment, whether it is a chord of a tessellated curve rather than a straight edge the
    /// drawing contains. Entry <c>i</c> describes the segment from <c>Points[i]</c> to
    /// <c>Points[i + 1]</c>, the last one wrapping back to the start. Null when nobody recorded it.
    /// </summary>
    IReadOnlyList<bool> CurvedSegments { get; }

    bool IsChild { get; }
  }
}
