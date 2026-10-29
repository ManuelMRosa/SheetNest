namespace DeepNestSharp.CiTests
{
  using System;
  using FluentAssertions;
  using Microsoft.Win32;
  using Xunit;

  /// <summary>
  /// The picked language lives where the installer writes it. "auto", an empty value or no value at all all
  /// mean the Windows language, because those are what a fresh install, a silent install and a portable copy
  /// leave behind. Each test works in a key of its own, never in the real one.
  /// </summary>
  public sealed class LanguagePreferenceFixture : IDisposable
  {
    private readonly string key = $@"Software\SheetNest-test-{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(this.key, throwOnMissingSubKey: false);

    [Fact]
    public void APickedLanguageIsReadBack()
    {
      LanguagePreference.Write("es", this.key);

      LanguagePreference.Read(this.key).Should().Be("es");
    }

    /// <summary>The app writes "follow Windows" the way the installer does, so each reads the other's choice.</summary>
    [Fact]
    public void FollowingWindowsIsStoredAsTheInstallersAuto()
    {
      LanguagePreference.Write(null, this.key);

      LanguagePreference.Read(this.key).Should().BeNull();
      using RegistryKey stored = Registry.CurrentUser.OpenSubKey(this.key);
      stored.GetValue("Language").Should().Be("auto");
    }

    [Fact]
    public void NothingSavedFollowsWindows()
    {
      LanguagePreference.Read(this.key).Should().BeNull("the key does not exist");
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void AnEmptyOrAutomaticValueFollowsWindows(string value)
    {
      using (RegistryKey created = Registry.CurrentUser.CreateSubKey(this.key))
      {
        created.SetValue("Language", value);
      }

      LanguagePreference.Read(this.key).Should().BeNull();
    }
  }
}
