namespace DeepNestSharp.Ui.Converters
{
  using System;
  using System.Globalization;
  using System.Windows.Data;
  using DeepNestLib.Placement;

  /// <summary>
  /// Bridges the "Keep reusable offcut" checkbox to the engine's placement strategy:
  /// checked = Gravity (parts pushed to one side, leaving a clean rectangular remnant),
  /// unchecked = BoundingBox (tightest overall pack). Hides the Gravity/BoundingBox/Squeeze jargon.
  /// </summary>
  public class OffcutPlacementConverter : IValueConverter
  {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
      return value is PlacementTypeEnum p && p == PlacementTypeEnum.Gravity;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
      return value is bool b && b ? PlacementTypeEnum.Gravity : PlacementTypeEnum.BoundingBox;
    }
  }
}
