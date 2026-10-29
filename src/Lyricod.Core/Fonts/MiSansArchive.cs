using System.IO.Compression;
using System.Security.Cryptography;

namespace Lyricod.Core.Fonts;

public enum FontDownloadPhase { Archive, Font, Licence }
public sealed record FontDownloadProgress(FontDownloadPhase Phase, long Completed, long? Total);

public static class MiSansArchive
{
    public const string ZipUrl = "https://hyperos.mi.com/font-download/MiSans.zip";
    public const string LicenceUrl = "https://hyperos.mi.com/font-download/MiSans%E5%AD%97%E4%BD%93%E7%9F%A5%E8%AF%86%E4%BA%A7%E6%9D%83%E8%AE%B8%E5%8F%AF%E5%8D%8F%E8%AE%AE.pdf";
    public const string KnownHash = "0ddef90648998900175cfdca9a6f087a2544c182f130b0ad4f7e94a03a115e79";

    public static ZipArchiveEntry FindFont(ZipArchive archive)
    {
        // Xiaomi's folder names aren't UTF-8 flagged. The filename is ASCII.
        var entries = archive.Entries.Where(e =>
            !e.FullName.Replace('\\', '/').Split('/').Contains("__MACOSX", StringComparer.OrdinalIgnoreCase) &&
            e.FullName.Replace('\\', '/').Split('/').Last().Equals("MiSansVF.ttf", StringComparison.OrdinalIgnoreCase)).ToArray();
        return entries.Length == 1 ? entries[0] : throw new InvalidDataException("The MiSans variable font was missing or ambiguous in Xiaomi's ZIP.");
    }

    public static async Task<string> ValidateAsync(string path, CancellationToken stop = default)
    {
        await using var font = File.OpenRead(path);
        byte[] header = new byte[4];
        await font.ReadExactlyAsync(header, stop).ConfigureAwait(false);
        if (font.Length <= 1024 * 1024 || !header.AsSpan().SequenceEqual(new byte[] { 0, 1, 0, 0 }))
            throw new InvalidDataException("The download is not a valid TrueType font.");
        font.Position = 0;
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(font, stop).ConfigureAwait(false));
    }

    /// <summary>Downloads both files before publishing either. The font is committed last.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, string directory,
        IProgress<FontDownloadProgress>? progress, CancellationToken stop)
    {
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        string font = temp + ".ttf", licence = temp + ".pdf";
        try
        {
            try { await ExtractAsync(http, font, progress, stop, useRanges: true).ConfigureAwait(false); }
            catch (Exception e) when (e.GetBaseException() is RangeNotSupportedException)
            {
                // Restart ZIP parsing too: a changed archive can have different offsets.
                await ExtractAsync(http, font, progress, stop, useRanges: false).ConfigureAwait(false);
            }
            string hash = await ValidateAsync(font, stop).ConfigureAwait(false);
            using (var request = new HttpRequestMessage(HttpMethod.Get, LicenceUrl))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var target = File.Create(licence);
                await HttpRangeStream.CopyAsync(source, target, response.Content.Headers.ContentLength, FontDownloadPhase.Licence, progress, stop, 20 * 1024 * 1024).ConfigureAwait(false);
            }
            await using (var pdf = File.OpenRead(licence))
            {
                byte[] header = new byte[5];
                await pdf.ReadExactlyAsync(header, stop).ConfigureAwait(false);
                if (!header.AsSpan().SequenceEqual("%PDF-"u8)) throw new InvalidDataException("The licence download is not a PDF.");
            }
            stop.ThrowIfCancellationRequested();
            File.Move(licence, Path.Combine(directory, "MiSans-License.pdf"), true);
            File.Move(font, Path.Combine(directory, "MiSansVF.ttf"), true);
            return hash;
        }
        finally
        {
            File.Delete(font);
            File.Delete(licence);
        }
    }

    static async Task ExtractAsync(HttpClient http, string font, IProgress<FontDownloadProgress>? progress,
        CancellationToken stop, bool useRanges)
    {
        using var stream = await HttpRangeStream.OpenAsync(http, new Uri(ZipUrl), progress, stop, useRanges).ConfigureAwait(false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = FindFont(archive);
        if (entry.Length > 64 * 1024 * 1024) throw new InvalidDataException("The font is too large.");
        using var source = entry.Open();
        await using var target = File.Create(font);
        await HttpRangeStream.CopyAsync(source, target, entry.Length, FontDownloadPhase.Font, progress, stop, 64 * 1024 * 1024).ConfigureAwait(false);
    }

}
