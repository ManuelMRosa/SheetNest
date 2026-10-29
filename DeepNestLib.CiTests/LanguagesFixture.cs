namespace DeepNestLib.CiTests
{
  using System;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using FluentAssertions;
  using SheetNest.Localization;
  using Xunit;

  /// <summary>
  /// Which language SheetNest opens in: the one the operator picked, else the Windows language when it is
  /// translated, else English. And which languages the Language menu offers: the translations that were built.
  /// </summary>
  public class LanguagesFixture
  {
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es");
    private static readonly CultureInfo[] EnglishAndSpanish = { Languages.English, Spanish };

    [Fact]
    public void ThePickedLanguageWinsOverWindows()
    {
      Languages.Resolve("es", CultureInfo.GetCultureInfo("en-US"), EnglishAndSpanish).Should().Be(Spanish);
      Languages.Resolve("en", CultureInfo.GetCultureInfo("es-MX"), EnglishAndSpanish).Should().Be(Languages.English);
    }

    [Fact]
    public void WithoutAPickARegionalWindowsGetsItsLanguage()
    {
      Languages.Resolve(null, CultureInfo.GetCultureInfo("es-MX"), EnglishAndSpanish)
        .Should().Be(Spanish, "Mexican Spanish Windows is served by the Spanish translation");
      Languages.Resolve(string.Empty, CultureInfo.GetCultureInfo("es-ES"), EnglishAndSpanish).Should().Be(Spanish);
    }

    /// <summary>
    /// A German Windows has no German SheetNest. It gets English, not German, so the menus Windows itself
    /// draws in German do not end up beside SheetNest's own texts in English.
    /// </summary>
    [Fact]
    public void AnUntranslatedWindowsLanguageGetsEnglish()
    {
      Languages.Resolve(null, CultureInfo.GetCultureInfo("de-DE"), EnglishAndSpanish).Should().Be(Languages.English);
    }

    /// <summary>A pick whose translation is gone (a later build dropped it) falls back as if nothing was picked.</summary>
    [Fact]
    public void APickThatIsNoLongerAvailableFollowsWindows()
    {
      Languages.Resolve("fr", CultureInfo.GetCultureInfo("es-AR"), EnglishAndSpanish).Should().Be(Spanish);
    }

    /// <summary>
    /// Only folders holding SheetNest's own resources are translations: components ship their own es\ or de\
    /// folders, and a folder with another name is not a language at all.
    /// </summary>
    [Fact]
    public void TheAvailableLanguagesAreTheTranslationsThatWereBuilt()
    {
      string dir = Path.Combine(Path.GetTempPath(), $"sheetnest-languages-{Guid.NewGuid():N}");
      try
      {
        Directory.CreateDirectory(Path.Combine(dir, "es"));
        File.WriteAllText(Path.Combine(dir, "es", "SheetNest.Localization.resources.dll"), string.Empty);
        Directory.CreateDirectory(Path.Combine(dir, "de"));
        File.WriteAllText(Path.Combine(dir, "de", "SomeComponent.resources.dll"), string.Empty);
        Directory.CreateDirectory(Path.Combine(dir, "runtimes"));

        Languages.Available(dir).Select(c => c.Name).Should().Equal("en", "es");
      }
      finally
      {
        Directory.Delete(dir, recursive: true);
      }
    }

    [Fact]
    public void ALanguageIsListedInItsOwnWords()
    {
      Languages.DisplayName(Spanish).Should().Be("Español");
      Languages.DisplayName(Languages.English).Should().Be("English");
    }

    [Fact]
    public void ACountPicksTheSingularOnlyForOne()
    {
      Languages.Count(1, "{0} sheet", "{0} sheets").Should().Be("1 sheet");
      Languages.Count(0, "{0} sheet", "{0} sheets").Should().Be("0 sheets");
      Languages.Count(3, "{0} sheet", "{0} sheets").Should().Be("3 sheets");
      Languages.Count(2, "{0} parts on {1}", "{0} parts on {1}", "the sheet").Should().Be("2 parts on the sheet");
    }
  }
}
