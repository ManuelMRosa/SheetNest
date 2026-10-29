namespace DeepNestLib.CiTests
{
  using System;
  using System.Collections;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using System.Text.RegularExpressions;
  using FluentAssertions;
  using SheetNest.Localization;
  using Xunit;
  using Xunit.Abstractions;

  /// <summary>
  /// What a translation has to get right before it ships, checked on the translations as built: the
  /// satellite assemblies next to these tests, which are what users run. A text a translation lacks is
  /// allowed, because it shows in English; a wrong one is not, because it breaks the window it is in.
  /// </summary>
  public class TranslationFixture
  {
    private static readonly Regex Placeholder = new Regex(@"\{\d+(?:[,:][^}]*)?\}");

    private readonly ITestOutputHelper output;

    public TranslationFixture(ITestOutputHelper output)
    {
      this.output = output;
    }

    public static IEnumerable<object[]> Translations()
      => Languages.Available(AppContext.BaseDirectory)
        .Where(culture => !culture.Equals(Languages.English))
        .Select(culture => new object[] { culture.Name });

    /// <summary>A key the English file no longer has is a text no window shows: renamed or removed.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslationOnlyHasTextsTheEnglishFileHas(string culture)
    {
      var english = Texts(CultureInfo.InvariantCulture);

      Texts(CultureInfo.GetCultureInfo(culture)).Keys.Except(english.Keys)
        .Should().BeEmpty("every key in Strings.{0}.resx must exist in Strings.resx", culture);
    }

    /// <summary>
    /// {0}, {1:F2} are where SheetNest puts numbers and names. A translation that drops, adds or retypes one
    /// shows the wrong value, or makes the window fail when the message is built.
    /// </summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslationKeepsEveryPlaceholder(string culture)
    {
      var english = Texts(CultureInfo.InvariantCulture);

      var broken = Texts(CultureInfo.GetCultureInfo(culture))
        .Where(t => english.ContainsKey(t.Key) && Placeholders(t.Value) != Placeholders(english[t.Key]))
        .Select(t => $"{t.Key}: \"{english[t.Key]}\" / \"{t.Value}\"");

      broken.Should().BeEmpty("a translated text must carry the same placeholders as the English one");
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void ATranslationHasNoEmptyText(string culture)
    {
      Texts(CultureInfo.GetCultureInfo(culture)).Where(t => string.IsNullOrWhiteSpace(t.Value)).Select(t => t.Key)
        .Should().BeEmpty("an empty text hides the English one it replaces; leave the key out instead");
    }

    /// <summary>Spanish ships with SheetNest, so it is found next to the program and every text is translated.</summary>
    [Fact]
    public void SpanishIsBuiltAndComplete()
    {
      Languages.Available(AppContext.BaseDirectory).Select(c => c.Name).Should().Contain("es");

      Texts(CultureInfo.InvariantCulture).Keys.Except(Texts(CultureInfo.GetCultureInfo("es")).Keys)
        .Should().BeEmpty("Spanish is maintained with the code: a new English text needs its Spanish one");
    }

    /// <summary>Not a check: tells a translator what is left to do in each language.</summary>
    [Fact]
    public void ReportHowMuchOfEachLanguageIsTranslated()
    {
      var english = Texts(CultureInfo.InvariantCulture);
      foreach (CultureInfo culture in Languages.Available(AppContext.BaseDirectory).Where(c => !c.Equals(Languages.English)))
      {
        var translated = Texts(culture);
        var missing = english.Keys.Except(translated.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        this.output.WriteLine($"{culture.Name}: {english.Count - missing.Count} of {english.Count} translated");
        foreach (string key in missing)
        {
          this.output.WriteLine($"  missing: {key}");
        }
      }
    }

    /// <summary>The texts of one language only, without falling back to English.</summary>
    private static Dictionary<string, string> Texts(CultureInfo culture)
    {
      var texts = new Dictionary<string, string>(StringComparer.Ordinal);
      var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
      if (set == null)
      {
        return texts;
      }

      foreach (DictionaryEntry entry in set)
      {
        if (entry.Value is string value)
        {
          texts[(string)entry.Key] = value;
        }
      }

      return texts;
    }

    private static string Placeholders(string text)
      => string.Join(" ", Placeholder.Matches(text).Select(m => m.Value).OrderBy(p => p, StringComparer.Ordinal));
  }
}
