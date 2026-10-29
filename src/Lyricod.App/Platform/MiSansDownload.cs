using Lyricod.Core.Fonts;

namespace Lyricod.Platform;

static class MiSansDownload
{
    public static string DirectoryPath => Path.Combine(Settings.DataDir, "Fonts");
    public static string FontPath => Path.Combine(DirectoryPath, "MiSansVF.ttf");
    public static string LicencePath => Path.Combine(DirectoryPath, "MiSans-License.pdf");
    public static string AppCopyPath => Path.Combine(AppContext.BaseDirectory, "Fonts", "MiSansVF.ttf");
    public static bool IsDownloaded => File.Exists(FontPath) && File.Exists(LicencePath);

    public static Task<string> DownloadAsync(IProgress<FontDownloadProgress> progress, CancellationToken stop) => Task.Run(async () =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev";
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Lyricod/{version} (+https://github.com/Indarkdust/Lyricod-App)");
        return await MiSansArchive.DownloadAsync(http, DirectoryPath, progress, timeout.Token).ConfigureAwait(false);
    }, stop);

    public static void Remove()
    {
        // Remove the app copy first: a locked file must be reported, not forgotten.
        foreach (string path in new[] { AppCopyPath, FontPath, LicencePath })
            if (File.Exists(path)) File.Delete(path);
    }
}
