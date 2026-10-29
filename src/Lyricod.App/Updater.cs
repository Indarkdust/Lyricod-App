using System.Diagnostics;
using System.Runtime.InteropServices;
using Lyricod.Core;
using Microsoft.UI.Dispatching;

namespace Lyricod;

public enum UpdateState { Idle, Downloading, Ready, Applying, Error }

public sealed partial class UpdateChecker
{
    readonly DispatcherQueue _queue;
    readonly Task _cleanup;
    readonly SemaphoreSlim _files = new(1, 1);
    CancellationTokenSource? _download;
    string? _readyPath;
    bool _notWritable;
    static readonly Uri? TestFeed = Environment.GetEnvironmentVariable("LYRICOD_UPDATE_FEED") is { Length: > 0 } feed
        && Uri.TryCreate(feed, UriKind.Absolute, out var uri) ? uri : null;
    static readonly string BaseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    public static InstallKind Kind { get; } = UpdateFiles.DetectKind(Path.GetFileName(BaseDirectory),
        File.Exists(Path.Combine(BaseDirectory, "unins000.exe")), File.Exists(Path.Combine(BaseDirectory, "..", "Lyricod.exe")));
    static string? PortableRoot => Kind == InstallKind.Portable ? Path.GetDirectoryName(BaseDirectory) : null;
    static string UpdatesDirectory => Path.Combine(Settings.DataDir, "Updates");
    UpdateAsset? Asset => Available is { } release ? UpdateFiles.SelectAsset(release, Kind, RuntimeInformation.ProcessArchitecture, TestFeed) : null;
    public bool CanInstall => !_notWritable && Asset != null;
    public UpdateState State { get; private set; }
    public UpdateProgress? Progress { get; private set; }
    public string? Error { get; private set; }

    void SetRelease(AppRelease? release)
    {
        if (Available?.Version == release?.Version) return;
        Cancel();
        _download = null;
        Available = release;
        _readyPath = null;
        _notWritable = false;
        Progress = null;
        Error = null;
        State = UpdateState.Idle;
    }

    public void Cancel()
    {
        try { _download?.Cancel(); }
        catch (Exception) { }
    }

    public async Task ActivateAsync()
    {
        try
        {
            if (_disposed || State is UpdateState.Downloading or UpdateState.Applying) return;
            if (!CanInstall) { OpenReleasePage(); return; }
            if (State == UpdateState.Ready) await ApplyAsync();
            else await DownloadAsync();
        }
        catch (Exception) { Fail("UpdateFailed"); }
    }

    public void OpenReleasePage()
    {
        try
        {
            if (Available is { } release) Process.Start(new ProcessStartInfo(release.Url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception) { Fail("UpdateOpenPageFailed"); }
    }

    async Task DownloadAsync()
    {
        if (Available is not { } release || Asset is not { } asset) return;
        using var stop = new CancellationTokenSource();
        _download = stop;
        State = UpdateState.Downloading;
        Error = null;
        Progress = new(0, asset.Size);
        Notify();
        bool locked = false;
        try
        {
            await _files.WaitAsync(stop.Token);
            locked = true;
            await _cleanup;
            string path = await Task.Run(async () =>
            {
                if (PortableRoot is { } root)
                {
                    try { PortableUpdate.CheckWritable(root); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new UpdateFolderException(); }
                }
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
                var progress = new DownloadProgress(p => Ui(() =>
                {
                    if (_download != stop || State != UpdateState.Downloading) return;
                    // A percentage change is enough to refresh both views.
                    if (Progress?.Percent == p.Percent) return;
                    Progress = p;
                    Notify();
                }));
                string file = await UpdateDownload.DownloadAsync(http, asset, Path.Combine(UpdatesDirectory, release.Version.ToString(3)),
                    Version, progress, stop.Token, TestFeed).ConfigureAwait(false);
                if (PortableRoot is { } portable)
                {
                    try { await PortableUpdate.StageAsync(file, portable, release.Version, stop.Token).ConfigureAwait(false); }
                    catch (UnauthorizedAccessException) { throw new UpdateFolderException(); }
                }
                return file;
            }, stop.Token);
            if (_disposed || _download != stop) return;
            stop.Token.ThrowIfCancellationRequested();
            _readyPath = path;
            Progress = new(asset.Size, asset.Size);
            State = UpdateState.Ready;
        }
        catch (OperationCanceledException)
        {
            if (_download == stop && !_disposed)
            {
                State = stop.IsCancellationRequested ? UpdateState.Idle : UpdateState.Error;
                Error = stop.IsCancellationRequested ? null : L.Get("UpdateTimeout");
            }
        }
        catch (Exception e)
        {
            if (_download == stop && !_disposed)
            {
                _notWritable = e is UpdateFolderException;
                Fail(e is DamagedUpdateException ? "UpdateDamaged" : _notWritable ? "UpdateNotWritable" : "UpdateFailed");
            }
        }
        finally
        {
            if (locked) _files.Release();
            if (_download == stop) { _download = null; Notify(); }
        }
    }

    async Task ApplyAsync()
    {
        if (_readyPath is not { } path || Available is not { } release || Asset is not { } asset) return;
        State = UpdateState.Applying;
        Error = null;
        Notify();
        try
        {
            await Task.Run(async () =>
            {
                if (!await UpdateDownload.VerifyAsync(path, asset, CancellationToken.None).ConfigureAwait(false)) throw new DamagedUpdateException();
                ProcessStartInfo start;
                if (PortableRoot is { } root)
                {
                    // Restage from the checked ZIP in case the staged files changed while waiting.
                    await PortableUpdate.StageAsync(path, root, release.Version, CancellationToken.None).ConfigureAwait(false);
                    start = new(Path.Combine(root, "Lyricod.exe.new")) { WorkingDirectory = root, UseShellExecute = false };
                    start.ArgumentList.Add("--apply-update");
                    start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else start = new(path)
                {
                    UseShellExecute = false,
                    Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /relaunch=1",
                };
                using var helper = Process.Start(start) ?? throw new IOException("Could not start the update.");
            });
            ((App)Microsoft.UI.Xaml.Application.Current).QuitForUpdate();
        }
        catch (Exception e) { Fail(e is DamagedUpdateException ? "UpdateDamaged" : "UpdateApplyFailed"); }
    }

    void Fail(string key)
    {
        if (_disposed) return;
        Error = L.Get(key);
        State = UpdateState.Error;
        Notify();
    }

    void Ui(Action action)
    {
        try
        {
            _queue.TryEnqueue(() =>
            {
                try { if (!_disposed) action(); }
                catch (Exception) { }
            });
        }
        catch (Exception) { }
    }

    sealed class DownloadProgress(Action<UpdateProgress> report) : IProgress<UpdateProgress>
    {
        public void Report(UpdateProgress value) => report(value);
    }

    sealed class UpdateFolderException : IOException { }
}
