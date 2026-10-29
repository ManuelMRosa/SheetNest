namespace DeepNestSharp.CiTests
{
  using System.Globalization;
  using System.Runtime.CompilerServices;
  using SheetNest.Localization;

  /// <summary>
  /// The tests read SheetNest's texts in English whatever language Windows runs in, so a test that checks a
  /// message reads the same on every machine. A test that wants another language sets
  /// <see cref="Strings.Culture"/> itself and puts it back.
  /// </summary>
  internal static class EnglishTexts
  {
    [ModuleInitializer]
    internal static void Pin() => Strings.Culture = CultureInfo.InvariantCulture;
  }
}
