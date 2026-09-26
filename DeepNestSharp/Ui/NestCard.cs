namespace DeepNestSharp.Ui
{
  using System.Collections.Generic;
  using System.Globalization;
  using System.Windows;
  using System.Windows.Media;
  using DeepNestLib.Placement;
  using DeepNestSharp.Ui.Converters;

  /// <summary>
  /// One card in the Nests panel: a distinct layout of the result, how many times it is cut, how much of
  /// the sheet it uses and the sheet's size. Card n is layout n of the viewer, so picking a card is
  /// picking that layout.
  /// </summary>
  internal sealed class NestCard
  {
    public int Index { get; set; }

    public string Name { get; set; }

    public int Sheets { get; set; }

    public string Utilization { get; set; }

    public string Size { get; set; }

    public ImageSource Thumbnail { get; set; }

    /// <summary>The cards for a production plan, in its order. Thumbnails are drawn separately, by
    /// <see cref="DrawThumbnail"/>, so this part stays plain text a test can read.</summary>
    internal static List<NestCard> From(IReadOnlyList<(ISheetPlacement Sheet, int Count, string Name)> plan, bool unitsMm)
    {
      var cards = new List<NestCard>();
      if (plan == null)
      {
        return cards;
      }

      string units = unitsMm ? "mm" : "in";
      for (int i = 0; i < plan.Count; i++)
      {
        // The plan's own name is deliberately not used: a card is named for where it is in the list, and
        // that is the number the operator reads off the report and the drawing.
        var (sheet, count, _) = plan[i];
        cards.Add(new NestCard
        {
          Index = i,
          Name = $"Nest {i + 1}",
          Sheets = count,
          Utilization = (sheet.MaterialUtilization * 100.0).ToString("0.0", CultureInfo.InvariantCulture) + "%",
          Size = $"{Num(sheet.Sheet.WidthCalculated)} × {Num(sheet.Sheet.HeightCalculated)} {units}",
        });
      }

      return cards;
    }

    /// <summary>
    /// A small picture of the layout, drawn the way the printed report draws it: the sheet and every part
    /// with its holes as outlines, nothing filled. Frozen, so the list can hold it without tying it to this
    /// thread.
    /// </summary>
    internal static ImageSource DrawThumbnail(ISheetPlacement sheetPlacement)
    {
      var sheet = sheetPlacement?.Sheet;
      if (sheet == null || sheet.WidthCalculated <= 0 || sheet.HeightCalculated <= 0)
      {
        return null;
      }

      double w = sheet.WidthCalculated;
      double h = sheet.HeightCalculated;

      // With no fill to carry a shape, the lines are all there is: about a pixel wide at the 90 pixels the
      // panel shows the thumbnail at, whatever the sheet measures.
      double stroke = System.Math.Max(w, h) / 110.0;

      // AppendContour flips Y (sheet up, screen down), so the sheet is drawn over the same flipped span.
      var drawing = new DrawingGroup();
      drawing.Children.Add(new GeometryDrawing(
        null,
        new Pen(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), stroke),
        new RectangleGeometry(new Rect(0, -h, w, h))));

      var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), stroke);
      foreach (var pp in sheetPlacement.PartPlacements)
      {
        var placed = pp.PlacedPart;
        if (placed == null || placed.Length < 3)
        {
          continue;
        }

        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var ctx = geometry.Open())
        {
          PartPreviewConverter.AppendContour(ctx, placed);
          if (placed.Children != null)
          {
            foreach (var hole in placed.Children)
            {
              PartPreviewConverter.AppendContour(ctx, hole);
            }
          }
        }

        drawing.Children.Add(new GeometryDrawing(null, outline, geometry));
      }

      var image = new DrawingImage(drawing);
      image.Freeze();
      return image;
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
  }
}
