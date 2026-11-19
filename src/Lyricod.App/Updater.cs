using System.Diagnostics;
using System.Net;
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
    int _ticks;
    string? _readyPath;
    UpdateAsset? _readyAsset;
    /// <summary>The release whose patch couldn't be used: from then on, its whole download.</summary>
    Version? _patchRefused;
    bool _notWritable;
    static readonly Uri? TestFeed = Environment.GetEnvironmentVariable("LYRICOD_UPDATE_FEED") is { Length: > 0 } feed
        && Uri.TryCreate(feed, UriKind.Absolute, out var uri) ? uri : null;
    static readonly string BaseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    static readonly bool ParentLauncher = File.Exists(Path.Combine(BaseDirectory, "..", "Lyricod.exe"));
    public static InstallKind Kind { get; } = UpdateFiles.DetectKind(Path.GetFileName(BaseDirectory),
        File.Exists(Path.Combine(BaseDirectory, "unins000.exe")), ParentLauncher, File.Exists(Path.Combine(BaseDirectory, "..", "unins000.exe")));
    /// <summary>The folder with the launcher and the app folder, where the launcher can swap in a new version:
    /// a portable copy, or an installed one laid out the same way. An installed copy from before that has none.</summary>
    static string? LauncherRoot { get; } = UpdateFiles.HasLauncher(Path.GetFileName(BaseDirectory), ParentLauncher) ? Path.GetDirectoryName(BaseDirectory) : null;
    /// <summary>Only a portable copy stages its whole download (the zip) there; an installed one runs Setup.</summary>
    static string? PortableRoot => Kind == InstallKind.Portable ? LauncherRoot : null;
    /// <summary>An installed copy has Setup as another way to update when the launcher's doesn't work.</summary>
    static bool HasSetup => Kind == InstallKind.Installer;
    static string UpdatesDirectory => Path.Combine(Settings.DataDir, "Updates");
    UpdateAsset? Asset => Available is { } release ? UpdateFiles.SelectAsset(release, Kind, RuntimeInformation.ProcessArchitecture, TestFeed) : null;
    static string? Arch => UpdateFiles.ArchName(RuntimeInformation.ProcessArchitecture);
    /// <summary>The small download that takes this very version to the release, when it has one. A test build
    /// carries a release's version number but not its files, so it only tries a patch against a test feed.</summary>
    UpdateAsset? Patch => Available is { } release && release.Version != _patchRefused && (TestBuild == null || TestFeed != null)
        && UpdateCheck.ParseVersion(Version) is { } running
        ? UpdateFiles.SelectPatch(release, Kind, LauncherRoot != null, RuntimeInformation.ProcessArchitecture, running, TestFeed) : null;
    /// <summary>The hash of the release's whole zip, which a patch names as what it was made from.</summary>
    string? PatchBase => Available is { } release ? UpdateFiles.PatchBase(release, RuntimeInformation.ProcessArchitecture, TestFeed) : null;
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
        _readyAsset = null;
        _notWritable = false;
        Progress = null;
        Error = null;
        State = UpdateState.Idle;
    }

    /// <summary>After an installer update Setup is still running when the new Lyricod opens, so the
    /// start-up cleanup can't remove the downloaded setup yet: try once more a minute later, unless
    /// an update is downloading or waiting (its staged files must stay).</summary>
    async Task CleanUpAgainAsync()
    {
        if (State != UpdateState.Idle || _disposed || !await _files.WaitAsync(0)) return;
        try
        {
            var running = UpdateCheck.ParseVersion(Version) ?? new(0, 0, 0);
            // Started by an update: the old copy is the launcher's to remove.
            bool keepOldCopies = Platform.UpdateHealth.Pending;
            await Task.Run(() => PortableUpdate.Cleanup(LauncherRoot, UpdatesDirectory, running, keepOldCopies));
        }
        catch (Exception) { }
        finally { _files.Release(); }
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
        var patch = Patch;
        string? patchBase = PatchBase;
        using var stop = new CancellationTokenSource();
        _download = stop;
        State = UpdateState.Downloading;
        Error = null;
        Progress = new(0, (patch ?? asset).Size);
        Notify();
        bool locked = false;
        try
        {
            await _files.WaitAsync(stop.Token);
            locked = true;
            await _cleanup;
            var (path, got) = await Task.Run(async () =>
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
                string directory = Path.Combine(UpdatesDirectory, release.Version.ToString(3));
                if (patch != null && patchBase != null && LauncherRoot is { } folder && Arch is { } arch && UpdateCheck.ParseVersion(Version) is { } running)
                {
                    // A patch that can't be had or can't be used isn't a failure: the whole download doesn't
                    // depend on it. The connection failing is one, and would fail the whole download too.
                    try
                    {
                        string small = await UpdateDownload.DownloadAsync(http, patch, directory, Version, progress, stop.Token, TestFeed).ConfigureAwait(false);
                        try
                        {
                            await UpdatePatch.StageAsync(small, folder, running, release.Version, arch, patchBase, stop.Token).ConfigureAwait(false);
                            return (small, patch);
                        }
                        catch (Exception e) when (e is not OperationCanceledException) { RefusePatch(release, e); }
                    }
                    catch (Exception e) when (e is DamagedUpdateException or InvalidDataException || NotThere(e)) { RefusePatch(release, e); }
                }
                string file = await UpdateDownload.DownloadAsync(http, asset, directory, Version, progress, stop.Token, TestFeed).ConfigureAwait(false);
                if (PortableRoot is { } portable)
                {
                    try { await PortableUpdate.StageAsync(file, portable, release.Version, stop.Token).ConfigureAwait(false); }
                    catch (UnauthorizedAccessException) { throw new UpdateFolderException(); }
                }
                return (file, asset);
            }, stop.Token);
            if (_disposed || _download != stop) return;
            stop.Token.ThrowIfCancellationRequested();
            _readyPath = path;
            _readyAsset = got;
            Progress = new(got.Size, got.Size);
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
        if (_readyPath is not { } path || _readyAsset is not { } ready || Available is not { } release || Asset is not { } asset) return;
        bool patched = ready.Name != asset.Name;
        string? patchBase = PatchBase;
        State = UpdateState.Applying;
        Error = null;
        Notify();
        try
        {
            ProcessStartInfo start;
            try
            {
                start = await Task.Run(async () =>
                {
                    if (!await UpdateDownload.VerifyAsync(path, ready, CancellationToken.None).ConfigureAwait(false)) throw new DamagedUpdateException();
                    // An installed copy's whole download is Setup, which does the rest itself.
                    if (!patched && PortableRoot == null) return new ProcessStartInfo(path)
                    {
                        UseShellExecute = false,
                        Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /relaunch=1",
                    };
                    if (LauncherRoot is not { } root) throw new InvalidDataException("No launcher for this copy.");
                    // Restage from the checked download in case the staged files changed while waiting.
                    if (!patched) await PortableUpdate.StageAsync(path, root, release.Version, CancellationToken.None).ConfigureAwait(false);
                    else if (patchBase != null && Arch is { } arch && UpdateCheck.ParseVersion(Version) is { } running)
                        await UpdatePatch.StageAsync(path, root, running, release.Version, arch, patchBase, CancellationToken.None).ConfigureAwait(false);
                    else throw new InvalidDataException("No patch for this copy.");
                    var launcher = new ProcessStartInfo(Path.Combine(root, "Lyricod.exe.new")) { WorkingDirectory = root, UseShellExecute = false };
                    launcher.ArgumentList.Add("--apply-update");
                    launcher.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return launcher;
                });
            }
            catch (Exception e) when (patched)
            {
                // The patch was fine when it was downloaded; this copy has changed since. The whole download
                // doesn't depend on it: fetch that, and the restart is offered again.
                await UseWholeDownloadAsync(release, e);
                return;
            }
            // Setup doesn't use the launcher, so it can work where the launcher's swap didn't: note the try,
            // and a start that finds the note with the old version still running leaves the patch be.
            bool noted = patched && HasSetup;
            try
            {
                await Task.Run(() =>
                {
                    if (noted) UpdateAttempt.Write(UpdatesDirectory, release.Version);
                    using var helper = Process.Start(start) ?? throw new IOException("Could not start the update.");
                });
            }
            catch (Exception e) when (noted)
            {
                UpdateAttempt.Clear(UpdatesDirectory);
                await UseWholeDownloadAsync(release, e);
                return;
            }
            ((App)Microsoft.UI.Xaml.Application.Current).QuitForUpdate();
        }
        catch (Exception e) { Fail(e is DamagedUpdateException ? "UpdateDamaged" : "UpdateApplyFailed"); }
    }

    async Task UseWholeDownloadAsync(AppRelease release, Exception e)
    {
        RefusePatch(release, e);
        _readyPath = null;
        _readyAsset = null;
        State = UpdateState.Idle;
        await DownloadAsync();
    }

    void RefusePatch(AppRelease release, Exception e)
    {
        Log.Write($"Update: the patch to {release.Version.ToString(3)} wasn't used. {e.Message}");
        _patchRefused = release.Version;
    }

    /// <summary>The server has no such file (any more), as opposed to being busy or out of reach.</summary>
    static bool NotThere(Exception e) => e is HttpRequestException { StatusCode: { } status }
        && (int)status is >= 400 and < 500 && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

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
