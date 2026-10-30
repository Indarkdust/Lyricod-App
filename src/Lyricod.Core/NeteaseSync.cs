using System.Text.Json;

namespace Lyricod.Core;

/// <summary>The helper's download: where it is, how big, and its SHA-256 as GitHub lists it.</summary>
public sealed record NeteaseHelperFile(Uri Url, long Size, string Sha256);

/// <summary>How running the helper went: done, or what went wrong (its own words, in English).</summary>
public sealed record NeteaseHelperOutcome(bool Ok, string? Error);

/// <summary>
/// NetEase Cloud Music sync (Settings › General): Lyricod CM Helper, downloaded only when asked,
/// installs BetterNCM and the InfLink-rs plugin into NetEase Cloud Music so it reports where it is
/// in the song. Everything about it lives here, in Platform/NeteaseSetup.cs and in
/// SettingsWindow.NeteaseSync.cs, so it can be taken out whole.
/// </summary>
public static class NeteaseSync
{
    public const string HelperRepository = "darkdust-c/Lyricod-CM-Helper";
    public const string HelperEndpoint = "https://api.github.com/repos/" + HelperRepository + "/releases/latest";
    public const string HelperName = "Lyricod-CM-Helper.exe";
    public const string HelperPage = "https://github.com/" + HelperRepository;

    /// <summary>The NetEase Cloud Music versions sync has been tested with (3.x).</summary>
    public static readonly Version TestedFrom = new(3, 1, 20), TestedTo = new(3, 1, 42);

    public static bool IsTested(Version version)
    {
        var release = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        return release >= TestedFrom && release <= TestedTo;
    }

    /// <summary>
    /// The helper in its latest release, if it comes from that repository's own releases on github.com
    /// and GitHub lists its SHA-256: without one, it can't be checked, so it isn't used.
    /// </summary>
    public static NeteaseHelperFile? FindHelper(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || Text(asset, "name") != HelperName) continue;
                if (!asset.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                    || !size.TryGetInt64(out long bytes) || bytes <= 0
                    || !Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var url)
                    || url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0
                    || !url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    || !url.AbsolutePath.StartsWith($"/{HelperRepository}/releases/download/", StringComparison.OrdinalIgnoreCase)) return null;
                string? digest = Text(asset, "digest");
                if (digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !digest[7..].All(Uri.IsHexDigit))
                    return null;
                return new NeteaseHelperFile(url, bytes, digest[7..]);
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The helper's command line: install or remove, with NetEase's and BetterNCM's folders and where to write the result.</summary>
    public static string Arguments(bool remove, string netease, string data, string result) =>
        $"{(remove ? "remove" : "install")} --netease {Quote(netease)} --data {Quote(data)} --result {Quote(result)}";

    /// <summary>A path in quotes. Backslashes just before the closing quote are doubled, or they'd escape it.</summary>
    static string Quote(string path)
    {
        if (path.Contains('"')) throw new ArgumentException("A path can't contain quotes.", nameof(path));
        int trailing = path.Length - path.TrimEnd('\\').Length;
        return "\"" + path + new string('\\', trailing) + "\"";
    }

    /// <summary>What the helper wrote (<c>{"ok":true}</c> or <c>{"ok":false,"error":"…"}</c>), or what its exit code says.</summary>
    public static NeteaseHelperOutcome ReadResult(string? json, int exitCode)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ok", out var ok)
                    && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    if (ok.GetBoolean() && exitCode == 0) return new(true, null);
                    string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    return new(false, string.IsNullOrWhiteSpace(error) ? $"The helper stopped (code {exitCode})." : error);
                }
            }
            catch (JsonException) { /* fall back to the exit code */ }
        }
        return new(false, $"The helper stopped (code {exitCode}).");
    }

    /// <summary>Which processor an exe is built for, from its header (0x8664: x64), or 0 when it isn't one.</summary>
    public static ushort Machine(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return 0; // "MZ"
        stream.Position = 0x3C;
        uint header = reader.ReadUInt32();
        if (header > stream.Length - 6) return 0;
        stream.Position = header;
        if (reader.ReadUInt32() != 0x00004550) return 0; // "PE\0\0"
        return reader.ReadUInt16();
    }

    public const ushort X64 = 0x8664;
}
