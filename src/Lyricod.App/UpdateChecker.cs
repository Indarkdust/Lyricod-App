using System.Reflection;
using Lyricod.Core;
using Microsoft.UI.Dispatching;

namespace Lyricod;

/// <summary>All state and notifications live on the UI thread.</summary>
public sealed class UpdateChecker : IDisposable
{
    readonly Settings _settings;
    readonly DispatcherQueueTimer _timer;
    readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(15) };
    bool _disposed;
    public static string Version => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev";
    /// <summary>"57-abc1234" (CI run and commit) on a test build; null on a release or a local build.
    /// A test build keeps the last released version, so the next release still counts as newer.</summary>
    public static string? TestBuild { get; } = typeof(App).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "LyricodTestBuild")?.Value is { Length: > 0 } build ? build : null;
    /// <summary>Debug: LYRICOD_UPDATE_FEED=url reads the release list from a copy of it instead of GitHub,
    /// to try an update before the releases can be seen.</summary>
    static readonly string Feed = Environment.GetEnvironmentVariable("LYRICOD_UPDATE_FEED") is { Length: > 0 } feed ? feed : UpdateCheck.Endpoint;
    public static string DisplayVersion => TestBuild is { } build ? L.Format("TestBuildVersion", Version, build) : Version;
    public bool Checking { get; private set; }
    public UpdateResult? Result { get; private set; }
    public AppRelease? Available { get; private set; }
    public event Action? Changed;

    public UpdateChecker(Settings settings, DispatcherQueue queue)
    {
        _settings = settings;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(10);
        _timer.Tick += async (_, _) =>
        {
            // An exception escaping a timer callback ends the app (0xC000027B).
            try
            {
                _timer.Interval = TimeSpan.FromMinutes(1);
                if (_settings.CheckForUpdatesAutomatically && UpdateCheck.IsDue(_settings.LastUpdateCheck, DateTimeOffset.UtcNow))
                    await CheckAsync(manual: false);
            }
            catch (Exception) { }
        };
        _timer.Start();
    }

    public async Task CheckAsync(bool manual = true)
    {
        if (Checking || _disposed) return;
        Checking = true;
        _settings.LastUpdateCheck = DateTimeOffset.UtcNow;
        UpdateResult outcome;
        try
        {
            Notify();
            outcome = await UpdateCheck.FetchAsync(_client, Version, Feed);
        }
        catch (Exception) { outcome = new(UpdateStatus.CouldntCheck); } // never stuck on "Checking"
        finally { Checking = false; }
        if (_disposed) return;
        if (manual || outcome.Status != UpdateStatus.CouldntCheck) Result = outcome;
        if (outcome.Status == UpdateStatus.Available) Available = outcome.Release;
        else if (outcome.Status == UpdateStatus.UpToDate) Available = null;
        Notify();
    }

    /// <summary>A listener's exception mustn't reach the timer or click that started the check.</summary>
    void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _client.Dispose();
    }
}
