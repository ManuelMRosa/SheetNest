namespace DeepNestSharp
{
  using Microsoft.Win32;

  /// <summary>
  /// The language the operator picked, in HKCU\Software\SheetNest\Language: where the installer writes the
  /// choice it asks for, and where Settings > Language writes it later, so each sees what the other chose and
  /// an upgrade offers the language already in use. "auto" (or nothing) means the Windows language.
  /// </summary>
  internal static class LanguagePreference
  {
    internal const string Automatic = "auto";

    private const string DefaultKey = @"Software\SheetNest";
    private const string ValueName = "Language";

    /// <summary>The picked culture name, such as "es"; null to follow Windows. Never throws.</summary>
    internal static string? Read(string keyPath = DefaultKey)
    {
      try
      {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
        string? value = (key?.GetValue(ValueName) as string)?.Trim();
        return string.IsNullOrEmpty(value) || string.Equals(value, Automatic, System.StringComparison.OrdinalIgnoreCase)
          ? null
          : value;
      }
      catch
      {
        // A preference that cannot be read must never stop SheetNest from starting: follow Windows.
        return null;
      }
    }

    /// <summary>Remembers <paramref name="culture"/>, or the Windows language when it is null. Best effort.</summary>
    internal static void Write(string? culture, string keyPath = DefaultKey)
    {
      try
      {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(ValueName, string.IsNullOrWhiteSpace(culture) ? Automatic : culture, RegistryValueKind.String);
      }
      catch
      {
        // Losing the choice beats crashing on a menu click.
      }
    }
  }
}
