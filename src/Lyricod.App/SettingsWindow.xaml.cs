using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Lyricod.Core;
using Lyricod.Platform;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using Windows.UI.Core;
using WinUIEx;

namespace Lyricod;

public enum SettingsPane { General, Desktop, Island, LyricsWindow, Lyrics, Shortcuts, About }

/// <summary>
/// Lyricod's settings, like the macOS app's: General, Desktop, Lyricod Island, Lyrics Window,
/// Lyrics, Shortcuts and About. Each control follows its setting while the page is up, so a change from the
/// tray menu or a shortcut shows here too.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    readonly UpdateChecker _updates;
    readonly Settings _settings;
    readonly PlaybackModel _model;
    readonly Action _openLyricsWindow;
    readonly Action _openChooseLyrics;
    readonly GlobalShortcuts _shortcuts;
    readonly HashSet<VirtualKey> _recordedKeys = [];
    ShortcutAction? _recording;
    string? _recordingProblem;
    /// <summary>Refreshes for the controls on the current page, by setting name.</summary>
    readonly Dictionary<string, List<Action>> _watchers = [];
    /// <summary>Refreshes for what follows the song (now playing, this song's timing, lyrics status).</summary>
    readonly List<Action> _songWatchers = [];

    /// <summary>Starts Lyricod again (a new language applies then).</summary>
    public Action? Restart { get; set; }

    static readonly string[] PresetColours = ["#8CD9FF", "#FFD36E", "#FF8FB1", "#9BF59B", "#FFFFFF"];

    public SettingsWindow(Settings settings, PlaybackModel model, Action openLyricsWindow, Action openChooseLyrics, GlobalShortcuts shortcuts, UpdateChecker updates, SettingsPane pane = SettingsPane.General)
    {
        InitializeComponent();
        _updates = updates;
        _updates.Changed += Refresh;
        _settings = settings;
        _model = model;
        _openLyricsWindow = openLyricsWindow;
        _openChooseLyrics = openChooseLyrics;
        _shortcuts = shortcuts;
        _shortcuts.Changed += RefreshShortcuts;
        Root.PreviewKeyDown += RecordKey;
        Root.PreviewKeyUp += (_, e) =>
        {
            // A recorded Space or Enter must not click the recorder again on release.
            if (_recordedKeys.Remove(e.OriginalKey)) e.Handled = true;
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                _recordedKeys.Clear();
                StopRecording();
            }
        };
        Title = TitleText.Text = L.Get("SettingsTitle");
        TitleIcon.Source = new BitmapImage(new Uri("ms-appx:///Assets/Lyricod-256.png")) { DecodePixelWidth = 32 };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Lyricod.ico"));
        this.CenterOnScreen(1000, 720);

        foreach (var p in Enum.GetValues<SettingsPane>())
            Nav.MenuItems.Add(new NavigationViewItem { Content = PaneTitle(p), Tag = p, Icon = new FontIcon { Glyph = PaneGlyph(p) } });
        _settings.PropertyChanged += OnSettingChanged;
        _model.Changed += OnModelChanged;
        Fonts.Changed += OnFontsChanged;
        Closed += (_, _) =>
        {
            _fontClosed = true;
            try { _fontDownload?.Cancel(); } catch (ObjectDisposedException) { }
            Fonts.Changed -= OnFontsChanged;
            StopRecording();
            _updates.Changed -= Refresh;
            _shortcuts.Changed -= RefreshShortcuts;
            _settings.PropertyChanged -= OnSettingChanged;
            _model.Changed -= OnModelChanged;
        };
        Show(pane);
    }

    public void Show(SettingsPane pane) =>
        Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().First(i => (SettingsPane)i.Tag == pane);

    static string PaneTitle(SettingsPane p) => p switch
    {
        SettingsPane.General => L.Get("PaneGeneral"),
        SettingsPane.Desktop => L.Get("PaneDesktop"),
        SettingsPane.Island => L.Get("PaneIsland"),
        SettingsPane.LyricsWindow => L.Get("PaneLyricsWindow"),
        SettingsPane.Lyrics => L.Get("PaneLyrics"),
        SettingsPane.Shortcuts => L.Get("PaneShortcuts"),
        _ => L.Get("PaneAbout"),
    };

    // Segoe Fluent Icons.
    static string PaneGlyph(SettingsPane p) => p switch
    {
        SettingsPane.General => "\uE713",      // Settings
        SettingsPane.Desktop => "\uE7F4",      // TV monitor
        SettingsPane.Island => "\uE8D6",       // Audio
        SettingsPane.LyricsWindow => "\uE8FD", // Bulleted list
        SettingsPane.Lyrics => "\uE8D2",       // Font
        SettingsPane.Shortcuts => "\uE765", // Keyboard
        _ => "\uE946",                         // Info
    };

    void OnPaneChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: SettingsPane pane }) return;
        StopRecording();
        _fontRefresh = null;
        _watchers.Clear();
        _songWatchers.Clear();
        Page.Children.Clear();
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        if (pane != SettingsPane.About)
            Page.Children.Add(new TextBlock { Text = PaneTitle(pane), Style = Style("TitleTextBlockStyle"), Margin = new Thickness(1, 16, 0, 12) });
        switch (pane)
        {
            case SettingsPane.General: General(); break;
            case SettingsPane.Desktop: Desktop(); break;
            case SettingsPane.Island: Island(); break;
            case SettingsPane.LyricsWindow: LyricsWindowPane(); break;
            case SettingsPane.Lyrics: Lyrics(); break;
            case SettingsPane.Shortcuts: Shortcuts(); break;
            default: About(); break;
        }
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != null && _watchers.TryGetValue(e.PropertyName, out var refreshes))
            foreach (var refresh in refreshes) refresh();
    }

    void OnModelChanged(PlaybackModel.Change change)
    {
        foreach (var refresh in _songWatchers) refresh();
    }

    void Watch(string property, Action refresh)
    {
        if (!_watchers.TryGetValue(property, out var list)) _watchers[property] = list = [];
        list.Add(refresh);
    }

    // ---- Pages ----

    void General()
    {
        var startup = new ToggleSwitch { IsOn = Startup.IsEnabled, OnContent = L.Get("ToggleOn"), OffContent = L.Get("ToggleOff") };
        startup.Toggled += (_, _) =>
        {
            if (startup.IsOn != Startup.IsEnabled && !Startup.Set(startup.IsOn)) startup.IsOn = Startup.IsEnabled;
        };
        Card(L.Get("OpenAtStartup"), L.Get("OpenAtStartupDetail"), startup);
        Card(L.Get("AnimationLabel"), L.Get("AnimationDetail"), Choice(nameof(Settings.FrameRate), () => _settings.FrameRate, v => _settings.FrameRate = v,
            (FrameRate.BatterySaver, L.Get("FrameRateLow")), (FrameRate.Balanced, L.Get("FrameRateBalanced")), (FrameRate.Smooth, L.Get("FrameRateSmooth"))));
        // Each language in its own name, so it can be found whatever is showing.
        Card(L.Get("LanguageLabel"), null, Choice(nameof(Settings.Language), () => _settings.Language, v => _settings.Language = v,
            ("", L.Get("LanguageSystem")), ("en-US", "English"), ("zh-Hans", "简体中文")));
        var restart = new Button { Content = L.Get("RestartNow"), Style = Style("AccentButtonStyle") };
        restart.Click += (_, _) => Restart?.Invoke();
        var restartCard = new Border { Style = Style("Card"), Child = RestartRow(restart) };
        void SyncRestart() => restartCard.Visibility = _settings.Language != L.Language ? Visibility.Visible : Visibility.Collapsed;
        Watch(nameof(Settings.Language), SyncRestart);
        SyncRestart();
        Page.Children.Add(restartCard);

        Heading(L.Get("PlayerHeading"));
        // Whichever is playing, or one player always: those Windows knows about now, and the saved one.
        var players = new List<(string, string)> { ("auto", L.Get("FollowAuto")) };
        foreach (var id in _model.PlayerIds.Append(_settings.PlayerChoice).Where(id => id != "auto").Distinct())
            players.Add((id, MediaSession.DisplayName(id)));
        Card(L.Get("FollowLabel"), L.Get("FollowDetail"), Choice(nameof(Settings.PlayerChoice), () => _settings.PlayerChoice, v => _settings.PlayerChoice = v, [.. players]));
        var song = Value();
        var player = Value();
        _songWatchers.Add(() =>
        {
            song.Text = _model.Track is { } t ? $"{t.Title} — {t.Artist}" : L.Get("NothingPlaying");
            player.Text = _model.PlayerName ?? "—";
        });
        Card(L.Get("NowPlayingLabel"), null, song);
        Card(L.Get("PlayerLabel"), null, player);
        Note(L.Get("PlayerNote"));
        Refresh();
    }

    void Desktop()
    {
        Card(L.Get("ShowDesktopLyrics"), null, Toggle(nameof(Settings.ShowDesktopLyrics), () => _settings.ShowDesktopLyrics, v => _settings.ShowDesktopLyrics = v));
        Card(L.Get("LockDesktopLyrics"), L.Get("LockDetail"), Toggle(nameof(Settings.DesktopLocked), () => _settings.DesktopLocked, v => _settings.DesktopLocked = v));
        var reset = new Button { Content = L.Get("ResetPosition") };
        reset.Click += (_, _) => { _settings.DesktopX = null; _settings.DesktopY = null; };
        Card(L.Get("PositionLabel"), null, reset);

        Heading(L.Get("AppearanceHeading"));
        Card(L.Get("TextSize"), null, TextSize());
        Card(L.Get("SungColour"), null, Colours());
        Card(L.Get("LayoutLabel"), L.Get("LayoutDetail"), Layout(nameof(Settings.SecondLine), () => _settings.SecondLine, v => _settings.SecondLine = v));
    }

    void Island()
    {
        Card(L.Get("ShowIsland"), L.Get("ShowIslandDetail"), Toggle(nameof(Settings.ShowIsland), () => _settings.ShowIsland, v => _settings.ShowIsland = v));
        Card(L.Get("OnHover"), null, Choice(nameof(Settings.IslandHover), () => _settings.IslandHover, v => _settings.IslandHover = v,
            (IslandHover.Expand, L.Get("HoverExpand")), (IslandHover.Hide, L.Get("HoverHide")), (IslandHover.Nothing, L.Get("HoverNothing")),
            (IslandHover.ByDirection, L.Get("HoverByDirection"))));
        Card(L.Get("AlbumColour"), L.Get("AlbumColourDetail"), Toggle(nameof(Settings.IslandAlbumColour), () => _settings.IslandAlbumColour, v => _settings.IslandAlbumColour = v));
        Card(L.Get("HideWhenPaused"), L.Get("HideWhenPausedDetail"), Toggle(nameof(Settings.IslandHideWhenPaused),
            () => _settings.IslandHideWhenPaused, v => _settings.IslandHideWhenPaused = v));
        Card(L.Get("AudioBars"), L.Get("AudioBarsDetail"), Toggle(nameof(Settings.IslandAudioBars), () => _settings.IslandAudioBars, v => _settings.IslandAudioBars = v));
        Card(L.Get("LayoutLabel"), null, Layout(nameof(Settings.IslandSecondLine), () => _settings.IslandSecondLine, v => _settings.IslandSecondLine = v));

        Heading(L.Get("StyleHeading"));
        Card(L.Get("IslandSize"), null, Choice(nameof(Settings.IslandSize), () => _settings.IslandSize, v => _settings.IslandSize = v,
            (IslandSize.Small, L.Get("SizeSmall")), (IslandSize.Medium, L.Get("SizeMedium")),
            (IslandSize.Large, L.Get("SizeLarge")), (IslandSize.ExtraLarge, L.Get("SizeExtraLarge"))));
        Card(L.Get("IslandMaxWidth"), L.Get("IslandMaxWidthDetail"), Choice(nameof(Settings.IslandMaxTextWidth),
            () => _settings.IslandMaxTextWidth, v => _settings.IslandMaxTextWidth = v,
            (320, L.Get("IslandWidthNarrow")), (460, L.Get("IslandWidthStandard")), (600, L.Get("IslandWidthWide"))));
        Card(L.Get("FontWeight"), null, Choice(nameof(Settings.IslandWeight), () => _settings.IslandWeight, v => _settings.IslandWeight = v,
            (IslandWeight.Regular, L.Get("WeightRegular")), (IslandWeight.Medium, L.Get("WeightMedium")), (IslandWeight.Semibold, L.Get("WeightSemibold")),
            (IslandWeight.Bold, L.Get("WeightBold")), (IslandWeight.Heavy, L.Get("WeightHeavy"))));
        Card(L.Get("SungLyrics"), null, Effect(nameof(Settings.IslandEffect), () => _settings.IslandEffect, v => _settings.IslandEffect = v));
        Card(L.Get("NextLine"), null, Choice(nameof(Settings.IslandTransition), () => _settings.IslandTransition, v => _settings.IslandTransition = v,
            (LineTransition.Instant, L.Get("TransitionInstant")), (LineTransition.Slide, L.Get("TransitionSlide")), (LineTransition.Fade, L.Get("TransitionFade"))));
    }

    ComboBox Effect(string property, Func<SungEffect> get, Action<SungEffect> set) => Choice(property, get, set,
        (SungEffect.Sweep, L.Get("EffectSweep")), (SungEffect.Glow, L.Get("EffectGlow")), (SungEffect.Line, L.Get("EffectLine")));

    /// <summary>"Lyricod needs to restart to change language", and the button.</summary>
    Grid RestartRow(Button restart)
    {
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = L.Get("RestartToChangeLanguage"), Style = Style("Detail"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(restart, 1);
        restart.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(restart);
        return grid;
    }

    void LyricsWindowPane()
    {
        var open = new Button { Content = L.Get("Open") };
        open.Click += (_, _) => _openLyricsWindow();
        Card(L.Get("LyricsWindowLabel"), L.Get("LyricsWindowDetail"), open);
        Card(L.Get("BackgroundLabel"), null, Choice(nameof(Settings.LyricsBackground), () => _settings.LyricsBackground, v => _settings.LyricsBackground = v,
            (LyricsBackground.Artwork, L.Get("BackgroundArtwork")), (LyricsBackground.StillArtwork, L.Get("BackgroundStillArtwork")),
            (LyricsBackground.Colour, L.Get("BackgroundColour")),
            (LyricsBackground.Dark, L.Get("BackgroundDark"))));

        Heading(L.Get("TextHeading"));
        Card(L.Get("TextSize"), null, Choice(nameof(Settings.LyricsTextSize), () => _settings.LyricsTextSize, v => _settings.LyricsTextSize = v,
            (LyricsTextSize.Automatic, L.Get("SizeAutomatic")), (LyricsTextSize.Small, L.Get("SizeSmall")),
            (LyricsTextSize.Medium, L.Get("SizeMedium")), (LyricsTextSize.Large, L.Get("SizeLarge"))));
        Card(L.Get("ShowTranslations"), null, Toggle(nameof(Settings.ShowTranslations), () => _settings.ShowTranslations, v => _settings.ShowTranslations = v));
        Card(L.Get("ShowPronunciation"), null, Toggle(nameof(Settings.ShowPronunciation), () => _settings.ShowPronunciation, v => _settings.ShowPronunciation = v));
        Card(L.Get("CentreLines"), null, Toggle(nameof(Settings.LyricsCentered), () => _settings.LyricsCentered, v => _settings.LyricsCentered = v));
        Card(L.Get("BlurDistant"), null, Toggle(nameof(Settings.LyricsBlurDistant), () => _settings.LyricsBlurDistant, v => _settings.LyricsBlurDistant = v));
        Card(L.Get("SungLyrics"), null, Effect(nameof(Settings.LyricsEffect), () => _settings.LyricsEffect, v => _settings.LyricsEffect = v));
        Card(L.Get("AutoHideControls"), L.Get("AutoHideControlsDetail"), Toggle(nameof(Settings.LyricsAutoHide), () => _settings.LyricsAutoHide, v => _settings.LyricsAutoHide = v));
    }

    void Lyrics()
    {
        Heading(L.Get("TimingHeading"));
        Card(L.Get("AllSongs"), null, Offset(() => _settings.Offset, v => _settings.Offset = v, () => true, nameof(Settings.Offset)));
        Card(L.Get("ThisSong"), null, Offset(() => _model.SongOffset, _model.SetSongOffset, () => _model.Track != null, null));
        Note(L.Get("TimingNote"));

        Heading(L.Get("DisplayHeading"));
        FontPicker();
        var languages = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach (var (language, key) in new[] { (LyricLanguage.Japanese, "Japanese"), (LyricLanguage.Korean, "Korean"), (LyricLanguage.Chinese, "Chinese") })
        {
            var box = new CheckBox { Content = L.Get(key), MinWidth = 0 };
            void Sync() => box.IsChecked = _settings.PronunciationLanguages.Contains(language);
            Sync();
            box.Click += (_, _) =>
            {
                var set = _settings.PronunciationLanguages.ToHashSet();
                if (box.IsChecked == true) set.Add(language); else set.Remove(language);
                _settings.PronunciationLanguages = [.. set.OrderBy(l => l)];
            };
            Watch(nameof(Settings.PronunciationLanguages), Sync);
            languages.Children.Add(box);
        }
        Card(L.Get("PronunciationFor"), L.Get("PronunciationForDetail"), languages);
        Card(L.Get("ShowCredits"), null, Toggle(nameof(Settings.ShowCredits), () => _settings.ShowCredits, v => _settings.ShowCredits = v));
        Note(L.Get(Lyricod.Platform.JapaneseReader.IsAvailable ? "PronunciationNote" : "PronunciationNoteNoReader"));

        Heading(L.Get("SourcesHeading"));
        Card(L.Get("LookUpOtherPlayers"), L.Get("LookUpOtherPlayersDetail"),
            Toggle(nameof(Settings.LookUpOtherPlayers), () => _settings.LookUpOtherPlayers, v => _settings.LookUpOtherPlayers = v));
        var status = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var title = new TextBlock { TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
        var kind = new TextBlock { Style = Style("Detail"), TextAlignment = TextAlignment.Right };
        status.Children.Add(title);
        status.Children.Add(kind);
        var again = new Button { Content = L.Get("SearchAgain") };
        again.Click += (_, _) => _model.SearchAgain();
        var choose = new Button { Content = L.Get("ChooseLyricsButton") };
        choose.Click += (_, _) => _openChooseLyrics();
        _songWatchers.Add(() =>
        {
            title.Text = _model.Track?.Title ?? L.Get("NothingPlaying");
            kind.Text = LyricsStatus();
            again.IsEnabled = _model.Track != null && !_model.IsSearching && _model.AutomaticLookupAllowed;
            choose.IsEnabled = _model.Track != null;
        });
        Card(L.Get("CurrentSong"), null, Row(status, again, choose));

        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Style = Style("Detail") };
        var clear = new Button { Content = L.Get("Clear") };
        void Count()
        {
            int n = _model.CachedSongs;
            count.Text = n == 1 ? L.Get("OneSong") : L.Format("NSongs", n);
            clear.IsEnabled = n > 0;
        }
        clear.Click += (_, _) => { _model.ClearSavedLyrics(); Count(); };
        Count();
        Card(L.Get("SavedLyrics"), L.Get("SavedLyricsDetail"), Row(count, clear));
        Refresh();
    }

    string LyricsStatus()
    {
        if (_model.Track == null) return "—";
        if (_model.IsSearching) return L.Get("Searching");
        if (_model.Lyrics is not { } l) return L.Get("NoLyrics");
        var kind = !l.IsSynced ? L.Get("KindPlain") : l.IsWordSynced ? L.Get("KindWords") : L.Get("KindLines");
        return $"{l.Source} · {kind}";
    }

    static string ShortcutTitle(ShortcutAction action) => L.Get("Shortcut" + action);

    void RefreshShortcuts() => OnSettingChanged(this, new PropertyChangedEventArgs(nameof(Settings.Shortcuts)));

    void Shortcuts()
    {
        Heading(L.Get("ShortcutPlayback"));
        ShortcutRow(ShortcutAction.PlayPause);
        ShortcutRow(ShortcutAction.Next);
        ShortcutRow(ShortcutAction.Previous);
        Heading(L.Get("ShortcutShow"));
        ShortcutRow(ShortcutAction.ToggleDesktop);
        ShortcutRow(ShortcutAction.ToggleIsland);
        ShortcutRow(ShortcutAction.ToggleLyricsWindow);
        Heading(L.Get("TimingHeading"));
        ShortcutRow(ShortcutAction.LyricsEarlier);
        ShortcutRow(ShortcutAction.LyricsLater);
        Note(L.Get("ShortcutTimingNote"));
        Note(L.Get("ShortcutNote"));
    }

    void ShortcutRow(ShortcutAction action)
    {
        var button = new Button { MinWidth = 180, HorizontalAlignment = HorizontalAlignment.Right };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, ShortcutTitle(action));
        var problem = new TextBlock { Style = Style("Detail"), MaxWidth = 280, TextWrapping = TextWrapping.Wrap };
        var control = new StackPanel { Spacing = 4 };
        control.Children.Add(button);
        control.Children.Add(problem);
        void Sync()
        {
            bool recording = _recording == action;
            button.Content = recording ? L.Get("ShortcutPressKeys") : _shortcuts.Combo(action)?.Display(L.Get) ?? L.Get("ShortcutRecord");
            problem.Text = recording ? _recordingProblem ?? "" : _shortcuts.Failed(action) ? L.Get("ShortcutUnavailable") : "";
            problem.Visibility = problem.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        button.Click += (_, _) =>
        {
            StopRecording();
            _recording = action;
            _shortcuts.Paused = true;
            button.Focus(FocusState.Programmatic);
            RefreshShortcuts();
        };
        button.LostFocus += (_, _) => { if (_recording == action) StopRecording(); };
        Sync();
        Watch(nameof(Settings.Shortcuts), Sync);
        Card(ShortcutTitle(action), null, control);
    }

    void StopRecording()
    {
        if (_recording == null) return;
        _recording = null;
        _recordingProblem = null;
        _shortcuts.Paused = false;
        RefreshShortcuts();
    }

    void RecordKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.KeyStatus.WasKeyDown && _recordedKeys.Contains(e.OriginalKey)) { e.Handled = true; return; }
        if (_recording is not { } action) return;
        e.Handled = true;
        _recordedKeys.Add(e.OriginalKey);
        if (e.KeyStatus.WasKeyDown) return;
        if (e.OriginalKey == VirtualKey.Escape) { StopRecording(); return; }
        if (e.OriginalKey is VirtualKey.Back or VirtualKey.Delete)
        {
            _shortcuts.Set(action, null);
            StopRecording();
            return;
        }
        uint key = (uint)e.OriginalKey;
        if (KeyCombo.IsModifier(key)) return;
        static bool Down(VirtualKey k) => (InputKeyboardSource.GetKeyStateForCurrentThread(k) & CoreVirtualKeyStates.Down) != 0;
        var modifiers = KeyModifiers.None;
        if (Down(VirtualKey.Control)) modifiers |= KeyModifiers.Control;
        if (Down(VirtualKey.Menu)) modifiers |= KeyModifiers.Alt;
        if (Down(VirtualKey.Shift)) modifiers |= KeyModifiers.Shift;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= KeyModifiers.Win;
        var combo = new KeyCombo(modifiers, key);
        if (!combo.IsUsable) _recordingProblem = L.Get("ShortcutInvalid");
        else if (_shortcuts.ActionUsing(combo, action) is { } other)
            _recordingProblem = L.Format("ShortcutDuplicate", ShortcutTitle(other));
        else
        {
            _shortcuts.Set(action, combo);
            StopRecording();
            return;
        }
        RefreshShortcuts();
    }

    void About()
    {
        var top = new StackPanel { Spacing = 4, Margin = new Thickness(0, 24, 0, 4) };
        top.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Lyricod-256.png")) { DecodePixelWidth = 160 },
            Width = 80, Height = 80, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8),
        });
        top.Children.Add(Centred(new TextBlock { Text = "Lyricod", Style = Style("TitleTextBlockStyle") }));
        top.Children.Add(Centred(new TextBlock { Text = L.Format("Version", UpdateChecker.DisplayVersion), Style = Style("Detail") }));
        top.Children.Add(Centred(new TextBlock { Text = L.Get("Tagline"), Margin = new Thickness(0, 6, 0, 0) }));
        top.Children.Add(Centred(new HyperlinkButton { Content = "GitHub", NavigateUri = new Uri("https://github.com/Indarkdust/Lyricod-App") }));
        Page.Children.Add(top);

        Heading(L.Get("Updates"));
        Card(L.Get("AutomaticUpdates"), null, Toggle(nameof(Settings.CheckForUpdatesAutomatically),
            () => _settings.CheckForUpdatesAutomatically, v => _settings.CheckForUpdatesAutomatically = v));
        UpdateControls();

        Heading(L.Get("Diagnostics"));
        var diagnostics = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            Visibility = Visibility.Collapsed, // hidden until asked for
        };
        void RefreshDiagnostics() => diagnostics.Text = _model.DiagnosticsText;
        _songWatchers.Add(RefreshDiagnostics);
        Watch(nameof(Settings.Offset), RefreshDiagnostics);
        RefreshDiagnostics();
        var copy = new Button { Content = L.Get("CopyDiagnostics") };
        copy.Click += (_, _) =>
        {
            RefreshDiagnostics();
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(diagnostics.Text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        };
        var show = new Button { Content = L.Get("ShowDiagnostics") };
        show.Click += (_, _) =>
        {
            bool shown = diagnostics.Visibility == Visibility.Visible;
            diagnostics.Visibility = shown ? Visibility.Collapsed : Visibility.Visible;
            show.Content = L.Get(shown ? "ShowDiagnostics" : "HideDiagnostics");
        };
        Group(new TextBlock { Text = L.Get("DiagnosticsDetail"), Style = Style("Detail"), TextWrapping = TextWrapping.Wrap },
              Row(show, copy), diagnostics);

        Heading(L.Get("LyricsHeader"));
        Group(Entry(L.Get("NetEase"), L.Get("SourceDetail"), "https://music.163.com"),
              Entry(L.Get("QQMusic"), L.Get("SourceDetail"), "https://y.qq.com"),
              Entry("Lyricify Lyrics Helper by WXRIW", L.Get("LyricsHelperDetail"), "https://github.com/WXRIW/Lyricify-Lyrics-Helper"),
              Entry("LRCLIB", L.Get("LrclibDetail"), "https://lrclib.net"),
              new TextBlock { Text = L.Get("LyricsRights"), Style = Style("Detail") });

        Heading(L.Get("FontHeader"));
        var licence = new HyperlinkButton { Content = L.Get("ViewLicence"), Padding = new Thickness(0, 4, 0, 0) };
        licence.Click += (_, _) => Open(MiSansDownload.LicencePath);
        void ShowFontLicence() => licence.Visibility = MiSansDownload.IsDownloaded ? Visibility.Visible : Visibility.Collapsed;
        _songWatchers.Add(ShowFontLicence);
        ShowFontLicence();
        Group(Entry(L.Get("MiSansName"), L.Get("MiSansDetail"), "https://hyperos.mi.com/font/"), licence);

        Heading(L.Get("BuiltWith"));
        Group(Entry("WinUIEx by Morten Nielsen", L.Get("WinUIExDetail"), "https://github.com/dotMorten/WinUIEx"),
              Entry("ToolGood.Words", L.Get("ToolGoodDetail"), "https://github.com/toolgood/ToolGood.Words"),
              new TextBlock { Text = L.Get("PlatformNote"), Style = Style("Detail") });

        Heading(L.Get("Trademarks"));
        Group(new TextBlock { Text = L.Get("TrademarksText"), Style = Style("Detail") });
    }

    // ---- Building blocks ----

    Style Style(string key) => Root.Resources.TryGetValue(key, out var local) ? (Style)local : (Style)Application.Current.Resources[key];

    void Refresh()
    {
        foreach (var refresh in _songWatchers) refresh();
    }

    void Heading(string text) => Page.Children.Add(new TextBlock { Text = text, Style = Style("Heading") });

    void Note(string text) => Page.Children.Add(new TextBlock { Text = text, Style = Style("Note") });

    TextBlock Value() => new() { Style = Style("Detail"), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420, TextWrapping = TextWrapping.NoWrap };

    static T Centred<T>(T element) where T : FrameworkElement
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        return element;
    }

    static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    /// <summary>A setting: its name (and a line about it) on the left, its control on the right.</summary>
    void Card(string title, string? detail, FrameworkElement? control)
    {
        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (detail != null) text.Children.Add(new TextBlock { Text = detail, Style = Style("Detail") });
        grid.Children.Add(text);
        if (control != null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }
        Page.Children.Add(new Border { Style = Style("Card"), Child = grid });
    }

    /// <summary>Several rows in one card (About).</summary>
    void Group(params UIElement[] rows)
    {
        var inside = new StackPanel { Spacing = 12 };
        foreach (var row in rows) inside.Children.Add(row);
        Page.Children.Add(new Border { Style = Style("Card"), Child = inside, Padding = new Thickness(16, 14, 16, 14) });
    }

    /// <summary>A name, a line about it, and a link out.</summary>
    Grid Entry(string name, string detail, string url)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, Style = Style("BodyStrongTextBlockStyle") });
        text.Children.Add(new TextBlock { Text = detail, Style = Style("Detail") });
        grid.Children.Add(text);
        var link = new HyperlinkButton { Content = new FontIcon { Glyph = "\uE8A7", FontSize = 14 }, NavigateUri = new Uri(url), VerticalAlignment = VerticalAlignment.Top };
        ToolTipService.SetToolTip(link, url);
        Grid.SetColumn(link, 1);
        grid.Children.Add(link);
        return grid;
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException) { }
    }

    ToggleSwitch Toggle(string property, Func<bool> get, Action<bool> set)
    {
        // WinUI's own On/Off would stay in Windows' language.
        var toggle = new ToggleSwitch { IsOn = get(), MinWidth = 0, OnContent = L.Get("ToggleOn"), OffContent = L.Get("ToggleOff") };
        toggle.Toggled += (_, _) => { if (toggle.IsOn != get()) set(toggle.IsOn); };
        Watch(property, () => { if (toggle.IsOn != get()) toggle.IsOn = get(); });
        return toggle;
    }

    ComboBox Choice<T>(string property, Func<T> get, Action<T> set, params (T Value, string Label)[] options)
    {
        var box = new ComboBox { MinWidth = 260 };
        foreach (var option in options) box.Items.Add(option.Label);
        int Index() => Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.Value, get()));
        box.SelectedIndex = Index();
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && box.SelectedIndex != Index()) set(options[box.SelectedIndex].Value);
        };
        Watch(property, () => box.SelectedIndex = Index());
        return box;
    }

    ComboBox Layout(string property, Func<SecondLine> get, Action<SecondLine> set) =>
        Choice(property, get, set, [.. Enum.GetValues<SecondLine>().Select(mode => (mode, L.Get("Layout" + mode)))]);

    FrameworkElement TextSize()
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Style = Style("Detail"), MinWidth = 44, TextAlignment = TextAlignment.Right };
        var slider = new Slider { Minimum = 18, Maximum = 48, StepFrequency = 1, Width = 220, Value = _settings.DesktopFontSize, VerticalAlignment = VerticalAlignment.Center };
        void Sync()
        {
            label.Text = L.Format("Points", _settings.DesktopFontSize);
            if (Math.Abs(slider.Value - _settings.DesktopFontSize) > 0.5) slider.Value = _settings.DesktopFontSize;
        }
        slider.ValueChanged += (_, e) => _settings.DesktopFontSize = Math.Round(e.NewValue);
        Watch(nameof(Settings.DesktopFontSize), Sync);
        Sync();
        return Row(slider, label);
    }

    FrameworkElement Colours()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var swatches = new List<(Button Button, string Hex)>();
        foreach (var hex in PresetColours)
        {
            var swatch = Swatch(DesktopLyricsWindow.ParseColor(hex));
            swatch.Click += (_, _) => _settings.SungColor = hex;
            ToolTipService.SetToolTip(swatch, hex);
            swatches.Add((swatch, hex));
            row.Children.Add(swatch);
        }
        // Any colour, from a picker.
        var picker = new ColorPicker
        {
            Color = DesktopLyricsWindow.ParseColor(_settings.SungColor), IsAlphaEnabled = false,
            IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false,
        };
        picker.ColorChanged += (_, e) => _settings.SungColor = Hex(e.NewColor);
        var custom = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16),
            Content = new FontIcon { Glyph = "\uE790", FontSize = 14 }, // Color
            Flyout = new Flyout { Content = picker },
        };
        ToolTipService.SetToolTip(custom, L.Get("CustomColour"));
        row.Children.Add(custom);

        void Sync()
        {
            var current = _settings.SungColor.ToUpperInvariant();
            foreach (var (button, hex) in swatches) button.BorderThickness = new Thickness(hex == current ? 3 : 1);
            custom.BorderThickness = new Thickness(PresetColours.Contains(current) ? 1 : 3);
        }
        Watch(nameof(Settings.SungColor), Sync);
        Sync();
        return row;
    }

    static Button Swatch(Windows.UI.Color colour) => new()
    {
        Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16),
        Background = new SolidColorBrush(colour), BorderBrush = new SolidColorBrush(Colors.Gray),
        Resources = { ["ButtonBackgroundPointerOver"] = new SolidColorBrush(colour), ["ButtonBackgroundPressed"] = new SolidColorBrush(colour) },
    };

    static string Hex(Windows.UI.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>A timing offset: −, the value, +, Reset.</summary>
    FrameworkElement Offset(Func<double> get, Action<double> set, Func<bool> enabled, string? property)
    {
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 64, TextAlignment = TextAlignment.Center };
        var minus = new Button { Content = new FontIcon { Glyph = "\uE738", FontSize = 12 }, Padding = new Thickness(10, 8, 10, 8) };
        var plus = new Button { Content = new FontIcon { Glyph = "\uE710", FontSize = 12 }, Padding = new Thickness(10, 8, 10, 8) };
        var reset = new Button { Content = L.Get("Reset") };
        ToolTipService.SetToolTip(minus, L.Get("OffsetLater"));
        ToolTipService.SetToolTip(plus, L.Get("OffsetEarlier"));
        minus.Click += (_, _) => set(Math.Clamp(get() - 0.25, -10, 10));
        plus.Click += (_, _) => set(Math.Clamp(get() + 0.25, -10, 10));
        reset.Click += (_, _) => set(0);
        void Sync()
        {
            bool on = enabled();
            value.Text = get().ToString("+0.00 s;-0.00 s;0.00 s", CultureInfo.CurrentCulture);
            value.Opacity = on ? 1 : 0.5;
            minus.IsEnabled = plus.IsEnabled = on;
            reset.IsEnabled = on && get() != 0;
        }
        if (property != null) Watch(property, Sync); else _songWatchers.Add(Sync);
        Sync();
        return Row(minus, value, plus, reset);
    }
}
