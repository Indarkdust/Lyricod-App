using System.ComponentModel;
using System.Globalization;
using Lyricod.Core;
using Lyricod.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using WinUIEx;

namespace Lyricod;

/// <summary>
/// The tray icon's panel, like the macOS app's menu-bar panel: now playing with its controls, the
/// desktop lyrics and island tiles, this song's lyrics (to choose others) and timing, and the
/// lyrics window, Settings and Quit. It sits above the notification area like Windows' own
/// flyouts and goes away when it loses focus.
/// </summary>
public sealed partial class TrayPanel : Window
{
    const double PanelWidth = 336;

    readonly UpdateChecker _updates;
    readonly PlaybackModel _model;
    readonly Settings _settings;
    readonly Action _openLyricsWindow, _openSettings, _chooseLyrics, _quit;
    readonly TileLock _desktopLock, _islandLock;

    /// <summary>What the NetEase tip opens: NetEase Cloud Music sync's settings; without it, the guide in the README.</summary>
    public Action? OpenNeteaseSync { get; set; }
    readonly TransportButtons _transport;
    readonly ProgressLine _progress;
    readonly DispatcherQueueTimer _progressTimer;
    bool _shown;
    /// <summary>When it last closed: a click on the tray icon that closed it mustn't reopen it.</summary>
    double _hiddenAt;

    public TrayPanel(PlaybackModel model, Settings settings, Action openLyricsWindow, Action openSettings, Action chooseLyrics, Action quit, UpdateChecker updates)
    {
        InitializeComponent();
        _updates = updates;
        _updates.Changed += OnUpdatesChanged;
        UpdateLink.Click += async (_, _) =>
        {
            try { await _updates.ActivateAsync(); }
            catch (Exception) { }
        };
        _model = model;
        _settings = settings;
        _openLyricsWindow = openLyricsWindow;
        _openSettings = openSettings;
        _chooseLyrics = chooseLyrics;
        NeteaseTip.Click += (_, _) =>
        {
            Hide();
            if (OpenNeteaseSync is { } open) open();
            else
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(L.Get("NeteaseGuideUrl")) { UseShellExecute = true })?.Dispose(); }
                catch (Exception) { }
            }
        };
        _quit = quit;

        // A popup: a thin border and Windows 11's rounded corners, but no title bar, and out of
        // the taskbar and Alt-Tab.
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(true, false);
        AppWindow.IsShownInSwitchers = false;

        _transport = new TransportButtons(model, 20) { Spacing = 24 };
        TransportHost.Children.Add(_transport);
        _progress = new ProgressLine(model);
        ProgressHost.Children.Add(_progress);
        DesktopTileText.Text = L.Get("DesktopLyrics");
        IslandTileText.Text = L.Get("PaneIsland");
        _desktopLock = new TileLock(DesktopTileHost, DesktopTile, L.Get("LockDesktopLyrics"), on => _settings.DesktopLocked = on);
        _islandLock = new TileLock(IslandTileHost, IslandTile, L.Get("LockIsland"), on => _settings.IslandLocked = on);
        LyricsTitle.Text = L.Get("PaneLyrics");
        TimingTitle.Text = L.Get("TimingHeading");
        ToolTipService.SetToolTip(LyricsRow, L.Get("ChooseLyricsTip"));
        ToolTipService.SetToolTip(EarlierButton, L.Get("OffsetEarlier"));
        ToolTipService.SetToolTip(LaterButton, L.Get("OffsetLater"));
        ToolTipService.SetToolTip(LyricsWindowButton, L.Get("LyricsWindowTitle"));
        ToolTipService.SetToolTip(SettingsButton, L.Get("SettingsTip"));
        ToolTipService.SetToolTip(QuitButton, L.Get("Quit"));

        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromSeconds(0.5);
        _progressTimer.Tick += (_, _) => { try { _progress.Update(); } catch (Exception) { } };

        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) Hide(); };
        Root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) Hide(); };
        _model.Changed += OnModelChanged;
        _settings.PropertyChanged += OnSettingChanged;
        Closed += (_, _) =>
        {
            _updates.Changed -= OnUpdatesChanged;
            _model.Changed -= OnModelChanged;
            _settings.PropertyChanged -= OnSettingChanged;
            _progressTimer.Stop();
        };
    }

    void OnUpdatesChanged()
    {
        if (!_shown) return;
        RefreshUpdate();
        SizeToContent();
    }

    void OnModelChanged(PlaybackModel.Change change) { if (_shown) Update(); }
    void OnSettingChanged(object? sender, PropertyChangedEventArgs e) { if (_shown) Update(); }

    /// <summary>Opens above the notification area, or closes if it's open (a second click on the icon).</summary>
    public void Toggle()
    {
        if (_shown || PlaybackModel.Now - _hiddenAt < 0.3)
        {
            Hide();
            return;
        }
        Update();
        SizeToContent();
        _shown = true;
        Activate();
        // Opened from the keyboard (Win+B), Windows can leave focus in the notification area;
        // the panel takes it, like its own flyouts, so the keys work in it and it closes on leaving.
        this.SetForegroundWindow();
        if (_model.IsPlaying) _progressTimer.Start();
    }

    void SizeToContent()
    {
        // Sized to its content, in the corner above the clock, like Windows' quick settings.
        Panel.Measure(new Size(PanelWidth, double.PositiveInfinity));
        double height = Panel.DesiredSize.Height > 0 ? Panel.DesiredSize.Height : 440;
        var hwnd = this.GetWindowHandle();
        double k = Win32.Scale(hwnd);
        var area = DisplayArea.Primary.WorkArea;
        int gap = (int)(12 * k), w = (int)Math.Ceiling(PanelWidth * k), h = (int)Math.Ceiling(height * k);
        // The panel only gets the window's inside: Windows keeps invisible edges round it for the
        // shadow (left, right and bottom; none on top, as the title bar is hidden), and sizing the
        // whole window to the panel cut off its right side. So the inside is sized to the panel and
        // put in the corner, with the edges measured rather than assumed (AppWindow.ResizeClient
        // still counts the hidden title bar), and checked once.
        for (int pass = 0; pass < 2; pass++)
        {
            var (left, top, right, bottom) = Win32.FrameEdges(hwnd);
            AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - gap - w - left, area.Y + area.Height - gap - h - top,
                                                  w + left + right, h + top + bottom));
            if (Win32.ClientSize(hwnd) == (w, h)) break;
        }
    }

    public void Hide()
    {
        if (!_shown) return;
        _shown = false;
        _hiddenAt = PlaybackModel.Now;
        _progressTimer.Stop();
        AppWindow.Hide();
    }

    void RefreshUpdate()
    {
        UpdateLink.Visibility = _updates.Available == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateLink.Content = _updates.Available is { } release ? _updates.State switch
        {
            UpdateState.Downloading => L.Format("UpdateDownloading", _updates.Progress?.Percent ?? 0),
            UpdateState.Ready => L.Format("UpdateRestartVersion", release.Version.ToString(3)),
            UpdateState.Applying => L.Get("UpdateApplying"),
            _ => L.Format("UpdateAvailable", release.Version.ToString(3)),
        } : "";
        UpdateLink.IsEnabled = _updates.State is not (UpdateState.Downloading or UpdateState.Applying);
    }

    async void Update()
    {
        try
        {
            RefreshUpdate();
            var track = _model.Track;
            SongTitle.Text = track?.Title ?? L.Get("NothingPlaying");
            SongArtist.Text = track?.Artist ?? L.Get("PlaySomething");
            ProgressHost.Visibility = TransportHost.Visibility = track != null ? Visibility.Visible : Visibility.Collapsed;
            _transport.Update();
            _progress.Update();
            if (_model.IsPlaying && _shown) _progressTimer.Start(); else _progressTimer.Stop();

            DesktopTile.IsChecked = _settings.ShowDesktopLyrics;
            IslandTile.IsChecked = _settings.ShowIsland;
            // Like Settings: locking needs the view showing, and only a floating island can move.
            _desktopLock.Sync(_settings.DesktopLocked, _settings.ShowDesktopLyrics, L.Get("UnlockDesktopLyrics"), L.Get("LockDesktopLyrics"));
            _islandLock.Sync(_settings.IslandLocked, _settings.ShowIsland, L.Get("UnlockIsland"), L.Get("LockIsland"));
            _islandLock.Toggle.Visibility = _settings.IslandStyle == IslandStyle.Floating ? Visibility.Visible : Visibility.Collapsed;

            ThisSong.Opacity = track != null ? 1 : 0.5;
            LyricsRow.IsEnabled = EarlierButton.IsEnabled = LaterButton.IsEnabled = track != null;
            var status = LyricsStatusText();
            bool statusChanged = LyricsStatus.Text != status;
            LyricsStatus.Text = status;
            LyricsStatus.TextWrapping = _model.IsNotSong ? TextWrapping.Wrap : TextWrapping.NoWrap;
            LyricsStatus.TextTrimming = _model.IsNotSong ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            // NetEase on its own doesn't say where it is in the song, so the lyrics can drift.
            bool drifting = track != null && _model.NowPlaying is { HasTimeline: false } now && MusicPlayers.IsNetease(now.AppId);
            var tip = drifting ? Visibility.Visible : Visibility.Collapsed;
            if (NeteaseTip.Visibility != tip)
            {
                NeteaseTip.Content = L.Get("NeteaseTip");
                NeteaseTip.Visibility = tip;
                statusChanged = true;
            }
            if (_shown && statusChanged) SizeToContent();
            LyricsIcon.Glyph = _model.LyricsPinned ? "\uE718" : "\uE8BD"; // pin, or a speech bubble
            double offset = _model.SongOffset;
            string amount = Math.Abs(offset).ToString("0.00", CultureInfo.CurrentCulture);
            // A positive offset moves the lyrics' clock ahead, so they show earlier.
            TimingStatus.Text = offset > 0 ? L.Format("LyricsEarlierBy", amount)
                : offset < 0 ? L.Format("LyricsLaterBy", amount) : L.Get("MatchesTheSong");

            var art = await Images.FromBytes(_model.Artwork, 112);
            if (ReferenceEquals(track, _model.Track)) Art.Source = art;
        }
        catch (Exception) { }
    }

    string LyricsStatusText()
    {
        if (_model.Track == null) return L.Get("NothingPlaying");
        if (_model.IsSearching) return L.Get("Searching");
        if (_model.IsNotSong) return L.Get("NotASong");
        if (_model.Lyrics is not { } l) return L.Get("NoneFoundChoose");
        var kind = !l.IsSynced ? L.Get("KindPlain") : l.IsWordSynced ? L.Get("KindWords") : L.Get("KindLines");
        return _model.LyricsPinned ? $"{kind} · {l.Source} · {L.Get("ChooseLyricsPinned")}" : $"{kind} · {l.Source}";
    }

    /// <summary>
    /// A tile's lock toggle, in its bottom-right corner: filled while locked, outlined while not. It
    /// draws its own face so it shows on a lit (accent) tile as well as a plain one.
    /// </summary>
    sealed class TileLock
    {
        public ToggleButton Toggle { get; }
        readonly ToggleButton _tile;
        readonly Border _face;
        readonly FontIcon _icon = new() { FontSize = 13 };

        public TileLock(Grid host, ToggleButton tile, string name, Action<bool> set)
        {
            _tile = tile;
            _face = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = _icon };
            Toggle = new ToggleButton
            {
                Content = _face, Padding = new Thickness(0), BorderThickness = new Thickness(0), MinWidth = 0, MinHeight = 0, CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 8),
            };
            // The toggle's own fills stay clear at rest (the face shows the state); hover and press still show.
            var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            foreach (string key in new[] { "ToggleButtonBackground", "ToggleButtonBackgroundChecked", "ToggleButtonBackgroundCheckedPointerOver",
                                           "ToggleButtonBackgroundCheckedPressed", "ToggleButtonBackgroundDisabled", "ToggleButtonBackgroundCheckedDisabled" })
                Toggle.Resources[key] = clear;
            AutomationProperties.SetName(Toggle, name);
            Toggle.Click += (_, _) => set(Toggle.IsChecked == true);
            host.Children.Add(Toggle);
        }

        public void Sync(bool locked, bool usable, string unlockTip, string lockTip)
        {
            bool lit = _tile.IsChecked == true;
            Toggle.IsChecked = locked;
            Toggle.IsEnabled = usable;
            _icon.Glyph = locked ? "\uE72E" : "\uE785"; // a closed lock, an open one
            // On a lit tile the tile's text colour fills it; on a plain one, the accent.
            Brush ink = Theme(lit ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");
            Brush fill = Theme(lit ? "TextOnAccentFillColorPrimaryBrush" : "AccentFillColorDefaultBrush");
            _face.Background = locked ? fill : null;
            _face.BorderBrush = locked ? fill : ink;
            _icon.Foreground = locked ? Theme(lit ? "AccentFillColorDefaultBrush" : "TextOnAccentFillColorPrimaryBrush") : ink;
            _face.Opacity = usable ? 1 : 0.4;
            ToolTipService.SetToolTip(Toggle, locked ? unlockTip : lockTip);
        }

        static Brush Theme(string key) => (Brush)Application.Current.Resources[key];
    }

    void OnDesktopTile(object sender, RoutedEventArgs e) => _settings.ShowDesktopLyrics = DesktopTile.IsChecked == true;
    void OnIslandTile(object sender, RoutedEventArgs e) => _settings.ShowIsland = IslandTile.IsChecked == true;
    void OnEarlier(object sender, RoutedEventArgs e) => _model.NudgeSongOffset(0.25, clamp: true);
    void OnLater(object sender, RoutedEventArgs e) => _model.NudgeSongOffset(-0.25, clamp: true);

    void OnLyricsRow(object sender, RoutedEventArgs e) { Hide(); _chooseLyrics(); }
    void OnLyricsWindow(object sender, RoutedEventArgs e) { Hide(); _openLyricsWindow(); }
    void OnSettings(object sender, RoutedEventArgs e) { Hide(); _openSettings(); }
    void OnQuit(object sender, RoutedEventArgs e) { Hide(); _quit(); }
}
