using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Lyricod.Core.Fonts;

/// <summary>A font file downloaded as it was released: where from, and the size and SHA-256 it must have.</summary>
public sealed record FontFile(string Url, string FileName, long Size, string Sha256);

/// <summary>
/// LXGW WenKai Lite (霞鹜文楷), Medium: an open font (SIL OFL 1.1) from its author's GitHub release.
/// Lite is the build the author makes for apps to embed; it has no Hangul, so Korean falls back as with
/// MiSans. It has no bold, so every weight asks for its one face (see <see cref="FontWeights"/>). The
/// file is kept exactly as released: the licence reserves the font's name for unmodified copies.
/// </summary>
public static class WenKaiFont
{
    public static readonly FontFile File = new(
        "https://github.com/lxgw/LxgwWenKai-Lite/releases/download/v1.530/LXGWWenKaiLite-Medium.ttf",
        "LXGWWenKaiLite-Medium.ttf", 13_801_408, "9282063131640c9558a102560cf6d6f8ecc4bb441e74f2bcff032ce4786238d0");

    public const string LicenceUrl = "https://github.com/lxgw/LxgwWenKai-Lite/blob/v1.530/OFL.txt";

    /// <summary>Its name in Lyricod's licenses folder.</summary>
    public const string LicenceFileName = "LXGWWenKaiLite-OFL.txt";

    /// <summary>The names Windows may know it by: the family, then the Medium face's own (older apps' view of it).</summary>
    public static readonly string[] Families = ["LXGW WenKai Lite", "LXGW WenKai Lite Medium"];
}

public static class FontFileDownload
{
    /// <summary>
    /// Downloads <paramref name="file"/> into <paramref name="directory"/>, picking up a part left by an
    /// earlier try where the server allows it, and publishes it only once its size and hash match. A
    /// part that turns out wrong is deleted, so the next try starts over.
    /// </summary>
    public static async Task DownloadAsync(HttpClient http, FontFile file, string directory,
        IProgress<FontDownloadProgress>? progress, CancellationToken stop)
    {
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, file.FileName), part = target + ".part";
        long have = System.IO.File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have < file.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed) have = 0;
            long start = have;
            using var source = await response.Content.ReadAsStreamAsync(stop).ConfigureAwait(false);
            await using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var counted = progress == null ? null : new Offset(progress, start, file.Size);
                await HttpRangeStream.CopyAsync(source, output, file.Size - start, FontDownloadPhase.Font, counted, stop, file.Size - start).ConfigureAwait(false);
            }
        }
        string hash;
        await using (var font = System.IO.File.OpenRead(part))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(font, stop).ConfigureAwait(false));
        }
        if (hash != file.Sha256)
        {
            System.IO.File.Delete(part);
            throw new InvalidDataException("The downloaded font isn't the expected file.");
        }
        System.IO.File.Move(part, target, true);
    }

    /// <summary>Reports progress through the whole file when a download picks up partway.</summary>
    sealed class Offset(IProgress<FontDownloadProgress> inner, long start, long total) : IProgress<FontDownloadProgress>
    {
        public void Report(FontDownloadProgress value) => inner.Report(value with { Completed = start + value.Completed, Total = total });
    }
}
