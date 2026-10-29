using System.ComponentModel;
using System.Numerics;
using Lyricod.Core;
using Lyricod.Core.Romanization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinUIEx;

namespace Lyricod;

/// <summary>
/// The whole song, like the macOS app's lyrics window: the line being sung sweeps word by word
/// (in rows that fit the width) and stays a third of the way down; the others shrink a little and
/// fade and blur with distance, and scrolling eases like a spring. The softened cover drifts behind.
/// Click a line to jump there. Wide windows and full screen put a large cover beside the lyrics.
/// </summary>
public sealed partial class LyricsWindow : Window
{
    readonly PlaybackModel _model;
    readonly Settings _settings;
    readonly Action _chooseLyrics;
    readonly TransportButtons _transport;
    readonly ProgressLine _progress;
    readonly DispatcherQueueTimer _progressTimer;
    readonly DispatcherQueueTimer _relayout;
    readonly ArtworkDrift _drift;
    /// <summary>Fires when the pointer has rested a few seconds (controls then fade, when wide).</summary>
    readonly DispatcherQueueTimer _idle;
    bool _pointerIdle, _closed;
    readonly List<LineView> _views = [];
    int? _current;
    bool _wide;
    double _fontSize = 30;
    /// <summary>Text widths in the current line's font, cleared when the font or size changes.</summary>
    readonly Dictionary<string, double> _widths = [];

    /// <summary>One lyric line on screen; while it's being sung, its rows and their sweeps.</summary>
    sealed class LineView(LyricLine line, StackPanel panel)
    {
        public LyricLine Line { get; } = line;
        public StackPanel Panel { get; } = panel;
        public List<(LyricLine Piece, double Width, SweepPainter Sweep)> Rows { get; } = [];
        /// <summary>The line's texts that soften with distance (none while it's being sung).</summary>
        public List<SoftText> Soft { get; } = [];
    }

    static readonly TimeSpan Transition = TimeSpan.FromSeconds(0.35);

    public LyricsWindow(PlaybackModel model, Settings settings, Action chooseLyrics)
    {
        InitializeComponent();
        _model = model;
        _settings = settings;
        _chooseLyrics = chooseLyrics;
        Title = L.Get("LyricsWindowTitle");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Lyricod.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        // Caption buttons readable over the dark artwork.
        var bar = AppWindow.TitleBar;
        bar.ButtonForegroundColor = Colors.White;
        bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(160, 255, 255, 255);
        bar.ButtonBackgroundColor = bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(40, 255, 255, 255);
        bar.ButtonHoverForegroundColor = Colors.White;
        this.CenterOnScreen(520, 760);
        AppWindow.Changed += (_, e) => { if (e.DidPresenterChange) UpdateFullScreenIcon(); };

        _drift = new ArtworkDrift(ArtHost);
        _transport = new TransportButtons(model);
        _progress = new ProgressLine(model);
        ToolTipService.SetToolTip(TranslationButton, L.Get("TranslationsTip"));
        ToolTipService.SetToolTip(PronunciationButton, L.Get("PronunciationTip"));
        ToolTipService.SetToolTip(FullScreenButton, L.Get("FullScreenTip"));
        ToolTipService.SetToolTip(ChooseButton, L.Get("ChooseLyricsTip"));

        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromSeconds(0.5);
        _progressTimer.Tick += (_, _) => { try { _progress.Update(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } };
        // Rows depend on the width: rebuild the sung line once resizing settles.
        _relayout = DispatcherQueue.CreateTimer();
        _relayout.IsRepeating = false;
        _relayout.Interval = TimeSpan.FromMilliseconds(120);
        _relayout.Tick += (_, _) => { try { Relayout(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } };

        Root.SizeChanged += (_, _) => _relayout.Start();
        _idle = DispatcherQueue.CreateTimer();
        _idle.IsRepeating = false;
        _idle.Interval = TimeSpan.FromSeconds(3);
        _idle.Tick += (_, _) => { try { _pointerIdle = true; UpdateControls(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } };
        Root.PointerMoved += (_, _) =>
        {
            _idle.Stop();
            _idle.Start();
            if (_pointerIdle) { _pointerIdle = false; UpdateControls(); }
        };
        foreach (var element in new UIElement[] { TopButtons, BigProgressHost, BigTransportHost })
            element.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromSeconds(0.4) };
        Root.KeyDown += OnKeyDown;
        _model.Changed += OnModelChanged;
        Fonts.Changed += OnFontChanged;
        _settings.PropertyChanged += OnSettingChanged;
        Activated += (_, e) => UpdateTimer();
        Closed += (_, _) =>
        {
            _closed = true;
            _model.Changed -= OnModelChanged;
            Fonts.Changed -= OnFontChanged;
            _settings.PropertyChanged -= OnSettingChanged;
            _progressTimer.Stop();
            _relayout.Stop();
            _idle.Stop();
        };

        SyncButtons();
        ApplyLayout(force: true);
        _ = ApplyArtwork();
        UpdateSong();
        BuildLines();
    }

    // ---- Model and settings ----

    void OnFontChanged()
    {
        try
        {
            BuildLines();
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("font refresh: " + e); }
    }

    void OnModelChanged(PlaybackModel.Change change)
    {
        if (change.HasFlag(PlaybackModel.Change.Track) || change.HasFlag(PlaybackModel.Change.Lyrics)) UpdateSong();
        if (change.HasFlag(PlaybackModel.Change.Artwork)) _ = ApplyArtwork();
        if (change.HasFlag(PlaybackModel.Change.Lyrics) || change.HasFlag(PlaybackModel.Change.Track)) BuildLines();
        else if (change.HasFlag(PlaybackModel.Change.Line)) MoveToCurrent(animate: true);
        if (change.HasFlag(PlaybackModel.Change.Clock))
        {
            AnimateCurrent();
            _drift.Moving = _model.IsPlaying && _settings.LyricsBackground == LyricsBackground.Artwork;
            _transport.Update();
            _progress.Update();
            UpdateTimer();
            UpdateControls();
        }
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.LyricsEffect):
                if (_current is int i && i < _views.Count) RenderLine(_views[i], current: true);
                AnimateCurrent();
                break;
            case nameof(Settings.LyricsAutoHide):
                UpdateControls();
                break;
            case nameof(Settings.LyricsBlurDistant):
                foreach (var view in _views) ApplyDistance(view);
                break;
            case nameof(Settings.ShowTranslations) or nameof(Settings.ShowPronunciation) or nameof(Settings.LyricsCentered):
                SyncButtons();
                BuildLines();
                break;
            case nameof(Settings.LyricsTextSize):
                ApplyLayout(force: true);
                break;
            case nameof(Settings.LyricsBackground):
                _ = ApplyArtwork();
                break;
        }
    }

    void UpdateTimer()
    {
        // Closing deactivates the window, which comes here too.
        if (!_closed && _model.IsPlaying && _model.Duration > 0) _progressTimer.Start(); else _progressTimer.Stop();
    }

    void UpdateSong()
    {
        var t = _model.Track;
        SmallTitle.Text = BigTitle.Text = t?.Title ?? "";
        // Just the artist (the source is in Choose Lyrics), with a pin when the lyrics were chosen by hand.
        string artist = t?.Artist ?? "";
        SmallArtist.Text = BigArtist.Text = _model.LyricsPinned ? artist + " \U0001F4CC" : artist;
        ChooseButton.IsEnabled = t != null;
        ControlBar.Visibility = t != null && !_wide ? Visibility.Visible : Visibility.Collapsed;
        _transport.Update();
        _progress.Update();
        UpdateTimer();
    }

    async Task ApplyArtwork()
    {
        var bytes = _model.Artwork;
        var cover = await Images.FromBytes(bytes, 480);
        var small = await Images.FromBytes(bytes, 96);
        if (!ReferenceEquals(bytes, _model.Artwork)) return; // the song moved on
        BigCover.Source = cover;
        SmallCover.Source = small;
        var style = _settings.LyricsBackground;
        _drift.Moving = _model.IsPlaying && style == LyricsBackground.Artwork;
        await _drift.SetCover(style is LyricsBackground.Artwork or LyricsBackground.StillArtwork ? bytes : null);
        if (style == LyricsBackground.Colour && _model.ArtworkColor is { } c)
        {
            BackdropColour.Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop { Color = Shade(c, 0.45), Offset = 0 },
                    new GradientStop { Color = Shade(c, 0.22), Offset = 1 },
                },
            };
            BackdropColour.Visibility = Visibility.Visible;
        }
        else BackdropColour.Visibility = Visibility.Collapsed;
    }

    static Windows.UI.Color Shade(Windows.UI.Color c, double k) =>
        Windows.UI.Color.FromArgb(255, (byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));

    // ---- Layout ----

    /// <summary>Wide (cover beside the lyrics) or narrow (control bar), and the text size.</summary>
    void ApplyLayout(bool force = false)
    {
        double w = Root.ActualWidth > 0 ? Root.ActualWidth : 520, h = Root.ActualHeight > 0 ? Root.ActualHeight : 760;
        bool wide = w >= 820 && w > h * 1.05;
        double automatic = wide ? Math.Clamp(h / 19, 30, 64) : 30;
        double size = Math.Round(_settings.LyricsTextSize switch
        {
            LyricsTextSize.Small => automatic * 0.8,
            LyricsTextSize.Large => automatic * 1.25,
            _ => automatic,
        });
        bool changed = force || wide != _wide || size != _fontSize;
        _wide = wide;
        _fontSize = size;

        // Wide, as on the Mac: the cover and the lyrics as one centred block, the lyrics set in from
        // the cover, so the pair sits balanced instead of hugging the left edge in full screen; the
        // cover, title and controls grow with the window. (Every resize: the block follows the width.)
        double block = Math.Min(w, 1700), margin = wide ? (w - block) / 2 : 0;
        CoverColumn.Width = new GridLength(wide ? margin + Math.Min(block * 0.4, 600) : 0);
        CoverPanel.Margin = new Thickness(margin, 0, 0, 0);
        Scroller.Margin = Message.Margin = new Thickness(wide ? block * 0.04 : 0, 0, margin, 0);
        double side = Math.Max(Math.Min(Math.Min(block * 0.38 - 96, h * 0.5), 460), 120);
        BigCoverFrame.Width = BigCoverFrame.Height = BigTitles.Width = BigProgressHost.Width = side;
        BigCoverFrame.CornerRadius = new CornerRadius(side * 0.06);
        double titleSize = Math.Clamp(side / 13, 17, 32);
        BigTitle.FontSize = Math.Round(titleSize);
        BigArtist.FontSize = Math.Round(titleSize * 0.7);
        if (!changed) return;

        CoverPanel.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        ControlBar.Visibility = !wide && _model.Track != null ? Visibility.Visible : Visibility.Collapsed;
        Move(_transport, wide ? BigTransportHost : SmallTransportHost);
        Move(_progress, wide ? BigProgressHost : SmallProgressHost);
        _transport.SetLayoutScale(wide ? 1.35 : 1, 16);
        _progress.SetLayoutScale(wide ? 1.15 : 1);
        UpdateControls();
        _widths.Clear();
        BuildLines();
    }

    static void Move(FrameworkElement element, Panel host)
    {
        if (element.Parent == host) return;
        if (element.Parent is Panel old) old.Children.Remove(element);
        host.Children.Add(element);
    }

    void Relayout()
    {
        ApplyLayout();
        // Same layout, new width: only the sung line's rows change.
        if (_current is int i && i < _views.Count)
        {
            RenderLine(_views[i], current: true);
            AnimateCurrent();
        }
        UpdatePadding();
        ScrollToCurrent(animate: false);
    }

    void UpdatePadding()
    {
        double h = Scroller.ActualHeight > 0 ? Scroller.ActualHeight : 600;
        double side = _wide ? 40 : 28;
        // Room above and below, so the first and last lines can sit at the reading point.
        Lines.Padding = new Thickness(side, h * 0.3, side, h * 0.6 + 100);
    }

    double TextWidth => Math.Max((Scroller.ActualWidth > 0 ? Scroller.ActualWidth : 480) - 2 * (_wide ? 40 : 28), 100);

    // ---- Lines ----

    void BuildLines()
    {
        Lines.Children.Clear();
        _views.Clear();
        _current = null;
        var lyrics = _model.Lyrics;
        MessageButton.Visibility = MessageDetail.Visibility = Visibility.Collapsed;
        if (_model.Track == null || _model.IsSearching || lyrics == null)
        {
            MessageText.Text = _model.Track == null ? L.Get("NothingPlaying") : _model.IsSearching ? L.Get("Searching") : _model.IsNotSong ? L.Get("NotASong") : L.Get("NoLyrics");
            if (_model.Track != null && !_model.IsSearching)
            {
                // As on the Mac: search for them yourself and pick the right song.
                MessageDetail.Text = L.Get("NoLyricsDetail");
                MessageButton.Content = L.Get("ChooseLyricsTip");
                MessageDetail.Visibility = _model.IsNotSong ? Visibility.Collapsed : Visibility.Visible;
                MessageButton.Visibility = Visibility.Visible;
            }
            Message.Visibility = Visibility.Visible;
            return;
        }
        Message.Visibility = Visibility.Collapsed;
        UpdatePadding();
        foreach (var line in lyrics.Lines)
        {
            var panel = new StackPanel
            {
                Spacing = _fontSize * 0.2, Margin = new Thickness(0, 0, 0, _fontSize * 0.73),
                HorizontalAlignment = _settings.LyricsCentered ? HorizontalAlignment.Center : HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent), // hit-testable for clicks
                // Fading and shrinking run on the compositor.
                OpacityTransition = new ScalarTransition { Duration = Transition },
                ScaleTransition = new Vector3Transition { Duration = Transition },
            };
            // Lines shrink towards their start (or middle, when centred), as on the Mac.
            panel.SizeChanged += (_, e) => panel.CenterPoint =
                new Vector3(_settings.LyricsCentered ? (float)e.NewSize.Width / 2 : 0, (float)e.NewSize.Height / 2, 0);
            var view = new LineView(line, panel);
            panel.Tapped += (_, _) => { if (lyrics.IsSynced) _model.Seek(line); };
            _views.Add(view);
            RenderLine(view, current: false);
            Lines.Children.Add(panel);
        }
        MoveToCurrent(animate: false);
    }

    /// <summary>Draws a line plain (wrapped, faded) or, while it's being sung, as rows that sweep.</summary>
    void RenderLine(LineView view, bool current)
    {
        var lyrics = _model.Lyrics;
        if (lyrics == null) return;
        var line = view.Line;
        var song = Romanizer.SongLanguage(lyrics);
        var align = _settings.LyricsCentered ? TextAlignment.Center : TextAlignment.Left;
        var font = Fonts.For(line.Text, song);
        view.Panel.Children.Clear();
        view.Rows.Clear();
        view.Soft.Clear();

        if (current && lyrics.IsSynced)
        {
            var pieces = LinePieces.Split(line, TextWidth, Measure(font));
            // Without word timing every row lights as the line starts, as on the Mac.
            if (line.Words == null) pieces = [.. pieces.Select(p => p with { Start = line.Start })];
            foreach (var piece in pieces)
            {
                var text = new TextBlock
                {
                    Text = piece.Text, FontFamily = font, FontSize = _fontSize, FontWeight = Fonts.BoldWeight,
                    Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.NoWrap,
                };
                // The unsung text is dimmed by a wrapper: the sweep takes the text's shape, which
                // would be dim too if the text itself were.
                var dim = new Grid { Opacity = 0.35 };
                dim.Children.Add(text);
                var host = new Grid { IsHitTestVisible = false };
                var row = new Grid { HorizontalAlignment = _settings.LyricsCentered ? HorizontalAlignment.Center : HorizontalAlignment.Left };
                row.Children.Add(dim);
                row.Children.Add(host);
                var sweep = new SweepPainter(text, host) { Color = Colors.White, Effect = _settings.LyricsEffect };
                view.Rows.Add((piece, Math.Ceiling(Measure(font)(piece.Text)) + 1, sweep));
                view.Panel.Children.Add(row);
            }
        }
        else
        {
            Add(view, new TextBlock
            {
                Text = line.Text, FontFamily = font, FontSize = _fontSize, FontWeight = Fonts.BoldWeight,
                Foreground = new SolidColorBrush(Colors.White), TextWrapping = TextWrapping.Wrap, TextAlignment = align,
            }, soft: true);
        }
        if (_settings.ShowPronunciation && line.Pronunciation is { } p)
            Add(view, Extra(p.Text, Math.Round(_fontSize * 0.5), current ? 0.7 : 0.3, song, align), soft: !current);
        if (_settings.ShowTranslations && line.Translation is { } tr)
            Add(view, Extra(tr, Math.Round(_fontSize * 0.6), current ? 0.75 : 0.3, song, align), soft: !current);
        ApplyDistance(view);
    }

    /// <summary>Adds a text to a line, able to soften (blur) unless the line is being sung.</summary>
    static void Add(LineView view, TextBlock text, bool soft)
    {
        if (!soft)
        {
            view.Panel.Children.Add(text);
            return;
        }
        var softText = new SoftText(text);
        view.Soft.Add(softText);
        view.Panel.Children.Add(softText.Element);
    }

    static TextBlock Extra(string text, double size, double opacity, LyricLanguage? song, TextAlignment align) => new()
    {
        Text = text, FontFamily = Fonts.For(text, song), FontSize = size, FontWeight = Fonts.SemiboldWeight,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(opacity * 255), 255, 255, 255)),
        TextWrapping = TextWrapping.Wrap, TextAlignment = align,
    };

    /// <summary>
    /// The line being sung is full size and sharp; the others shrink a little, and fade and blur more
    /// the further they are (as on the Mac: up to 8 points of blur). Unsynced lyrics are all plain.
    /// </summary>
    void ApplyDistance(LineView view)
    {
        bool synced = _model.Lyrics is { IsSynced: true };
        bool current = synced && view.Line.Id == _current;
        int distance = Math.Abs(view.Line.Id - (_current ?? -1));
        view.Panel.Opacity = !synced ? 0.85 : current ? 1 : 0.35 * Math.Max(0.45, 1 - (distance - 1) * 0.12);
        view.Panel.Scale = current || !synced ? Vector3.One : new Vector3(0.96f, 0.96f, 1);
        double blur = !synced || current || _current == null || !_settings.LyricsBlurDistant ? 0 : Math.Min(distance * 1.2, 8);
        foreach (var soft in view.Soft) soft.Blur(blur);
    }

    Func<string, double> Measure(FontFamily font)
    {
        if (Measurer.FontFamily != font || Measurer.FontSize != _fontSize)
        {
            Measurer.FontFamily = font;
            Measurer.FontSize = _fontSize;
            Measurer.FontWeight = Fonts.BoldWeight;
            _widths.Clear();
        }
        return text =>
        {
            if (_widths.TryGetValue(text, out var w)) return w;
            Measurer.Text = text;
            Measurer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            w = Measurer.DesiredSize.Width;
            if (_widths.Count > 400) _widths.Clear();
            return _widths[text] = w;
        };
    }

    /// <summary>Redraws the old and new sung lines, refades the rest and scrolls to the new one.</summary>
    void MoveToCurrent(bool animate)
    {
        var lyrics = _model.Lyrics;
        int? index = lyrics is { IsSynced: true } ? _model.CurrentIndex : null;
        if (index is int n && n >= _views.Count) index = null;
        if (index == _current && animate) return;
        var old = _current;
        _current = index;
        if (old is int o && o < _views.Count) RenderLine(_views[o], current: false);
        if (index is int i) RenderLine(_views[i], current: true);
        foreach (var view in _views) ApplyDistance(view);
        AnimateCurrent();
        ScrollToCurrent(animate);
    }

    /// <summary>Puts the sung line a third of the way down.</summary>
    void ScrollToCurrent(bool animate)
    {
        if (_current is not int target || target >= _views.Count) return;
        Lines.UpdateLayout();
        // Before the window's first layout the viewport has no height yet; Relayout scrolls again.
        double viewport = Scroller.ViewportHeight > 0 ? Scroller.ViewportHeight : Scroller.ActualHeight;
        var top = _views[target].Panel.TransformToVisual(Lines).TransformPoint(new Point(0, 0)).Y;
        Scroller.ScrollTo(0, Math.Max(0, top - viewport * 0.3),
            new ScrollingScrollOptions(animate ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled, ScrollingSnapPointsMode.Ignore));
    }

    /// <summary>Scrolls to the next line with a soft, spring-like ease (the Mac's spring), not Windows' quick default.</summary>
    void OnScrollAnimationStarting(ScrollView sender, ScrollingScrollAnimationStartingEventArgs args)
    {
        if (args.Animation is not Microsoft.UI.Composition.Vector3KeyFrameAnimation stock) return;
        var compositor = stock.Compositor;
        var eased = compositor.CreateVector3KeyFrameAnimation();
        eased.InsertKeyFrame(1, new Vector3((float)args.EndPosition.X, (float)args.EndPosition.Y, 0),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.25f, 1)));
        eased.Duration = TimeSpan.FromSeconds(0.6);
        args.Animation = eased;
    }

    void AnimateCurrent()
    {
        if (_current is not int i || i >= _views.Count) return;
        double now = _model.Position();
        var font = Fonts.For(_views[i].Line.Text, _model.Lyrics is { } l ? Romanizer.SongLanguage(l) : null);
        var measure = Measure(font);
        foreach (var (piece, width, sweep) in _views[i].Rows)
            sweep.Animate(piece, width, now, _model.IsPlaying, measure);
    }

    /// <summary>
    /// Where the lyrics are the point (wide windows and full screen, while playing), the controls
    /// fade once the pointer rests, as on the Mac, and come back when it moves.
    /// </summary>
    void UpdateControls()
    {
        bool hide = _settings.LyricsAutoHide && _wide && _model.IsPlaying && _pointerIdle;
        TopButtons.Opacity = BigProgressHost.Opacity = BigTransportHost.Opacity = hide ? 0 : 1;
    }

    // ---- Buttons and keys ----

    void OnTranslation(object sender, RoutedEventArgs e) => _settings.ShowTranslations = !_settings.ShowTranslations;
    void OnPronunciation(object sender, RoutedEventArgs e) => _settings.ShowPronunciation = !_settings.ShowPronunciation;
    void OnChooseLyrics(object sender, RoutedEventArgs e) => _chooseLyrics();

    void OnFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

    void ToggleFullScreen() =>
        AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);

    void UpdateFullScreenIcon()
    {
        bool full = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        FullScreenIcon.Glyph = full ? "\uE73F" : "\uE740"; // Back to window / Full screen
        TitleBar.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape when AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen:
                AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Space:
                _ = _model.PlayPause();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Keeps the toggle buttons showing the settings.</summary>
    void SyncButtons()
    {
        TranslationButton.Opacity = _settings.ShowTranslations ? 1 : 0.5;
        PronunciationButton.Opacity = _settings.ShowPronunciation ? 1 : 0.5;
    }
}
