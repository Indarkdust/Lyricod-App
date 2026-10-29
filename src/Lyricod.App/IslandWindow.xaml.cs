using System.ComponentModel;
using System.Numerics;
using Lyricod.Core;
using Lyricod.Core.Romanization;
using Lyricod.Platform;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;

namespace Lyricod;

/// <summary>
/// Lyricod Island, like the macOS app's: a black pill at the top centre of the screen with the
/// cover, the line being sung (sweeping in the album's colour) and six bars that move to the
/// player's sound while music plays.
/// The pill fits the current piece and its second row. While
/// the pointer is over it, it takes clicks and (by default) grows into a mini player; otherwise
/// it's click-through.
/// </summary>
public sealed partial class IslandWindow : Window
{
    double LayoutScale => _settings.IslandSize switch
    {
        IslandSize.Small => 0.85,
        IslandSize.Large => 1.2,
        IslandSize.ExtraLarge => 1.4,
        _ => 1,
    };
    // The cover sits as far from the side as from the bottom, and its corner is the island's corner
    // minus that gap, so the two corners share a centre (as on the Mac: 12 and 6).
    const double CoverInset = 6, CoverSize = 22, CoverRadius = 6, PillRadius = CoverInset + CoverRadius;
    double RowHeight => (CoverSize + 2 * CoverInset) * LayoutScale;
    double SecondHeight => 18 * LayoutScale;
    double ExpandedHeight => 84 * LayoutScale;
    double SideWidth => (CoverInset + CoverSize + 10) * LayoutScale; // inset, cover (or bars) and gap on each side
    double MinText => 80 * LayoutScale; // a short line still gets a pill, not a stub
    double MaxText => Math.Min(_settings.IslandMaxTextWidth * LayoutScale,
        Math.Max(MinText, _screenWidth - 2 * SideWidth - 20 * LayoutScale));
    double ExpandedMinWidth => 460 * LayoutScale;
    double BarHeight => 14 * LayoutScale;
    double WindowWidth => Math.Max(2 * SideWidth + MaxText, ExpandedMinWidth) + 20 * LayoutScale;
    double WindowHeight => RowHeight + SecondHeight + ExpandedHeight + 12 * LayoutScale;
    double _screenWidth = double.PositiveInfinity;

    readonly PlaybackModel _model;
    readonly Settings _settings;
    readonly Overlay _overlay;
    readonly SweepPainter _sweep;
    readonly TransportButtons _transport;
    readonly ProgressLine _progress;
    readonly DispatcherQueueTimer _pieceTimer, _pointerTimer, _progressTimer, _barTimer, _revealTimer, _pauseTimer;
    /// <summary>Hidden because the music stopped (Settings › Lyricod Island › Hide when paused).</summary>
    bool _pausedAway;
    readonly List<Rectangle> _bars = [];
    readonly List<Visual> _barVisuals = [];
    readonly AudioMeter _meter = new();
    readonly Dictionary<(string Text, string Font, double Size, ushort Weight), double> _widths = [];

    List<LyricLine> _pieces = [];
    LyricLine? _piece;
    double _pieceWidth;
    double _textWidth = 80, _secondWidth, _secondOpacity = 0.6;
    double _widthTarget = double.NaN, _heightTarget = double.NaN;
    Storyboard? _widthAnimation, _heightAnimation, _lineAnimation;
    bool _waitingForWidth, _widthAnimating, _transitionPending;
    bool _shown, _hovered, _expanded, _barsRunning, _closed;
    IslandHover _hoverAction;
    (double X, double Y)? _lastOutside;
    int _barRate;
    /// <summary>The player the meter listens to, or null when it's off.</summary>
    string? _meteredApp;
    /// <summary>A lyric line has shown for this song (the next ones get the transition).</summary>
    bool _lineShown;
    double _lastInside;

    public IslandWindow(PlaybackModel model, Settings settings, Action openLyrics, Action openSettings)
    {
        InitializeComponent();
        _model = model;
        _settings = settings;
        _overlay = new Overlay(this);
        _overlay.SetClickThrough(true);
        _sweep = new SweepPainter(Lyric, LyricSweep) { Effect = settings.IslandEffect };
        _transport = new TransportButtons(model, 13);
        TransportHost.Children.Add(_transport);
        _progress = new ProgressLine(model);
        ProgressHost.Children.Add(_progress);
        LyricsButton.Click += (_, _) => openLyrics();
        SettingsButton.Click += (_, _) => openSettings();
        ToolTipService.SetToolTip(LyricsButton, L.Get("LyricsWindowTitle"));
        ToolTipService.SetToolTip(SettingsButton, L.Get("SettingsTitle"));
        // One bar per band of the meter, sized as on the Mac (2 wide, 1.5 apart, 3 to 14 tall).
        for (int i = 0; i < AudioMeter.BandCount; i++)
        {
            var bar = new Rectangle { Width = 2, Height = BarHeight, RadiusX = 1, RadiusY = 1, Fill = new SolidColorBrush(Colors.White) };
            _bars.Add(bar);
            Bars.Children.Add(bar);
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.CenterPoint = new Vector3(1, (float)BarHeight / 2, 0);
            visual.Scale = new Vector3(1, 3 / 14f, 1);
            _barVisuals.Add(visual);
        }

        _pieceTimer = DispatcherQueue.CreateTimer();
        _pieceTimer.IsRepeating = false;
        _pieceTimer.Tick += (_, _) => { try { ShowPiece(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } };
        // The pointer is checked often only near the island; a few times a second otherwise.
        _pointerTimer = DispatcherQueue.CreateTimer();
        _pointerTimer.Interval = TimeSpan.FromMilliseconds(250);
        _pointerTimer.Tick += (_, _) => { try { CheckPointer(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } };
        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromSeconds(0.5);
        _progressTimer.Tick += (_, _) => { try { _progress.Update(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } };
        _barTimer = DispatcherQueue.CreateTimer();
        _barTimer.Tick += (_, _) => { try { DrawBars(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } };
        // Text hidden while the pill widens shows again by this at the latest.
        _revealTimer = DispatcherQueue.CreateTimer();
        _revealTimer.IsRepeating = false;
        _revealTimer.Interval = TimeSpan.FromSeconds(0.35);
        _revealTimer.Tick += (_, _) =>
        {
            try { if (_waitingForWidth && !_closed) { _waitingForWidth = false; RevealText(); } }
            catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
        };

        // Hide when paused: only once the music has stayed stopped a moment, so the gap players
        // report between songs doesn't make the island blink.
        _pauseTimer = DispatcherQueue.CreateTimer();
        _pauseTimer.IsRepeating = false;
        _pauseTimer.Interval = TimeSpan.FromSeconds(2.5);
        _pauseTimer.Tick += (_, _) =>
        {
            try
            {
                if (_closed || !PausedLongEnough) return;
                if (_hovered) { _pauseTimer.Start(); return; } // not from under the pointer
                _pausedAway = true;
                UpdateVisibility();
            }
            catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
        };

        _model.Changed += OnModelChanged;
        Fonts.Changed += OnFontChanged;
        _settings.PropertyChanged += OnSettingChanged;
        // Nothing may touch the window once it's gone (the pointer check reads its position).
        Closed += (_, _) =>
        {
            _closed = true;
            _model.Changed -= OnModelChanged;
            Fonts.Changed -= OnFontChanged;
            _settings.PropertyChanged -= OnSettingChanged;
            _pieceTimer.Stop();
            _pointerTimer.Stop();
            _progressTimer.Stop();
            _barTimer.Stop();
            _revealTimer.Stop();
            _pauseTimer.Stop();
            _widthAnimation?.Stop();
            _heightAnimation?.Stop();
            _lineAnimation?.Stop();
            _meter.Dispose();
        };
        ApplyLayout();
        Place();
        _ = ApplyArtwork();
        UpdateSong();
        Render();
        UpdatePaused();
    }

    // ---- Window ----

    /// <summary>Top centre of the main display.</summary>
    void Place()
    {
        double k = _overlay.Scale;
        var area = DisplayArea.Primary.OuterBounds;
        _screenWidth = area.Width / k;
        // Keep room for an old, larger layout until its shrink animation finishes.
        double width = Math.Max(WindowWidth, Pill.Width + 20 * LayoutScale);
        double height = Math.Max(WindowHeight, Pill.Height + 12 * LayoutScale);
        var size = new SizeInt32((int)Math.Ceiling(width * k), (int)Math.Ceiling(height * k));
        int x = area.X + (area.Width - size.Width) / 2;
        if (AppWindow.Size.Width != size.Width || AppWindow.Size.Height != size.Height || AppWindow.Position.X != x || AppWindow.Position.Y != area.Y)
            AppWindow.MoveAndResize(new RectInt32(x, area.Y, size.Width, size.Height));
    }

    bool PausedLongEnough => _settings.IslandHideWhenPaused && _model.Track != null && !_model.IsPlaying;

    /// <summary>Starts the wait before hiding when the music stops; brings the island back when it plays.</summary>
    void UpdatePaused()
    {
        if (PausedLongEnough)
        {
            if (!_pausedAway && !_pauseTimer.IsRunning) _pauseTimer.Start();
            return;
        }
        _pauseTimer.Stop();
        if (!_pausedAway) return;
        _pausedAway = false;
        UpdateVisibility();
    }

    void UpdateVisibility()
    {
        bool visible = _settings.ShowIsland && _model.IsSong && !_pausedAway;
        if (visible == _shown) return;
        _shown = visible;
        if (visible)
        {
            Place();
            AppWindow.Show(false);
            _overlay.StripFrame();
            _pointerTimer.Start();
            AnimateShown(true);
        }
        else
        {
            _pointerTimer.Stop();
            _progressTimer.Stop();
            SetHovered(false);
            _lastOutside = null;
            AnimateShown(false, () => { if (!_shown && !_closed) AppWindow.Hide(); });
        }
        UpdateBars();
    }

    /// <summary>Comes and goes like the Mac's: a fade with a slight shrink toward the top edge.</summary>
    void AnimateShown(bool show, Action? done = null)
    {
        double seconds = show ? 0.3 : 0.35;
        if (Pill.RenderTransform is not ScaleTransform scale) Pill.RenderTransform = scale = new ScaleTransform();
        scale.CenterX = Pill.ActualWidth > 0 ? Pill.ActualWidth / 2 : Pill.Width / 2;
        scale.CenterY = 0;
        if (show && Root.Opacity < 0.05) scale.ScaleX = scale.ScaleY = 0.9;
        var story = new Storyboard();
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var grow = new DoubleAnimation
            {
                To = show ? 1 : 0.9, Duration = TimeSpan.FromSeconds(seconds),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(grow, scale);
            Storyboard.SetTargetProperty(grow, property);
            story.Children.Add(grow);
        }
        story.Begin();
        Fade(Root, show ? 1 : 0, seconds, done);
    }

    static void Fade(UIElement element, double to, double seconds, Action? done = null, double delay = 0)
    {
        var animation = new DoubleAnimation
        {
            To = to, Duration = TimeSpan.FromSeconds(seconds), BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var story = new Storyboard { Children = { animation } };
        if (done != null) story.Completed += (_, _) => { try { done(); } catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); } };
        story.Begin();
    }

    // ---- Hover ----

    void CheckPointer()
    {
        if (!_shown || _closed || !Win32.GetCursorPos(out var p)) return;
        double k = _overlay.Scale;
        var pos = AppWindow.Position;
        double width = Pill.ActualWidth * k, height = Pill.ActualHeight * k;
        double left = pos.X + (AppWindow.Size.Width - width) / 2, top = pos.Y;
        bool inside = p.X >= left - 2 && p.X <= left + width + 2 && p.Y >= top && p.Y <= top + height + 2;
        bool near = p.Y <= top + height + 120 && p.X >= left - 160 && p.X <= left + width + 160;
        _pointerTimer.Interval = TimeSpan.FromMilliseconds(near || _hovered ? 33 : 250);
        double now = PlaybackModel.Now;
        if (inside)
        {
            _lastInside = now;
            if (!_hovered)
            {
                // Use the same two-pixel margin as the hover check.
                bool fromBelow = IslandEntry.IsFromBelow(p.X, p.Y, left - 2, top, width + 4, height + 2, _lastOutside);
                SetHovered(true, fromBelow);
            }
        }
        else
        {
            _lastOutside = (p.X, p.Y);
            if (_hovered && now - _lastInside > 0.3) SetHovered(false); // a moment's grace when brushing past
        }
    }

    void SetHovered(bool hovered, bool fromBelow = false)
    {
        if (hovered == _hovered) return;
        _hovered = hovered;
        if (hovered)
            _hoverAction = _settings.IslandHover == IslandHover.ByDirection
                ? (fromBelow ? IslandHover.Hide : IslandHover.Expand) : _settings.IslandHover;
        bool expand = hovered && _hoverAction == IslandHover.Expand;
        // Takes clicks only while expanded; otherwise whatever is underneath stays usable.
        _overlay.SetClickThrough(!expand);
        // "Hide": out of the way while the pointer is over it, so what's underneath can be seen too.
        if (_hoverAction == IslandHover.Hide) Fade(Pill, hovered ? 0 : 1, hovered ? 0.16 : 0.28, delay: hovered ? 0 : 0.12);
        if (expand != _expanded)
        {
            _expanded = expand;
            // Keep the lyric in place; the player has its own cover.
            Fade(ArtFrame, expand ? 0 : 1, expand ? 0.18 : 0.28);
            Fade(Bars, expand ? 0 : 1, expand ? 0.18 : 0.28);
            if (expand)
            {
                Expanded.Visibility = Visibility.Visible;
                _progress.Update();
                _transport.Update();
                if (_model.IsPlaying) _progressTimer.Start();
            }
            else _progressTimer.Stop();
            // As on the Mac: the mini player fades in once the pill has mostly grown, and goes at once on collapse.
            Fade(Expanded, expand ? 1 : 0, expand ? 0.18 : 0.08, () => { if (!_expanded) Expanded.Visibility = Visibility.Collapsed; },
                delay: expand ? 0.12 : 0);
            Resize();
        }
    }

    // ---- Size ----

    /// <summary>Whether the current line has something under it (its translation, pronunciation or
    /// the next line, as chosen); the second row and its height come and go with it.</summary>
    bool HasSecondRow { get; set; }

    void ApplyLayout()
    {
        double s = LayoutScale;
        LyricRow.Height = new GridLength(RowHeight);
        SecondRow.Height = new GridLength(HasSecondRow ? SecondHeight : 0);
        MainRow.Padding = new Thickness(CoverInset * s, 0, CoverInset * s, 0);
        MainRow.ColumnSpacing = 10 * s;
        CoverColumn.Width = BarsColumn.Width = new GridLength(CoverSize * s);
        ArtFrame.Width = ArtFrame.Height = CoverSize * s;
        ArtFrame.CornerRadius = new CornerRadius(CoverRadius * s);
        Bars.Height = BarHeight;
        Bars.Spacing = 1.5 * s;
        for (int i = 0; i < _bars.Count; i++)
        {
            _bars[i].Width = 2 * s;
            _bars[i].Height = BarHeight;
            _bars[i].RadiusX = _bars[i].RadiusY = s;
            _barVisuals[i].CenterPoint = new Vector3((float)s, (float)BarHeight / 2, 0);
        }
        Second.FontSize = 11 * s;
        Second.Margin = new Thickness(SideWidth, -4 * s, SideWidth, 4 * s);
        Expanded.Padding = new Thickness(18 * s, 6 * s, 18 * s, 10 * s);
        Expanded.Spacing = 6 * s;
        PlayerRow.ColumnSpacing = 10 * s;
        BigArtFrame.Width = BigArtFrame.Height = 34 * s;
        BigArtFrame.CornerRadius = new CornerRadius(7 * s);
        SongLabels.Spacing = s;
        SongTitle.FontSize = 13 * s;
        SongArtist.FontSize = 12 * s;
        PlayerDivider.Width = s;
        PlayerDivider.Height = 18 * s;
        foreach (var button in new[] { LyricsButton, SettingsButton })
        {
            button.Padding = new Thickness(8 * s, 6 * s, 8 * s, 6 * s);
            button.CornerRadius = new CornerRadius(4 * s);
            if (button.Content is FontIcon icon) icon.FontSize = 13 * s;
        }
        _transport.SetLayoutScale(s, 13);
        _progress.SetLayoutScale(s);
        _widths.Clear();
    }

    /// <summary>Fits the displayed text, keeping room for the mini player while expanded.</summary>
    void Resize()
    {
        double required = _textWidth + 2 * SideWidth;
        double width = _expanded ? Math.Max(required, ExpandedMinWidth) : required;
        double height = RowHeight + (HasSecondRow ? SecondHeight : 0) + (_expanded ? ExpandedHeight : 0);
        if (width != _widthTarget)
        {
            double from = Pill.Width;
            _widthAnimation?.Stop();
            Pill.Width = from;
            _widthTarget = width;
            var story = Animate(Pill, "Width", width, from, width > from ? 0.18 : 0.5);
            _widthAnimation = story;
            _widthAnimating = true;
            story.Completed += (_, _) =>
            {
                try
                {
                    if (_closed || !ReferenceEquals(_widthAnimation, story)) return;
                    _widthAnimating = false;
                    _waitingForWidth = false;
                    RevealText();
                    Place();
                }
                catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
            };
            story.Begin();
        }
        // Don't expose new text inside a pill that's still too narrow: hide it only while the pill
        // is widening (an animation's end can land a hair short of its target, so allow half a
        // pixel), and never for long. Shrinking is safe: the incoming text already fits.
        _waitingForWidth = _widthAnimating && Pill.Width < required - 0.5;
        if (_waitingForWidth)
        {
            _lineAnimation?.Stop();
            LyricBox.Opacity = Second.Opacity = 0;
            _revealTimer.Stop();
            _revealTimer.Start();
        }
        if (height != _heightTarget)
        {
            double from = Pill.Height;
            _heightAnimation?.Stop();
            Pill.Height = from;
            _heightTarget = height;
            var story = Animate(Pill, "Height", height, from);
            _heightAnimation = story;
            story.Completed += (_, _) =>
            {
                try { if (!_closed && ReferenceEquals(_heightAnimation, story)) Place(); }
                catch (Exception e) { System.Diagnostics.Debug.WriteLine(e); }
            };
            story.Begin();
        }
        double radius = (_expanded ? 20 : PillRadius) * LayoutScale;
        Pill.CornerRadius = new CornerRadius(0, 0, radius, radius);
        if (!_waitingForWidth) RevealText();
    }

    /// <summary>A fast start and a soft settle, without overshooting.</summary>
    static Storyboard Animate(FrameworkElement element, string property, double to, double from, double seconds = 0.5)
    {
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = TimeSpan.FromSeconds(seconds), EnableDependentAnimation = true,
            EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, element);
        Storyboard.SetTargetProperty(animation, property);
        return new Storyboard { Children = { animation } };
    }

    void FitText()
    {
        _textWidth = Math.Clamp(Math.Ceiling(Math.Max(_pieceWidth, _secondWidth)), MinText, MaxText);
        Second.MaxWidth = _textWidth;
        Resize();
    }

    void RevealText()
    {
        LyricBox.Opacity = 1;
        Second.Opacity = _secondOpacity;
        if (_transitionPending)
        {
            _transitionPending = false;
            MoveIn();
        }
    }

    double Measure(string text, FontFamily font, double size, Windows.UI.Text.FontWeight weight)
    {
        var key = (text, font.Source, size, weight.Weight);
        if (_widths.TryGetValue(key, out var w)) return w;
        Measurer.FontFamily = font;
        Measurer.FontSize = size;
        Measurer.FontWeight = weight;
        Measurer.Text = text;
        Measurer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        w = Measurer.DesiredSize.Width;
        if (_widths.Count > 2000) _widths.Clear();
        return _widths[key] = w;
    }

    double MeasureLyric(string text) => Measure(text, Lyric.FontFamily, Lyric.FontSize, Lyric.FontWeight);

    // ---- Model ----

    void OnFontChanged()
    {
        try
        {
            _widths.Clear();
            Render();
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("font refresh: " + e); }
    }

    void OnModelChanged(PlaybackModel.Change change)
    {
        UpdatePaused();
        if (change.HasFlag(PlaybackModel.Change.Track)) UpdateSong();
        if (change.HasFlag(PlaybackModel.Change.Artwork)) _ = ApplyArtwork();
        if (change.HasFlag(PlaybackModel.Change.Track) || change.HasFlag(PlaybackModel.Change.Lyrics) || change.HasFlag(PlaybackModel.Change.Line))
            Render();
        else if (change.HasFlag(PlaybackModel.Change.Clock))
        {
            ShowPiece();
            _transport.Update();
            _progress.Update();
            if (_expanded && _model.IsPlaying) _progressTimer.Start(); else _progressTimer.Stop();
            UpdateBars();
        }
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.ShowIsland):
                UpdateVisibility();
                break;
            case nameof(Settings.IslandAlbumColour) or nameof(Settings.IslandColor):
                ApplyColour();
                break;
            case nameof(Settings.IslandSecondLine):
                Render();
                break;
            case nameof(Settings.IslandHideWhenPaused):
                UpdatePaused();
                break;
            case nameof(Settings.IslandHover):
                SetHovered(false);
                Pill.Opacity = 1;
                break;
            case nameof(Settings.IslandSize) or nameof(Settings.IslandMaxTextWidth):
                ApplyLayout();
                Place();
                Render();
                _ = ApplyArtwork();
                break;
            case nameof(Settings.IslandWeight):
                _widths.Clear();
                Render();
                break;
            case nameof(Settings.IslandEffect):
                _sweep.Effect = _settings.IslandEffect;
                ShowPiece();
                break;
            case nameof(Settings.IslandAudioBars) or nameof(Settings.FrameRate):
                UpdateBars();
                break;
        }
    }

    void UpdateSong()
    {
        _lineShown = false;
        SongTitle.Text = _model.Track?.Title ?? "";
        SongArtist.Text = _model.Track?.Artist ?? "";
        _transport.Update();
        _progress.Update();
        UpdateVisibility();
        UpdateBars(); // another player may be playing now
    }

    async Task ApplyArtwork()
    {
        var bytes = _model.Artwork;
        double scale = LayoutScale;
        var small = await Images.FromBytes(bytes, (int)Math.Ceiling(44 * scale));
        var big = await Images.FromBytes(bytes, (int)Math.Ceiling(68 * scale));
        if (_closed || !ReferenceEquals(bytes, _model.Artwork) || scale != LayoutScale) return;
        Art.Source = small;
        BigArt.Source = big;
        ApplyColour();
    }

    /// <summary>The album's colour, or the island's chosen colour.</summary>
    Windows.UI.Color LyricColour =>
        _settings.IslandAlbumColour && _model.ArtworkColor is { } c ? c : DesktopLyricsWindow.ParseColor(_settings.IslandColor);

    void ApplyColour()
    {
        var colour = LyricColour;
        _sweep.Color = colour;
        var brush = new SolidColorBrush(colour);
        foreach (var bar in _bars) bar.Fill = brush;
        _progress.BarBrush = brush;
    }

    // ---- Lyric ----

    Windows.UI.Text.FontWeight LyricWeight => Fonts.Weight(_settings.IslandWeight);

    void Render()
    {
        UpdateVisibility();
        var lyrics = _model.Lyrics;
        var line = lyrics is { IsSynced: true } ? _model.CurrentLine : null;
        var song = lyrics != null ? Romanizer.SongLanguage(lyrics) : null;
        var under = line != null ? _settings.IslandSecondLine.RowUnder(_model.CurrentIndex, lyrics) : null;
        HasSecondRow = under != null;
        SecondRow.Height = new GridLength(HasSecondRow ? SecondHeight : 0);
        Second.Visibility = HasSecondRow ? Visibility.Visible : Visibility.Collapsed;
        Second.Text = under?.Text ?? "";
        _secondOpacity = under is { IsExtra: false } ? 0.4 : 0.6;
        if (under is { } u)
        {
            Second.FontFamily = Fonts.For(u.Text, song);
            Second.FontWeight = Fonts.SemiboldWeight;
        }
        _secondWidth = under is { } extra ? Measure(extra.Text, Second.FontFamily, Second.FontSize, Second.FontWeight) : 0;

        if (line == null)
        {
            // No line to sing: the song.
            _pieces = [];
            _piece = null;
            _pieceTimer.Stop();
            Lyric.Text = _model.IsSong && _model.Track is { } t ? $"{t.Title} · {t.Artist}" : "";
            Lyric.FontFamily = Fonts.For(Lyric.Text, null);
            Lyric.FontSize = 13 * LayoutScale;
            Lyric.FontWeight = Fonts.SemiboldWeight;
            LyricDim.Opacity = 0.6;
            Lyric.MaxWidth = MaxText;
            Lyric.TextTrimming = TextTrimming.CharacterEllipsis;
            _pieceWidth = Math.Min(Math.Ceiling(MeasureLyric(Lyric.Text)) + 1, MaxText);
            LyricBox.Width = _pieceWidth;
            _transitionPending = false;
            _lineAnimation?.Stop();
            if (LyricBox.RenderTransform is TranslateTransform slide) slide.Y = 0;
            _sweep.Set(0, 1);
            FitText();
        }
        else
        {
            var font = Fonts.For(line.Text, song);
            Lyric.FontFamily = font;
            Lyric.FontSize = 15 * LayoutScale;
            Lyric.FontWeight = LyricWeight;
            LyricDim.Opacity = 0.4;
            Lyric.MaxWidth = double.PositiveInfinity;
            Lyric.TextTrimming = TextTrimming.None;
            _pieces = LinePieces.Split(line, MaxText - 1, MeasureLyric);
            _piece = null;
            ShowPiece();
        }

        UpdateBars();
    }

    void ShowPiece()
    {
        _pieceTimer.Stop();
        if (_pieces.Count == 0) return;
        double now = _model.Position();
        var piece = LinePieces.Current(_pieces, now)!;
        if (!ReferenceEquals(piece, _piece))
        {
            _piece = piece;
            Lyric.Text = piece.Text;
            Lyric.FontSize = 15 * LayoutScale;
            double natural = MeasureLyric(piece.Text);
            // A single unbreakable word can exceed the split limit. Lay it out smaller.
            if (natural + 1 > MaxText) Lyric.FontSize *= (MaxText - 1) / natural;
            _pieceWidth = Math.Min(Math.Ceiling(MeasureLyric(piece.Text)) + 1, MaxText);
            LyricBox.Width = _pieceWidth;
            _lineAnimation?.Stop();
            if (LyricBox.RenderTransform is TranslateTransform slide) slide.Y = 0;
            // The song's first line just appears; each one after comes in with the transition.
            _transitionPending = _lineShown;
            _lineShown = true;
            FitText();
        }
        _sweep.Animate(piece, _pieceWidth, now, _model.IsPlaying, MeasureLyric);
        int index = _pieces.IndexOf(piece);
        if (_model.IsPlaying && index + 1 < _pieces.Count)
        {
            _pieceTimer.Interval = TimeSpan.FromSeconds(Math.Max(_pieces[index + 1].Start - now, 0) + 0.005);
            _pieceTimer.Start();
        }
    }

    /// <summary>The next line comes in as the island's settings ask: at once, sliding up, or fading.</summary>
    void MoveIn()
    {
        var transition = _settings.IslandTransition;
        if (transition == LineTransition.Instant) return;
        var story = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromSeconds(0.28), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, LyricBox);
        Storyboard.SetTargetProperty(fade, "Opacity");
        story.Children.Add(fade);
        if (transition == LineTransition.Slide)
        {
            if (LyricBox.RenderTransform is not TranslateTransform) LyricBox.RenderTransform = new TranslateTransform();
            var slide = new DoubleAnimation { From = 10 * LayoutScale, To = 0, Duration = TimeSpan.FromSeconds(0.32), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(slide, LyricBox.RenderTransform);
            Storyboard.SetTargetProperty(slide, "Y");
            story.Children.Add(slide);
        }
        _lineAnimation = story;
        story.Begin();
    }

    // ---- Bars ----

    /// <summary>
    /// As on the Mac: while the island shows and music plays, the bars are redrawn at the
    /// animation rate from Settings, each frame gliding into the next on the compositor. They
    /// follow the player's sound when "Bars follow the music" is on and Windows lets Lyricod
    /// listen to it (Windows 11, or Server 2022), and bob on their own otherwise. Paused, they
    /// settle low. The player is listened to only while the bars move.
    /// </summary>
    void UpdateBars()
    {
        bool running = _shown && _model.IsPlaying;
        string? target = running && _settings.IslandAudioBars ? _model.NowPlaying?.AppId : null;
        if (target != _meteredApp)
        {
            _meteredApp = target;
            if (target != null) _meter.Start(target); else _meter.Stop();
        }

        int rate = (int)_settings.FrameRate;
        if (running == _barsRunning && rate == _barRate) return;
        _barsRunning = running;
        _barRate = rate;
        double interval = 1.0 / rate;
        Glide(running ? interval * 1.1 : 0.35, linear: running);
        if (running)
        {
            _barTimer.Interval = TimeSpan.FromSeconds(interval);
            _barTimer.Start();
        }
        else
        {
            _barTimer.Stop();
            DrawBars();
        }
    }

    /// <summary>A new height eases in over <paramref name="seconds"/>, so the compositor draws the
    /// frames in between (smooth at the display's rate, no extra work here).</summary>
    void Glide(double seconds, bool linear)
    {
        var compositor = _barVisuals[0].Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Scale";
        animation.InsertExpressionKeyFrame(1, "this.FinalValue", linear
            ? compositor.CreateLinearEasingFunction()
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0.58f, 1)));
        animation.Duration = TimeSpan.FromSeconds(seconds);
        var animations = compositor.CreateImplicitAnimationCollection();
        animations["Scale"] = animation;
        foreach (var visual in _barVisuals) visual.ImplicitAnimations = animations;
    }

    void DrawBars()
    {
        bool listening = _barsRunning && _settings.IslandAudioBars && !_meter.Failed;
        var levels = listening ? _meter.Levels() : null;
        double t = PlaybackModel.Now;
        for (int i = 0; i < _barVisuals.Count; i++)
        {
            double phase = t * (3.1 + i * 1.3) + i * 1.7;
            // Listening but nothing to hear (between songs, or the meter is still starting): stay low
            // rather than make up a bounce. The bounce is only for when listening can't work.
            double height = !_barsRunning ? 3
                : levels != null ? 3 + 11 * Math.Clamp(levels[i], 0, 1)
                : listening ? 3
                : 4 + 10 * Math.Abs(Math.Sin(phase));
            _barVisuals[i].Scale = new Vector3(1, (float)(height / 14), 1);
        }
    }
}
