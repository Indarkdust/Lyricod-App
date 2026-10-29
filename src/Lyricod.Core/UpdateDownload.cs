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

    public static async Task<string> DownloadAsync(HttpClient http, UpdateAsset asset, string directory, string currentVersion,
        IProgress<UpdateProgress>? progress, CancellationToken stop, Uri? testFeed = null, TimeSpan? stallTimeout = null)
    {
        if (!UpdateFiles.IsAllowedUrl(asset.Url, testFeed) || !UpdateFiles.ValidHash(asset.Sha256) || asset.Size <= 0
            || asset.Name.Length == 0 || asset.Name is "." or ".." || asset.Name.Any(c => c < 32 || "/\\:<>\"|?*".Contains(c)))
            throw new InvalidDataException("Unusable update asset.");
        stop.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string final = Path.Combine(directory, asset.Name), part = final + ".part";
        try
        {
            if (await VerifyAsync(final, asset, stop).ConfigureAwait(false))
            {
                progress?.Report(new(asset.Size, asset.Size));
                return final;
            }
            File.Delete(final);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var stall = stallTimeout ?? TimeSpan.FromSeconds(45);
            timeout.CancelAfter(stall);
            using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            request.Headers.UserAgent.ParseAdd(UpdateCheck.UserAgent(currentVersion));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size) throw new DamagedUpdateException();
            // HttpClientHandler also refuses HTTPS-to-HTTP redirects on .NET.
            if (asset.Url.Scheme == "https" && response.RequestMessage?.RequestUri?.Scheme == "http")
                throw new HttpRequestException("Insecure download redirect.");
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[131072];
                long done = 0;
                progress?.Report(new(0, asset.Size));
                while (true)
                {
                    timeout.CancelAfter(stall);
                    int count = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    done += count;
                    if (done > asset.Size) throw new DamagedUpdateException();
                    await file.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    progress?.Report(new(done, asset.Size));
                }
                if (done != asset.Size) throw new DamagedUpdateException();
            }
            if (!await VerifyAsync(part, asset, stop).ConfigureAwait(false)) throw new DamagedUpdateException();
            stop.ThrowIfCancellationRequested();
            File.Move(part, final, true);
            return final;
        }
        catch (HttpIOException e) when (e.HttpRequestError == HttpRequestError.ResponseEnded) { throw new DamagedUpdateException(); }
        finally { File.Delete(part); }
    }
}
