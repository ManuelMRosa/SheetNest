namespace SheetNest.Localization
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Threading;

  /// <summary>
  /// Which languages SheetNest can show, and which one it shows. A language is available when its translation
  /// was built next to the program (a culture folder holding this assembly's resources), so adding a
  /// Strings.xx.resx is all a translator does: no list in code to update.
  /// </summary>
  public static class Languages
  {
    /// <summary>The language of Strings.resx, and the one shown when nothing better is available.</summary>
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private static readonly string ResourcesFile = typeof(Languages).Assembly.GetName().Name + ".resources.dll";

    /// <summary>English, then every translation found in <paramref name="baseDirectory"/>, by culture name.</summary>
    public static IReadOnlyList<CultureInfo> Available(string baseDirectory)
    {
      var found = new List<CultureInfo> { English };
      if (!Directory.Exists(baseDirectory))
      {
        return found;
      }

      // Other components ship their own es\, de\ folders; only a folder with OUR resources is a translation.
      foreach (string folder in Directory.GetDirectories(baseDirectory).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
      {
        if (File.Exists(Path.Combine(folder, ResourcesFile)) && TryCulture(Path.GetFileName(folder), out CultureInfo culture)
            && !found.Contains(culture))
        {
          found.Add(culture);
        }
      }

      return found;
    }

    /// <summary>
    /// The language to show: the one the user picked, if it is still available; otherwise the Windows
    /// language or its parent (es-MX shows es) when translated; otherwise English, so a Windows in a language
    /// SheetNest lacks does not get the system's own menus in one language and SheetNest in another.
    /// </summary>
    public static CultureInfo Resolve(string? saved, CultureInfo windows, IReadOnlyList<CultureInfo> available)
    {
      if (!string.IsNullOrWhiteSpace(saved))
      {
        CultureInfo? picked = available.FirstOrDefault(c => string.Equals(c.Name, saved, StringComparison.OrdinalIgnoreCase));
        if (picked != null)
        {
          return picked;
        }
      }

      for (CultureInfo culture = windows; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
      {
        CultureInfo? match = available.FirstOrDefault(c => string.Equals(c.Name, culture.Name, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
          return match;
        }
      }

      return English;
    }

    /// <summary>
    /// Shows <paramref name="culture"/> from now on, on every thread. Only the UI culture: numbers and the
    /// files SheetNest reads and writes keep the formatting they have today.
    /// </summary>
    public static void Apply(CultureInfo culture)
    {
      CultureInfo.DefaultThreadCurrentUICulture = culture;
      Thread.CurrentThread.CurrentUICulture = culture;
    }

    /// <summary>A language's name the way its speakers write it, capitalised for a menu: English, Español.</summary>
    public static string DisplayName(CultureInfo culture)
    {
      string name = culture.NativeName;
      return name.Length == 0 ? culture.Name : char.ToUpper(name[0], culture) + name.Substring(1);
    }

    /// <summary>
    /// <paramref name="one"/> or <paramref name="other"/> with the count in place of {0}, and
    /// <paramref name="more"/> in place of {1}, {2}...: "1 sheet", "3 sheets".
    /// </summary>
    public static string Count(int count, string one, string other, params object[] more)
      => string.Format(CultureInfo.CurrentCulture, count == 1 ? one : other, new object[] { count }.Concat(more).ToArray());

    private static bool TryCulture(string name, out CultureInfo culture)
    {
      try
      {
        culture = CultureInfo.GetCultureInfo(name);
        return culture.Name.Length > 0;
      }
      catch (CultureNotFoundException)
      {
        culture = CultureInfo.InvariantCulture;
        return false;
      }
    }
  }
}
