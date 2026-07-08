namespace DeepNestSharp.Domain
{
  using System;
  using System.Net.Http;
  using System.Reflection;
  using System.Text.Json;
  using System.Threading.Tasks;

  /// <summary>
  /// Checks GitHub for a newer SheetNest release. Notify-only: nothing is downloaded and nothing
  /// about the user is sent — a single anonymous GET to the public releases API. Every failure
  /// (offline shop PC, rate limit, bad JSON) returns null so callers can stay silent.
  /// </summary>
  public static class UpdateChecker
  {
    public const string ReleasesPageUrl = "https://github.com/ManuelMRosa/SheetNest/releases";

    private const string LatestReleaseApi = "https://api.github.com/repos/ManuelMRosa/SheetNest/releases/latest";

    /// <summary>Gets the running app version (the exe's, not this library's), normalized to 3 fields.</summary>
    public static Version CurrentVersion
    {
      get
      {
        var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor < 0 ? 0 : v.Minor, v.Build < 0 ? 0 : v.Build);
      }
    }

    /// <summary>Latest published release (version + download page), or null on any failure.</summary>
    public static async Task<(Version Latest, string Url)?> CheckAsync()
    {
      try
      {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add("User-Agent", "SheetNest");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        var json = await http.GetStringAsync(LatestReleaseApi).ConfigureAwait(false);
        return TryParseLatest(json);
      }
      catch
      {
        return null; // offline / rate-limited / anything — the caller stays silent
      }
    }

    /// <summary>Parses the GitHub "latest release" JSON into (version, page url); null if unusable.</summary>
    public static (Version Latest, string Url)? TryParseLatest(string json)
    {
      try
      {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tag_name", out var tagProp))
        {
          return null;
        }

        var tag = (tagProp.GetString() ?? string.Empty).Trim().TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var latest))
        {
          return null;
        }

        string url = ReleasesPageUrl;
        if (doc.RootElement.TryGetProperty("html_url", out var urlProp) && !string.IsNullOrWhiteSpace(urlProp.GetString()))
        {
          url = urlProp.GetString();
        }

        return (latest, url);
      }
      catch
      {
        return null;
      }
    }

    /// <summary>True when the published release is newer than the running app (3-field compare).</summary>
    public static bool IsNewer(Version latest, Version current)
    {
      static Version Norm(Version v) => new Version(v.Major, v.Minor < 0 ? 0 : v.Minor, v.Build < 0 ? 0 : v.Build);
      return Norm(latest) > Norm(current);
    }

    /// <summary>Opens the release page in the default browser (net6 needs UseShellExecute for URLs).</summary>
    public static void OpenDownloadPage(string url)
    {
      try
      {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
      }
      catch
      {
        // no browser / blocked — nothing sensible to do
      }
    }
  }
}
