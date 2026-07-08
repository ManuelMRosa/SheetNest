namespace DeepNestLib.IO
{
  using System;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  public class MergeLine
  {
    private const int FractionalDigits = 4;

    private decimal? slope;
    private decimal? intercept;
    private DxfPoint? left;
    private DxfPoint? right;

    public MergeLine(DxfLine line)
    {
      Line = line;
    }

    public decimal Slope => slope ?? (slope = CalcSlope()).Value;

    public decimal Intercept => intercept ?? (intercept = CalcIntercept(Line)).Value;

    public DxfPoint Left
    {
      get
      {
        if (!left.HasValue)
        {
          SetLeftRight();
        }

        return left.Value;
      }
    }

    public DxfPoint Right
    {
      get
      {
        if (!right.HasValue)
        {
          SetLeftRight();
        }

        return right.Value;
      }
    }

    public DxfLine Line { get; }

    public bool IsVertical => Math.Round(Line.P1.X, FractionalDigits) == Math.Round(Line.P2.X, FractionalDigits);

    private void SetLeftRight()
    {
      if (IsVertical)
      {
        if (Line.P1.Y < Line.P2.Y)
        {
          left = Line.P1;
          right = Line.P2;
        }
        else
        {
          left = Line.P2;
          right = Line.P1;
        }
      }
      else if (Line.P1.X < Line.P2.X)
      {
        left = Line.P1;
        right = Line.P2;
      }
      else
      {
        left = Line.P2;
        right = Line.P1;
      }
    }

    private decimal CalcSlope()
    {
      if (IsVertical)
      {
        return decimal.MaxValue;
      }
      else
      {
        // Full precision on purpose: rounding to 4 digits made every DIAGONAL pair miss the
        // coincidence tolerance (slope error × run length > 0.0001"), so common-line hypotenuses
        // exported as two cuts while axis-aligned edges (exact slope 0) merged fine.
        return (decimal)((Right.Y - Left.Y) / (Right.X - Left.X));
      }
    }

    private decimal CalcIntercept(DxfLine line)
    {
      if (IsVertical)
      {
        return (decimal)Math.Round(line.P1.X, FractionalDigits);
      }
      else
      {
        // Anchor on the canonical LEFT point (not P1, which differs between the two directions the
        // same edge is drawn in) and use the unrounded slope — coincident edges then agree to ~1e-9.
        return (decimal)(Left.Y - (((Right.Y - Left.Y) / (Right.X - Left.X)) * Left.X));
      }
    }
  }
}