using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Lyricod.Core;

public sealed record UpdateProgress(long Completed, long Total)
{
    public int Percent => Total > 0 ? (int)Math.Clamp(100.0 * Completed / Total, 0, 100) : 0;
}

public sealed class DamagedUpdateException() : IOException("The download was damaged.");

public static class UpdateDownload
{
    public static async Task<bool> VerifyAsync(string path, UpdateAsset asset, CancellationToken stop)
    {
        if (!File.Exists(path)) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        if (file.Length != asset.Size || !UpdateFiles.ValidHash(asset.Sha256)) return false;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, stop).ConfigureAwait(false));
        return hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="directory"/> and returns it once its size and
    /// hash match, which a wrong part can't get past. What arrived is kept as a part and resumed while tries
    /// bring something new: a slow link that keeps breaking would otherwise never finish.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateAsset asset, string directory, string currentVersion,
        IProgress<UpdateProgress>? progress, CancellationToken stop, Uri? testFeed = null, TimeSpan? stallTimeout = null,
        TimeSpan? retryDelay = null)
    {
        if (!UpdateFiles.IsAllowedUrl(asset.Url, testFeed) || !UpdateFiles.ValidHash(asset.Sha256) || asset.Size <= 0
            || asset.Name.Length == 0 || asset.Name is "." or ".." || asset.Name.Any(c => c < 32 || "/\\:<>\"|?*".Contains(c)))
            throw new InvalidDataException("Unusable update asset.");
        stop.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string final = Path.Combine(directory, asset.Name), part = final + ".part";
        if (await VerifyAsync(final, asset, stop).ConfigureAwait(false))
        {
            progress?.Report(new(asset.Size, asset.Size));
            return final;
        }
        File.Delete(final);
        var stall = stallTimeout ?? TimeSpan.FromSeconds(45);
        var wait = retryDelay ?? TimeSpan.FromSeconds(2);
        long most = Length(part);
        // FetchAsync throws away a part longer than the file, so no try could ever beat its length.
        if (most > asset.Size) most = 0;
        int idle = 0;
        bool startedOver = false;
        for (;;)
        {
            try
            {
                bool pieced = await FetchAsync(http, asset, part, currentVersion, progress, stall, stop).ConfigureAwait(false);
                if (await VerifyAsync(part, asset, stop).ConfigureAwait(false)) break;
                File.Delete(part);
                // A part left by an earlier try may not have been this file's: fetch it whole, once.
                if (!pieced || startedOver) throw new DamagedUpdateException();
                startedOver = true;
                most = 0;
                idle = 0;
            }
            catch (Exception e) when (CanRetry(e, stop))
            {
                long have = Length(part);
                if (have == 0) File.Delete(part);
                idle = have > most ? 0 : idle + 1;
                // The best so far, not the last: a server that ignores the range cuts the part back each try.
                most = Math.Max(most, have);
                if (idle >= 3) throw;
                await Task.Delay(wait * (1 << Math.Min(idle, 2)), stop).ConfigureAwait(false);
            }
        }
        stop.ThrowIfCancellationRequested();
        File.Move(part, final, true);
        return final;
    }

    /// <summary>Fetches what the part still lacks. True when the part holds bytes from an earlier try.</summary>
    static async Task<bool> FetchAsync(HttpClient http, UpdateAsset asset, string part, string currentVersion,
        IProgress<UpdateProgress>? progress, TimeSpan stall, CancellationToken stop)
    {
        long have = Length(part);
        if (have > asset.Size)
        {
            File.Delete(part);
            have = 0;
        }
        if (have == asset.Size) return true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(stall);
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.UserAgent.ParseAdd(UpdateCheck.UserAgent(currentVersion));
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The server's file is shorter than the part, so the part isn't this file's.
            File.Delete(part);
            throw new IOException("The part doesn't fit the file.");
        }
        response.EnsureSuccessStatusCode();
        // HttpClientHandler also refuses HTTPS-to-HTTP redirects on .NET.
        if (asset.Url.Scheme == "https" && response.RequestMessage?.RequestUri?.Scheme == "http") throw new InsecureRedirectException();
        // More of this very file from where the part ends (a server may send less than the rest), or all of it
        // (a server may ignore the range): nothing else will do. The part is kept; nothing says it is wrong.
        bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
        long done = resumed ? have : 0, end = asset.Size;
        if (resumed)
        {
            if (have == 0 || response.Content.Headers.ContentRange is not { Unit: "bytes", From: long from, To: long to, Length: long length }
                || from != have || length != asset.Size) throw new DamagedUpdateException();
            end = to + 1;
        }
        else if (response.StatusCode != HttpStatusCode.OK) throw new DamagedUpdateException();
        if (response.Content.Headers.ContentLength is { } announced && announced != end - done) throw new DamagedUpdateException();
        using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
        {
            byte[] buffer = new byte[131072];
            progress?.Report(new(done, asset.Size));
            while (done <= end)
            {
                timeout.CancelAfter(stall);
                int count = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                done += count;
                if (done > end) break;
                // The stall timer is for the network: a write it cut short could leave a hole in the part.
                await file.WriteAsync(buffer.AsMemory(0, count), stop).ConfigureAwait(false);
                progress?.Report(new(done, asset.Size));
            }
        }
        if (done > end)
        {
            // More than the file holds: it isn't the file, and neither is the part.
            File.Delete(part);
            throw new DamagedUpdateException();
        }
        // Cut short, or only some of the range: the next try carries on from here.
        if (done != asset.Size) throw new HttpIOException(HttpRequestError.ResponseEnded);
        return resumed;
    }

    /// <summary>A stall, a dropped connection or a busy server is worth another try; a wrong answer isn't.</summary>
    static bool CanRetry(Exception e, CancellationToken stop) => !stop.IsCancellationRequested && e switch
    {
        DamagedUpdateException or InsecureRedirectException => false,
        OperationCanceledException => true,
        HttpRequestException { StatusCode: { } status } =>
            (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        HttpRequestException or IOException => true,
        _ => false,
    };

    static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    sealed class InsecureRedirectException() : HttpRequestException("Insecure download redirect.");
}
