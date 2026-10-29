using System.Globalization;
using System.Text.Json;

namespace Lyricod.Core;

public sealed record AppRelease(Version Version, Uri Url);
public enum UpdateStatus { UpToDate, Available, CouldntCheck }
public sealed record UpdateResult(UpdateStatus Status, AppRelease? Release = null);

public static class UpdateCheck
{
    public const string Endpoint = "https://api.github.com/repos/Indarkdust/Lyricod-App/releases?per_page=30";

    public static Version? ParseVersion(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Any(c => c < '0' || c > '9'))) return null;
        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return new Version(numbers[0], numbers[1], numbers[2]);
    }

    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck == null || now - lastCheck.Value >= TimeSpan.FromHours(24) || lastCheck > now;

    public static UpdateResult Evaluate(string json, int statusCode, string currentVersion)
    {
        var failed = new UpdateResult(UpdateStatus.CouldntCheck);
        if (statusCode != 200 || ParseVersion(currentVersion) is not { } current) return failed;
        try
        {
            using var document = JsonDocument.Parse(json);
            AppRelease? newest = null;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                // Read required fields even for excluded releases: an unexpected API shape is a failed check.
                var tag = release.GetProperty("tag_name").GetString();
                var url = release.GetProperty("html_url").GetString();
                bool draft = release.GetProperty("draft").GetBoolean();
                bool prerelease = release.GetProperty("prerelease").GetBoolean();
                if (draft || prerelease || tag == null || !tag.StartsWith("v", StringComparison.Ordinal)
                    || ParseVersion(tag[1..]) is not { } version
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") continue;
                if (newest == null || version > newest.Version) newest = new AppRelease(version, uri);
            }
            if (newest == null) return failed;
            return newest.Version > current ? new(UpdateStatus.Available, newest) : new(UpdateStatus.UpToDate);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return failed; }
    }

    public static async Task<UpdateResult> FetchAsync(HttpClient client, string currentVersion)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd($"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Lyricod/{currentVersion} (+https://github.com/Indarkdust/Lyricod-App)");
        try
        {
            using var response = await client.SendAsync(request);
            return Evaluate(await response.Content.ReadAsStringAsync(), (int)response.StatusCode, currentVersion);
        }
        catch (Exception) { return new(UpdateStatus.CouldntCheck); } // offline, timed out, or a cut-off reply
    }
}
