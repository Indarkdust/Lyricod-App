using Lyricod.Core;
using Lyricod.Core.Romanization;
using Lyricod.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinUIEx;

namespace Lyricod;

/// <summary>
/// A tray app, like the Mac's menu-bar app: no main window. The tray icon's panel shows and hides
/// the views and opens the lyrics window and Settings.
/// </summary>
public partial class App : Application
{
    UpdateChecker? _updates;
    Settings? _settings;
    PlaybackModel? _model;
    DesktopLyricsWindow? _desktop;
    IslandWindow? _island;
    LyricsWindow? _lyricsWindow;
    SettingsWindow? _settingsWindow;
    ChooseLyricsWindow? _chooseLyricsWindow;
    TrayIcon? _tray;
    TrayPanel? _panel;
    GlobalShortcuts? _shortcuts;
    bool _quitting;

    public App()
    {
        // Whatever ends Lyricod unexpectedly is logged first; this changes nothing about what happens next.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Write("unobserved task: " + e.Exception);
        UnhandledException += (_, e) => Log.Write("unhandled (XAML): " + e.Exception);
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { await LaunchAsync(); }
        catch (Exception e)
        {
            Log.Write(e);
            Quit();
        }
    }

    async Task LaunchAsync()
    {
        if (!AppInstance.FindOrRegisterForKey("Lyricod").IsCurrent)
        {
            Platform.UpdateHealth.SignalStarted(); // it runs: another Lyricod is just already open
            Exit();
            return;
        }
        // Closing a window mustn't quit a tray app.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // The log's errors read against this: which Lyricod, on which Windows.
        Log.Write($"Lyricod {UpdateChecker.Version}{(UpdateChecker.TestBuild is { } build ? $" (test build {build})" : "")} started on Windows {Environment.OSVersion.Version}, "
            + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
        _settings = Settings.Load();
        // A first run gets Lyricod Glass where its liquid effect can run (Windows 11); elsewhere the classic island.
        if (_settings.IsNew && LiquidGlass.IsSupported) _settings.IslandTheme = IslandTheme.Glass;
        // The language chosen in Settings, read once: like the Mac, a change applies after a restart.
        L.Start(_settings.Language);
        Fonts.Start(_settings);
        Fonts.ApplyToApp(Resources); // before any window opens
        _updates = new UpdateChecker(_settings, DispatcherQueue.GetForCurrentThread());
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var provider = new LyricsProvider(Path.Combine(Settings.DataDir, "lyrics"), version);
        var overrides = new TrackOverrides(Path.Combine(Settings.DataDir, "song-overrides.json"));
        _model = new PlaybackModel(DispatcherQueue.GetForCurrentThread(), new MediaSession(), provider,
            new Romanizer(JapaneseReader.IsAvailable ? new JapaneseReader() : null), overrides, _settings);
        PlaybackModel.GlassStatus = () => _island?.LiquidCaptureStatus ?? "inactive";
        _desktop = new DesktopLyricsWindow(_model, _settings);
        _shortcuts = new GlobalShortcuts(_desktop, _settings, PerformShortcut);
        _island = new IslandWindow(_model, _settings, OpenLyricsWindow, () => OpenSettings());

        _tray = new TrayIcon(1, Path.Combine(AppContext.BaseDirectory, "Assets", "Lyricod.ico"), "Lyricod");
        // Either click opens the panel, as the Mac's menu-bar icon does.
        _panel = new TrayPanel(_model, _settings, OpenLyricsWindow, () => OpenSettings(), OpenChooseLyrics, Quit, _updates)
        {
            OpenNeteaseSync = OpenNeteaseSync,
        };
        _tray.ContextMenu += (_, _) => _panel.Toggle();
        _tray.Selected += (_, _) => _panel.Toggle();
        _tray.IsVisible = true;
        _model.Changed += change =>
        {
            if (change.HasFlag(PlaybackModel.Change.Track) && _tray != null)
                _tray.Tooltip = _model.Track is { } t ? Trim($"Lyricod · {t.Title} — {t.Artist}", 127) : "Lyricod";
        };

        await _model.StartAsync();
        // After a portable update: everything is up, so the launcher can let the old copy go. If
        // anything above threw, this is never reached and the launcher puts the old copy back.
        Platform.UpdateHealth.SignalStarted();
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    void OpenLyricsWindow()
    {
        if (_lyricsWindow == null)
        {
            _lyricsWindow = new LyricsWindow(_model!, _settings!, OpenChooseLyrics);
            _lyricsWindow.Closed += (_, _) => _lyricsWindow = null;
        }
        _lyricsWindow.Activate();
    }

    void OpenChooseLyrics()
    {
        if (_chooseLyricsWindow == null)
        {
            _chooseLyricsWindow = new ChooseLyricsWindow(_model!);
            _chooseLyricsWindow.Closed += (_, _) => _chooseLyricsWindow = null;
        }
        _chooseLyricsWindow.Activate();
    }

    void PerformShortcut(ShortcutAction action)
    {
        var model = _model!;
        var settings = _settings!;
        switch (action)
        {
            case ShortcutAction.PlayPause: _ = model.PlayPause(); break;
            case ShortcutAction.Next: _ = model.Next(); break;
            case ShortcutAction.Previous: _ = model.Previous(); break;
            case ShortcutAction.ToggleDesktop: settings.ShowDesktopLyrics = !settings.ShowDesktopLyrics; break;
            case ShortcutAction.ToggleIsland: settings.ShowIsland = !settings.ShowIsland; break;
            case ShortcutAction.ToggleLyricsWindow:
                if (_lyricsWindow == null) OpenLyricsWindow(); else _lyricsWindow.Close();
                break;
            case ShortcutAction.LyricsEarlier: model.SetSongOffset(model.SongOffset + 0.5); break;
            case ShortcutAction.LyricsLater: model.SetSongOffset(model.SongOffset - 0.5); break;
        }
    }

    void OpenSettings(SettingsPane? pane = null)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings!, _model!, OpenLyricsWindow, OpenChooseLyrics, _shortcuts!, _updates!, pane ?? SettingsPane.General)
            {
                Restart = Restart,
                ShowIslandInScreenshots = () => _island?.ShowInScreenshots(),
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        else if (pane is { } p) _settingsWindow.Show(p);
        _settingsWindow.Activate();
    }

    /// <summary>The tray's NetEase tip: Settings › General › NetEase Cloud Music sync where it can be set up, else the guide.</summary>
    void OpenNeteaseSync()
    {
        if (Platform.NeteaseSetup.Find() is { Is64Bit: true })
        {
            OpenSettings(SettingsPane.General);
            _settingsWindow?.ShowNeteaseSync();
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(L.Get("NeteaseGuideUrl")) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { }
    }

    /// <summary>Starts a new Lyricod and quits this one (to change language).</summary>
    void Restart() => Quit(restart: true);

    /// <summary>Quits once the click that asked for it has finished, since its window closes on the way out.</summary>
    void Quit() => Quit(restart: false);

    public void QuitForUpdate() => Quit(restart: false);

    void Quit(bool restart)
    {
        if (_quitting) return;
        _quitting = true;
        void Finish()
        {
            try { ShutDown(restart); }
            catch (Exception) { Exit(); }
        }
        try { if (!DispatcherQueue.GetForCurrentThread().TryEnqueue(Finish)) Finish(); }
        catch (Exception) { Finish(); }
    }

    /// <summary>
    /// Stops everything that calls back on its own (player reports, timers, audio meter, shortcuts,
    /// tray icon), closes the windows, then exits. A callback left running against a closed window
    /// that throws crashes the process (0xC000027B in CoreMessagingXP).
    /// </summary>
    void ShutDown(bool restart)
    {
        _model?.Stop();
        _updates?.Dispose();
        _shortcuts?.Dispose();
        if (_tray != null)
        {
            _tray.Dispose();
            _tray = null;
        }
        // Each window stops its own timers as it closes.
        _chooseLyricsWindow?.Close();
        _settingsWindow?.Close();
        _lyricsWindow?.Close();
        _panel?.Close();
        _island?.Close();
        _desktop?.Close();
        if (restart) StartAgain();
        Exit();
    }

    /// <summary>A new Lyricod, started once this one has let go of its shortcuts and tray icon.</summary>
    static void StartAgain()
    {
        // Let the new one register as the single instance.
        AppInstance.GetCurrent().UnregisterKey();
        try
        {
            if (Environment.ProcessPath is { } path)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception) { } // it couldn't start: this one still quits, as it would have
    }
}
