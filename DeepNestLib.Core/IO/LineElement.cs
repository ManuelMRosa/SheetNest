namespace DeepNestLib.IO
{
  using System.Drawing;

  public class LineElement
  {
    public PointF Start;
    public PointF End;

    /// <summary>
    /// True when this straight segment is a CHORD the parser cut a curve into, rather than a straight
    /// edge the drawing actually contains.
    /// </summary>
    /// <remarks>
    /// It matters because a chord lies INSIDE its arc: the real curve bulges out past it by up to the
    /// tessellation tolerance. Anything that treats a chord as a real face, such as pulling a neighbour
    /// up to one kerf from it, gives that bulge away and the cut eats into the part.
    /// </remarks>
    public bool Curved;
  }
}
