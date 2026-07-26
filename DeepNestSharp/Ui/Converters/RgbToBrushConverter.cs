namespace DeepNestSharp.Ui.Converters
{
  using System;
  using System.Globalization;
  using System.Windows.Data;
  using System.Windows.Media;

  /// <summary>
  /// Paints a part's colour as the project stores it (0xRRGGBB; -1 = none chosen) so the swatch on its row
  /// shows the colour itself. One way: a new colour is written from the code-behind, never bound back.
  /// </summary>
  public class RgbToBrushConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      if (!(value is int rgb) || rgb < 0)
      {
        return Brushes.Transparent;
      }

      var (r, g, b) = PartColors.FromRgb(rgb);
      var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
      brush.Freeze();
      return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
  }
}
