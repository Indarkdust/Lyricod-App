using System.Globalization;
using System.Text.Json;

namespace Lyricod.Core;

public sealed record AppRelease(Version Version, Uri Url)
{
    public IReadOnlyList<UpdateAsset> Assets { get; init; } = [];
}
public sealed record UpdateAsset(string Name, long Size, Uri Url, string? Sha256);
public enum UpdateStatus { UpToDate, Available, CouldntCheck }
/// <summary>Why a check failed, so the app can say what to do about it.</summary>
public enum CheckFailure { None, Unreachable, RateLimited, Other }
public sealed record UpdateResult(UpdateStatus Status, AppRelease? Release = null)
{
    public CheckFailure Failure { get; init; }
    /// <summary>When GitHub takes requests from this network again, if it said.</summary>
    public DateTimeOffset? RetryAt { get; init; }
}

public static class UpdateCheck
{
    public const string Endpoint = "https://api.github.com/repos/Indarkdust/Lyricod-App/releases?per_page=30";
    /// <summary>For a look by hand when the check itself fails.</summary>
    public const string ReleasesPage = "https://github.com/Indarkdust/Lyricod-App/releases/latest";

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
                if (newest == null || version > newest.Version) newest = new AppRelease(version, uri) { Assets = ReadAssets(release) };
            }
            if (newest == null) return failed;
            return newest.Version > current ? new(UpdateStatus.Available, newest) : new(UpdateStatus.UpToDate);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { return failed; }
    }

    static IReadOnlyList<UpdateAsset> ReadAssets(JsonElement release)
    {
        var result = new List<UpdateAsset>();
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return result;
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object
                || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                || !asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out long bytes)
                || !asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri)) continue;
            string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            string? hash = digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal)
                && digest[7..].All(Uri.IsHexDigit) ? digest[7..] : null;
            result.Add(new(name.GetString()!, bytes, uri, hash));
        }
        return result;
    }

    public static string UserAgent(string version) =>
        $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Lyricod/{version} (+https://github.com/Indarkdust/Lyricod-App)";

    public static async Task<UpdateResult> FetchAsync(HttpClient client, string currentVersion, string endpoint = Endpoint)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd(UserAgent(currentVersion));
        try
        {
            using var response = await client.SendAsync(request);
            var result = Evaluate(await response.Content.ReadAsStringAsync(), (int)response.StatusCode, currentVersion);
            return result.Status == UpdateStatus.CouldntCheck ? Explain(response, DateTimeOffset.UtcNow) : result;
        }
        // Offline, no route to GitHub, timed out, or a cut-off reply.
        catch (Exception) { return new(UpdateStatus.CouldntCheck) { Failure = CheckFailure.Unreachable }; }
    }

    /// <summary>
    /// Why GitHub's answer wasn't a list of releases. Without a token GitHub takes 60 requests an hour from
    /// one address, which a shared or proxied network can use up; it then answers 403 or 429 and says when
    /// it takes requests again.
    /// </summary>
    public static UpdateResult Explain(HttpResponseMessage response, DateTimeOffset now)
    {
        int code = (int)response.StatusCode;
        bool spent = response.Headers.TryGetValues("x-ratelimit-remaining", out var left) && left.FirstOrDefault() == "0";
        var retryAfter = response.Headers.RetryAfter;
        if (code == 429 || code == 403 && (spent || retryAfter != null))
        {
            DateTimeOffset? at = retryAfter?.Date ?? (retryAfter?.Delta is { } wait ? now + wait : null);
            if (at == null && response.Headers.TryGetValues("x-ratelimit-reset", out var reset)
                && long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
                at = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return new(UpdateStatus.CouldntCheck) { Failure = CheckFailure.RateLimited, RetryAt = at };
        }
        return new(UpdateStatus.CouldntCheck) { Failure = CheckFailure.Other };
    }
}
