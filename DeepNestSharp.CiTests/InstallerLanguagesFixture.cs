namespace DeepNestSharp.CiTests
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Text;
  using System.Xml.Linq;
  using FluentAssertions;
  using SheetNest.Localization;
  using Xunit;

  /// <summary>
  /// The installer asks which language SheetNest opens in, from a list written by hand in installer\SheetNest.wxs.
  /// These hold that list to the translations in Localization\, so a language added there without its line in
  /// the installer fails here before an MSI ships without it.
  /// </summary>
  public class InstallerLanguagesFixture
  {
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    [Fact]
    public void TheInstallerOffersAutomaticEnglishAndEveryTranslation()
    {
      string repo = RepositoryRoot();
      var translations = Directory.GetFiles(Path.Combine(repo, "Localization"), "Strings.*.resx")
        .Select(file => Path.GetFileNameWithoutExtension(file).Substring("Strings.".Length));

      InstallerChoices(repo).Select(choice => choice.Value)
        .Should().BeEquivalentTo(new[] { "auto", "en" }.Concat(translations), "every translation must be offered at install");
    }

    /// <summary>
    /// Named the way Settings > Language names it, when the installer can show that name; a language written
    /// in another alphabet goes by its English name there (see <see cref="EveryChoiceFitsTheInstallersCodePage"/>).
    /// </summary>
    [Fact]
    public void EachLanguageIsNamedTheWayTheAppsMenuNamesIt()
    {
      foreach (var (value, text) in InstallerChoices(RepositoryRoot()).Where(choice => choice.Value != "auto"))
      {
        var culture = CultureInfo.GetCultureInfo(value);
        string native = Languages.DisplayName(culture);
        text.Should().Be(
          FitsCodePage1252(native) ? native : culture.EnglishName,
          "Settings > Language names {0} so, or the installer cannot show that name",
          value);
      }
    }

    /// <summary>
    /// The installer's screens take Western European letters only (code page 1252): wix refuses to build the MSI
    /// with any other letter in them, so this fails here instead of at release time.
    /// </summary>
    [Fact]
    public void EveryChoiceFitsTheInstallersCodePage()
    {
      InstallerChoices(RepositoryRoot()).Where(choice => !FitsCodePage1252(choice.Text)).Select(choice => choice.Value)
        .Should().BeEmpty("the MSI cannot be built with these names");
    }

    private static bool FitsCodePage1252(string text)
    {
      Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
      var cp1252 = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
      try
      {
        cp1252.GetBytes(text);
        return true;
      }
      catch (EncoderFallbackException)
      {
        return false;
      }
    }

    private static List<(string Value, string Text)> InstallerChoices(string repo)
      => XDocument.Load(Path.Combine(repo, "installer", "SheetNest.wxs"))
        .Descendants(Wix + "ComboBox")
        .Where(combo => (string)combo.Attribute("Property") == "SHEETNEST_LANGUAGE")
        .SelectMany(combo => combo.Elements(Wix + "ListItem"))
        .Select(item => ((string)item.Attribute("Value"), (string)item.Attribute("Text")))
        .ToList();

    private static string RepositoryRoot()
    {
      for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
      {
        if (File.Exists(Path.Combine(dir.FullName, "DeepNestSharp.sln")))
        {
          return dir.FullName;
        }
      }

      throw new DirectoryNotFoundException("The tests run outside the repository: DeepNestSharp.sln not found above them.");
    }
  }
}
