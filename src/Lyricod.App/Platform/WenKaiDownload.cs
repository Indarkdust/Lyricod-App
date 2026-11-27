using Lyricod.Core;
using Lyricod.Core.Fonts;

namespace Lyricod.Platform;

static class WenKaiDownload
{
    public static string FontPath => Path.Combine(MiSansDownload.DirectoryPath, WenKaiFont.File.FileName);
    public static string PartPath => FontPath + ".part";
    public static string LicencePath => Path.Combine(MiSansDownload.DirectoryPath, WenKaiFont.LicenceFileName);
    public static string AppCopyPath => Path.Combine(AppContext.BaseDirectory, "Fonts", WenKaiFont.File.FileName);
    static string ShippedLicence => Path.Combine(AppContext.BaseDirectory, "licenses", WenKaiFont.LicenceFileName);
    public static bool IsDownloaded => File.Exists(FontPath);

    public static Task DownloadAsync(IProgress<FontDownloadProgress> progress, CancellationToken stop) => Task.Run(async () =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UpdateCheck.UserAgent(UpdateChecker.Version));
        await FontFileDownload.DownloadAsync(http, WenKaiFont.File, MiSansDownload.DirectoryPath, progress, timeout.Token).ConfigureAwait(false);
        // The OFL asks for the licence to go with each copy of the font.
        File.Copy(ShippedLicence, LicencePath, true);
    }, stop);

    public static void Remove()
    {
        // Remove the app copy first: a locked file must be reported, not forgotten.
        foreach (string path in new[] { AppCopyPath, FontPath, PartPath, LicencePath })
            if (File.Exists(path)) File.Delete(path);
    }
}
