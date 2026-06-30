namespace DeepNestSharp.Ui.Converters
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Windows;
  using System.Windows.Data;
  using System.Windows.Media;
  using DeepNestLib;
  using DeepNestLib.IO;
  using DeepNestLib.NestProject;

  /// <summary>
  /// Builds a small vector <see cref="DrawingImage"/> (outer outline + holes, EvenOdd) from a part's
  /// source DXF, for the thumbnail shown next to each part. A Geometry can't render itself, so it's
  /// wrapped in a GeometryDrawing/DrawingImage and shown via an &lt;Image&gt; (robust auto-scaling).
  /// Cached per file path.
  /// </summary>
  public class PartPreviewConverter : IValueConverter
  {
    private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      string path = (value as IDetailLoadInfo)?.Path ?? value as string;
      if (string.IsNullOrEmpty(path) || !path.ToLowerInvariant().EndsWith(".dxf"))
      {
        return null;
      }

      if (Cache.TryGetValue(path, out ImageSource cached))
      {
        return cached;
      }

      ImageSource image = null;
      try
      {
        var raw = DxfParser.LoadDxfFile(path).GetAwaiter().GetResult();
        if (raw != null && raw.TryConvertToNfp(0, out INfp nfp))
        {
          image = Build(nfp);
        }
      }
      catch
      {
        image = null;
      }

      Cache[path] = image;
      return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static ImageSource Build(INfp nfp)
    {
      var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
      using (var ctx = g.Open())
      {
        AppendContour(ctx, nfp);
        if (nfp.Children != null)
        {
          foreach (var child in nfp.Children)
          {
            AppendContour(ctx, child);
          }
        }
      }

      g.Freeze();
      if (g.Bounds.IsEmpty || g.Bounds.Width <= 0 || g.Bounds.Height <= 0)
      {
        return null;
      }

      double dim = Math.Max(g.Bounds.Width, g.Bounds.Height);
      var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), dim / 60.0);
      pen.Freeze();
      var fill = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x99, 0xDD));
      fill.Freeze();

      var drawing = new GeometryDrawing(fill, pen, g);
      var image = new DrawingImage(drawing);
      image.Freeze();
      return image;
    }

    private static void AppendContour(StreamGeometryContext ctx, INfp c)
    {
      if (c == null || c.Length < 2)
      {
        return;
      }

      ctx.BeginFigure(new Point(c[0].X, -c[0].Y), true, true); // -Y: DXF up -> screen down
      var pts = new List<Point>(c.Length - 1);
      for (int i = 1; i < c.Length; i++)
      {
        pts.Add(new Point(c[i].X, -c[i].Y));
      }

      ctx.PolyLineTo(pts, true, true);
    }
  }
}
