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
    /// <summary>
    /// The ConverterParameter that asks for an outline instead of a filled drawing. The parts list passes it,
    /// to show parts the way the printed report draws them with their colour as a square beside them; the
    /// Edit Part dialog passes nothing and keeps its filled preview.
    /// </summary>
    private const string OutlineParameter = "Outline";

    /// <summary>Outline drawings are cached under their own key, so one kind is never handed out as the other.</summary>
    private const string OutlineKeySuffix = "|outline";

    private static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>();

    /// <summary>
    /// Colour per part file, so a filled preview is drawn in the colour that part has on the sheet. Set by the
    /// window that owns the part list, because a converter is created in XAML and only ever receives the bound
    /// value.
    /// </summary>
    private static IReadOnlyDictionary<string, (byte R, byte G, byte B)> colours;

    /// <summary>Drops the cached thumbnails for a path so they rebuild: used after a 3D part is re-unfolded
    /// (new K-factor) and its flat DXF changes. Both kinds go, filled and outline.</summary>
    public static void Invalidate(string path)
    {
      if (!string.IsNullOrEmpty(path))
      {
        Cache.Remove(path);
        Cache.Remove(path + OutlineKeySuffix);
      }
    }

    /// <summary>Hands the thumbnails the job's part colours. The whole cache goes: it is keyed by path, and
    /// the image now depends on the colour too.</summary>
    public static void SetColours(IReadOnlyDictionary<string, (byte R, byte G, byte B)> partColours)
    {
      colours = partColours;
      Cache.Clear();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      string path = (value as IDetailLoadInfo)?.Path ?? value as string;
      if (string.IsNullOrEmpty(path))
      {
        return null;
      }

      bool outline = string.Equals(parameter as string, OutlineParameter, StringComparison.Ordinal);
      string key = outline ? path + OutlineKeySuffix : path;

      // 3D parts preview the ALREADY-unfolded flat (FreeCAD is far too slow to run per thumbnail);
      // if it isn't unfolded yet, show nothing — PreviewSource refreshes this once loading completes.
      string dxfPath;
      if (StepUnfoldService.IsStepFile(path))
      {
        if (!StepUnfoldService.TryGetCachedDxf(path, out dxfPath))
        {
          return null;
        }
      }
      else if (path.ToLowerInvariant().EndsWith(".dxf"))
      {
        dxfPath = path;
      }
      else
      {
        return null;
      }

      if (Cache.TryGetValue(key, out ImageSource cached))
      {
        return cached;
      }

      ImageSource image = null;
      try
      {
        // Task.Run so a locked-file retry (await Task.Delay) can't deadlock the UI thread we're blocking
        // on. The wait is BOUNDED: the parser retries a locked file for seconds, and a thumbnail isn't
        // worth freezing the UI that long — on timeout show no image and skip the cache, so the thumbnail
        // can still appear on a later refresh once the file is free.
        var load = System.Threading.Tasks.Task.Run(() => DxfParser.LoadDxfFile(dxfPath));
        if (!load.Wait(1500))
        {
          return null;
        }

        var raw = load.GetAwaiter().GetResult();
        if (raw != null && raw.TryConvertToNfp(0, out INfp nfp))
        {
          image = Build(nfp, PartColors.For(colours, path), outline, dxfPath);
        }
      }
      catch
      {
        image = null;
      }

      Cache[key] = image;
      return image;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    private static ImageSource Build(INfp nfp, (byte R, byte G, byte B) colour, bool outline, string dxfPath)
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

      // An outline has no fill to carry the shape, so its line is heavier: about a pixel at the 48 pixels
      // the parts list shows it at.
      double dim = Math.Max(g.Bounds.Width, g.Bounds.Height);
      var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), dim / (outline ? 40.0 : 60.0));
      pen.Freeze();

      // Filled: the part's own colour in the same anodised finish the viewer paints it with on the sheet.
      // Outline: nothing, the way the printed report draws a part; the list shows the colour as a square
      // beside it instead.
      Brush fill = outline ? null : PartColors.AnodizedBrush(colour);

      var drawing = new GeometryDrawing(fill, pen, g);

      // What the part carries engraved, over the top of it. It comes back in the drawing's own frame, so
      // it is turned over to match the thumbnail, which draws Y downwards.
      Geometry marks = PartMarkDrawing.Placed(dxfPath, 0, 0, 0, false);
      if (marks == null)
      {
        var only = new DrawingImage(drawing);
        only.Freeze();
        return only;
      }

      marks = marks.Clone();
      marks.Transform = new ScaleTransform(1, -1);
      var markPen = new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), dim / 90.0);
      markPen.Freeze();
      var group = new DrawingGroup();
      group.Children.Add(drawing);
      group.Children.Add(new GeometryDrawing(null, markPen, marks));
      var image = new DrawingImage(group);
      image.Freeze();
      return image;
    }

    internal static void AppendContour(StreamGeometryContext ctx, INfp c)
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
