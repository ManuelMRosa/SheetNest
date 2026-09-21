namespace DeepNestSharp.Ui
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using System.Windows;
  using System.Windows.Media;
  using DeepNestLib.IO;
  using IxMilia.Dxf;
  using IxMilia.Dxf.Entities;

  /// <summary>
  /// What a part carries engraved on it, as something that can be drawn.
  /// </summary>
  /// <remarks>
  /// <para>The text is traced into strokes, so a mark is one shape wherever a part is drawn: the
  /// thumbnail, the viewer and the printed report all take a geometry and none of them has to know what
  /// a DXF is. Everything comes back in the drawing's own frame, Y upwards, for the caller to place the
  /// way it places the part itself.</para>
  /// <para>The letters are traced with a font off this machine rather than the one the drawing names.
  /// AutoCAD's own fonts are SHX, which cannot be read, so the shapes on screen are close but not
  /// identical. What gets cut is the DXF, and there the text keeps the font it was drawn with.</para>
  /// </remarks>
  internal static class PartMarkDrawing
  {
    /// <summary>
    /// The engraving on a part, placed the way the part is placed. Null when the part carries none, so
    /// a caller can skip the whole business for the parts that have nothing on them.
    /// </summary>
    internal static Geometry Placed(string partPath, double x, double y, double rotation, bool mirrored)
    {
      IReadOnlyList<DxfEntity> marks = DxfPartMarks.For(partPath);
      if (marks.Count == 0)
      {
        return null;
      }

      // Mirror about the origin, then rotate, then move onto the sheet: the same order the nested
      // geometry was built in and the exporter writes in, so what is drawn is where it will be cut.
      var placement = new TransformGroup();
      if (mirrored)
      {
        placement.Children.Add(new ScaleTransform(-1, 1));
      }

      placement.Children.Add(new RotateTransform(rotation));
      placement.Children.Add(new TranslateTransform(x, y));

      var group = new GeometryGroup { FillRule = FillRule.Nonzero };
      foreach (DxfEntity mark in marks)
      {
        // Text places itself. Carried along by the placement it would come out reflected, and a part
        // number that reads backwards is the difference between a part and a piece of scrap.
        Geometry lettering = Lettering(mark, x, y, rotation, mirrored);
        if (lettering != null)
        {
          group.Children.Add(lettering);
          continue;
        }

        Geometry drawn = Draw(mark);
        if (drawn != null)
        {
          drawn.Transform = placement;
          group.Children.Add(drawn);
        }
      }

      return group.Children.Count == 0 ? null : group;
    }

    /// <summary>
    /// The same marks as lines to follow, for a surface that draws points rather than geometry. Empty
    /// when the part carries nothing.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<Point>> Strokes(
      string partPath,
      double x,
      double y,
      double rotation,
      bool mirrored)
    {
      Geometry marks = Placed(partPath, x, y, rotation, mirrored);
      if (marks == null)
      {
        return Array.Empty<IReadOnlyList<Point>>();
      }

      var strokes = new List<IReadOnlyList<Point>>();
      PathGeometry flattened = marks.GetFlattenedPathGeometry();
      foreach (PathFigure figure in flattened.Figures)
      {
        var points = new List<Point> { figure.StartPoint };
        foreach (PathSegment segment in figure.Segments)
        {
          if (segment is PolyLineSegment poly)
          {
            points.AddRange(poly.Points);
          }
          else if (segment is LineSegment line)
          {
            points.Add(line.Point);
          }
        }

        if (figure.IsClosed && points.Count > 1)
        {
          points.Add(points[0]);
        }

        if (points.Count > 1)
        {
          strokes.Add(points);
        }
      }

      return strokes;
    }

    /// <summary>A line of text traced into strokes and put where it will be cut, reading the right way
    /// round. Null when the mark is drawn rather than written.</summary>
    private static Geometry Lettering(DxfEntity mark, double x, double y, double rotation, bool mirrored)
    {
      string words;
      DxfPoint at;
      double height;
      double own;

      // Which way the words sit round the point they hang from, as fractions of the text's own width and
      // height. A DXF says this with a justification or an attachment point, and getting it wrong here
      // would draw a centred part number in the corner of the part while the exporter writes it in the
      // middle: the screen has to agree with what gets cut.
      double acrossFrom;
      double downFrom;
      switch (mark)
      {
        case DxfText text when !string.IsNullOrWhiteSpace(text.Value):
          words = text.Value;
          height = text.TextHeight;
          own = text.Rotation;

          at = DxfPartMarks.AnchorOf(text);
          acrossFrom = Across(text.HorizontalTextJustification);
          downFrom = Down(text.VerticalTextJustification);
          break;
        case DxfMText mtext when !string.IsNullOrWhiteSpace(mtext.Text):
          words = mtext.Text;
          at = mtext.InsertionPoint;
          height = mtext.InitialTextHeight;
          own = mtext.RotationAngle;
          acrossFrom = Across(mtext.AttachmentPoint);
          downFrom = Down(mtext.AttachmentPoint);
          break;
        default:
          return null;
      }

      if (height <= 0)
      {
        return null;
      }

      var formatted = new FormattedText(
        words,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        new Typeface("Consolas"),
        height,
        Brushes.Black,
        1.0);

      Geometry letters = formatted.BuildGeometry(new Point(0, 0));
      if (letters == null || letters.IsEmpty())
      {
        return null;
      }

      (double angle, bool flip) = DxfPartMarks.TextPlacement(mirrored, rotation, own);
      Point anchor = Place(at, x, y, rotation, mirrored);
      if (flip)
      {
        // Turning the part over hangs the words from their other end, so they sit the other way round
        // the point as well.
        acrossFrom = 1.0 - acrossFrom;
      }

      var placed = new TransformGroup();

      // Text is traced the way a screen reads, downwards; in a drawing Y goes up, so it hangs below its
      // anchor instead of above it.
      placed.Children.Add(new ScaleTransform(1, -1));
      placed.Children.Add(new TranslateTransform(
        -formatted.Width * acrossFrom,
        formatted.Height * downFrom));
      placed.Children.Add(new RotateTransform(angle));
      placed.Children.Add(new TranslateTransform(anchor.X, anchor.Y));
      letters.Transform = placed;
      return letters;
    }

    /// <summary>How far along its own width a line of text sits from the point it hangs from.</summary>
    private static double Across(DxfHorizontalTextJustification justification)
    {
      switch (justification)
      {
        case DxfHorizontalTextJustification.Center:
        case DxfHorizontalTextJustification.Middle:
          return 0.5;
        case DxfHorizontalTextJustification.Right:
          return 1.0;
        default:
          return 0.0;
      }
    }

    private static double Across(DxfAttachmentPoint attachment)
    {
      switch (attachment)
      {
        case DxfAttachmentPoint.TopCenter:
        case DxfAttachmentPoint.MiddleCenter:
        case DxfAttachmentPoint.BottomCenter:
          return 0.5;
        case DxfAttachmentPoint.TopRight:
        case DxfAttachmentPoint.MiddleRight:
        case DxfAttachmentPoint.BottomRight:
          return 1.0;
        default:
          return 0.0;
      }
    }

    /// <summary>How far down its own height a line of text sits from the point it hangs from. Text on a
    /// baseline stands ON the point, which is most of the way down it.</summary>
    private static double Down(DxfVerticalTextJustification justification)
    {
      switch (justification)
      {
        case DxfVerticalTextJustification.Top:
          return 0.0;
        case DxfVerticalTextJustification.Middle:
          return 0.5;
        default:
          return 0.8;
      }
    }

    private static double Down(DxfAttachmentPoint attachment)
    {
      switch (attachment)
      {
        case DxfAttachmentPoint.MiddleLeft:
        case DxfAttachmentPoint.MiddleCenter:
        case DxfAttachmentPoint.MiddleRight:
          return 0.5;
        case DxfAttachmentPoint.BottomLeft:
        case DxfAttachmentPoint.BottomCenter:
        case DxfAttachmentPoint.BottomRight:
          return 1.0;
        default:
          return 0.0;
      }
    }

    /// <summary>Where a point on a part ends up once the part has been placed.</summary>
    private static Point Place(DxfPoint at, double x, double y, double rotation, bool mirrored)
    {
      double px = mirrored ? -at.X : at.X;
      double radians = rotation * Math.PI / 180.0;
      double cos = Math.Cos(radians);
      double sin = Math.Sin(radians);
      return new Point(
        (px * cos) - (at.Y * sin) + x,
        (px * sin) + (at.Y * cos) + y);
    }

    private static Geometry Draw(DxfEntity mark)
    {
      switch (mark)
      {
        case DxfArc arc:
          return Sweep(arc);
        case DxfCircle circle:
          return new EllipseGeometry(Point(circle.Center), circle.Radius, circle.Radius);
        case DxfLine line:
          return new LineGeometry(Point(line.P1), Point(line.P2));
        case DxfLwPolyline lwPolyline:
          return Chain(lwPolyline.Vertices.Select(v => new Point(v.X, v.Y)).ToList(), lwPolyline.IsClosed);
        case DxfPolyline polyline:
          return Chain(polyline.Vertices.Select(v => new Point(v.Location.X, v.Location.Y)).ToList(), polyline.IsClosed);
        default:
          return null;
      }
    }

    private static Geometry Sweep(DxfArc arc)
    {
      double start = arc.StartAngle * Math.PI / 180.0;
      double end = arc.EndAngle * Math.PI / 180.0;
      var from = new Point(arc.Center.X + (arc.Radius * Math.Cos(start)), arc.Center.Y + (arc.Radius * Math.Sin(start)));
      var to = new Point(arc.Center.X + (arc.Radius * Math.Cos(end)), arc.Center.Y + (arc.Radius * Math.Sin(end)));
      var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
      figure.Segments.Add(new ArcSegment(
        to,
        new Size(arc.Radius, arc.Radius),
        0,
        DxfPartMarks.Normalise(arc.EndAngle - arc.StartAngle) > 180.0,
        SweepDirection.Counterclockwise,
        true));
      var path = new PathGeometry();
      path.Figures.Add(figure);
      return path;
    }

    private static Geometry Chain(IReadOnlyList<Point> points, bool closed)
    {
      if (points.Count < 2)
      {
        return null;
      }

      var figure = new PathFigure { StartPoint = points[0], IsClosed = closed, IsFilled = false };
      for (int i = 1; i < points.Count; i++)
      {
        figure.Segments.Add(new LineSegment(points[i], true));
      }

      var path = new PathGeometry();
      path.Figures.Add(figure);
      return path;
    }

    private static Point Point(DxfPoint point) => new Point(point.X, point.Y);
  }
}
