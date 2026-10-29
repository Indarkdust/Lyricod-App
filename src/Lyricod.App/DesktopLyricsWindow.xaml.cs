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
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using System.Runtime.InteropServices;
using WinUIEx;
using WinUIEx.Messaging;

namespace Lyricod;

/// <summary>
/// The floating lyric strip over every app. The sung part of the line is the sung colour in
/// the text's shape, fading out over a soft edge like the macOS app's; that edge moves on the
/// compositor (no per-frame work in the app), re-planned whenever the line or the clock changes.
/// </summary>
public sealed partial class DesktopLyricsWindow : Window
{
    /// <summary>Strip width in view pixels, like the macOS app.</summary>
    const double StripWidth = 900;
    const double SidePadding = 24;

    readonly PlaybackModel _model;
    readonly Settings _settings;
    readonly nint _hwnd;
    readonly Compositor _compositor;
    readonly SweepPainter _sung;
    readonly DispatcherQueueTimer _pieceTimer;
    readonly Dictionary<string, double> _widths = [];

    List<LyricLine> _pieces = [];
    LyricLine? _piece;
    double _textWidth;
    bool? _shown;
    bool _placing;
    /// <summary>Whether the window has its strip size yet (before that, its size is Windows' default).</summary>
    bool _placed;
    readonly Overlay _overlay;

    public DesktopLyricsWindow(PlaybackModel model, Settings settings)
    {
        InitializeComponent();
        _model = model;
        _settings = settings;
        // A borderless, always-on-top, fully transparent window that never takes focus
        // and stays out of Alt-Tab.
        _overlay = new Overlay(this);
        _hwnd = _overlay.Handle;

        _compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
        _sung = new SweepPainter(Dim, LitHost);
        AttachShadow(Dim, LineShadow);
        AttachShadow(Second, SecondShadow);
        LineHost.RenderTransformOrigin = new Point(0.5, 0.5);
        MoveHint.Text = L.Get("MoveHint");

        _pieceTimer = DispatcherQueue.CreateTimer();
        _pieceTimer.IsRepeating = false;
        _pieceTimer.Tick += (_, _) => ShowPiece();

        _model.Changed += OnModelChanged;
        Fonts.Changed += OnFontChanged;
        _settings.PropertyChanged += OnSettingChanged;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += (_, _) =>
        {
            _model.Changed -= OnModelChanged;
            Fonts.Changed -= OnFontChanged;
            _settings.PropertyChanged -= OnSettingChanged;
            _pieceTimer.Stop();
        };

        ApplyStyle();
        ApplyLock();
        Place();
        Render();
    }

    // ---- Window ----

    /// <summary>Locked: clicks pass through to whatever is underneath. Unlocked: drag to move.</summary>
    void ApplyLock()
    {
        bool locked = _settings.DesktopLocked;
        _overlay.SetClickThrough(locked);
        MoveFrame.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Main line, plus the second line unless there's none, plus padding (view pixels).</summary>
    double StripHeight()
    {
        double f = _settings.DesktopFontSize;
        double second = _settings.SecondLine == SecondLine.None ? 0 : f * 0.6 * 1.25 + 4;
        return Math.Round(f * 1.25 + second + 32);
    }

    /// <summary>Sizes the strip and puts it where the user left it, or at the bottom centre.</summary>
    void Place()
    {
        double scale = Win32.Scale(_hwnd);
        var size = new SizeInt32((int)(StripWidth * scale), (int)(StripHeight() * scale));
        _placing = true;
        try
        {
            if (_settings.DesktopX is int x && _settings.DesktopY is int y && OnSomeScreen(x, y, size))
            {
                // Grow or shrink upward from the bottom edge, so the strip stays where it was put.
                // The first time, the saved spot is the strip's own top-left.
                int bottom = y + (_placed ? AppWindow.Size.Height : size.Height);
                AppWindow.MoveAndResize(new RectInt32(x, bottom - size.Height, size.Width, size.Height));
            }
            else
            {
                var area = DisplayArea.Primary.WorkArea;
                AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2,
                    area.Y + area.Height - size.Height - (int)(40 * scale), size.Width, size.Height));
            }
        }
        finally { _placing = false; _placed = true; }
    }

    static bool OnSomeScreen(int x, int y, SizeInt32 size)
    {
        var area = DisplayArea.GetFromRect(new RectInt32(x, y, size.Width, size.Height), DisplayAreaFallback.None);
        return area != null;
    }

    void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange || _placing || _settings.DesktopLocked) return;
        _settings.DesktopX = sender.Position.X;
        _settings.DesktopY = sender.Position.Y;
    }

    void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_settings.DesktopLocked) return;
        // Let Windows move the window as if its title bar were dragged.
        Win32.ReleaseCapture();
        Win32.SendMessage(_hwnd, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, 0);
    }

    void UpdateVisibility()
    {
        bool visible = _settings.ShowDesktopLyrics && _model.Track != null;
        if (visible == _shown) return;
        _shown = visible;
        if (visible)
        {
            AppWindow.Show(false);
            _overlay.StripFrame();
        }
        else AppWindow.Hide();
    }

    // ---- Text ----

    void ApplyStyle()
    {
        double f = _settings.DesktopFontSize;
        Dim.FontSize = Measurer.FontSize = f;
        Dim.FontWeight = Measurer.FontWeight = Fonts.BoldWeight;
        Second.FontSize = Math.Round(f * 0.6);
        Second.FontWeight = Fonts.SemiboldWeight;
        Second.MaxWidth = StripWidth - 2 * SidePadding;
        _sung.Color = ParseColor(_settings.SungColor);
        _widths.Clear();
    }

    internal static Windows.UI.Color ParseColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return Colors.White;
        return Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>Drawn width of <paramref name="text"/> in the lyric font (view pixels).</summary>
    double Measure(string text)
    {
        if (_widths.TryGetValue(text, out var w)) return w;
        Measurer.Text = text;
        Measurer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        w = Measurer.DesiredSize.Width;
        if (_widths.Count > 500) _widths.Clear();
        _widths[text] = w;
        return w;
    }

    /// <summary>A soft dark shadow under the text, so it reads over any wallpaper.</summary>
    void AttachShadow(TextBlock text, Grid host)
    {
        var shadow = _compositor.CreateDropShadow();
        shadow.Color = Colors.Black;
        shadow.Opacity = 0.85f;
        shadow.BlurRadius = 6;
        shadow.Offset = new Vector3(0, 1, 0);
        shadow.Mask = text.GetAlphaMask();
        var sprite = _compositor.CreateSpriteVisual();
        sprite.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(host, sprite);
        text.SizeChanged += (_, e) => sprite.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
    }

    // ---- Model ----

    void OnFontChanged()
    {
        try
        {
            ApplyStyle();
            Place();
            Render();
        }
        catch (Exception e) { System.Diagnostics.Debug.WriteLine("font refresh: " + e); }
    }

    void OnModelChanged(PlaybackModel.Change change)
    {
        if (change.HasFlag(PlaybackModel.Change.Track) || change.HasFlag(PlaybackModel.Change.Lyrics) || change.HasFlag(PlaybackModel.Change.Line))
            Render();
        else if (change.HasFlag(PlaybackModel.Change.Clock))
            ShowPiece();
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.DesktopLocked):
                ApplyLock();
                break;
            case nameof(Settings.DesktopFontSize) or nameof(Settings.SungColor):
                ApplyStyle();
                Place();
                Render();
                break;
            case nameof(Settings.SecondLine):
                Place();
                Render();
                break;
            case nameof(Settings.ShowDesktopLyrics):
                Render();
                break;
            case nameof(Settings.DesktopY) when _settings.DesktopX == null && _settings.DesktopY == null:
                // "Reset to bottom centre" in Settings.
                Place();
                break;
        }
    }

    /// <summary>Shows the current line (in pieces if it's too wide) and what goes under it.</summary>
    void Render()
    {
        UpdateVisibility();
        var lyrics = _model.Lyrics;
        var line = lyrics is { IsSynced: true } ? _model.CurrentLine : null;
        if (line == null)
        {
            // No line to sing: the song, as on the Mac.
            _pieces = [];
            _piece = null;
            _pieceTimer.Stop();
            Dim.FontSize = Measurer.FontSize = Math.Round(_settings.DesktopFontSize * 0.8);
            Dim.Text = _model.Track is { } t ? (_model.IsSearching ? L.Get("Searching") : $"{t.Title} — {t.Artist}") : "";
            Dim.FontFamily = Measurer.FontFamily = Fonts.For(Dim.Text, null);
            // A long title ends in "…" rather than running off the strip.
            Dim.MaxWidth = StripWidth - 2 * SidePadding;
            _widths.Clear();
            _sung.Set(0, 1);
            LineScale.ScaleX = LineScale.ScaleY = 1;
            Second.Visibility = Visibility.Collapsed;
            return;
        }
        Dim.FontSize = Measurer.FontSize = _settings.DesktopFontSize;
        // Lines are split into pieces that fit (or shrunk), never cut off.
        Dim.MaxWidth = double.PositiveInfinity;
        // Fonts in the order the line's language wants them (widths depend on it).
        var song = lyrics is { } l ? Romanizer.SongLanguage(l) : null;
        var font = Fonts.For(line.Text, song);
        if (Dim.FontFamily != font)
        {
            Dim.FontFamily = Measurer.FontFamily = font;
            _widths.Clear();
        }
        _pieces = LinePieces.Split(line, StripWidth - 2 * SidePadding, Measure);
        _piece = null;

        var under = _model.CurrentIndex is int i && lyrics != null ? _settings.SecondLine.TextUnder(i, lyrics) : null;
        Second.Text = under?.Text ?? "";
        if (under is { } u) Second.FontFamily = Fonts.For(u.Text, song);
        Second.Opacity = under is { IsExtra: true } ? 0.9 : 0.6;
        Second.Visibility = under == null ? Visibility.Collapsed : Visibility.Visible;
        ShowPiece();
    }

    /// <summary>Puts up the piece being sung and plans its sweep from now; sets a timer for the next piece.</summary>
    void ShowPiece()
    {
        _pieceTimer.Stop();
        if (_pieces.Count == 0) return;
        double now = _model.Position();
        var piece = LinePieces.Current(_pieces, now)!;
        if (!ReferenceEquals(piece, _piece))
        {
            _piece = piece;
            Dim.Text = piece.Text;
            _textWidth = Math.Ceiling(Measure(piece.Text)) + 1;
            // One unbreakable piece wider than the strip is shrunk to fit.
            double scale = Math.Min(1, (StripWidth - 2 * SidePadding) / _textWidth);
            LineScale.ScaleX = LineScale.ScaleY = scale;
        }
        Animate(piece, now);

        int index = _pieces.IndexOf(piece);
        if (_model.IsPlaying && index + 1 < _pieces.Count)
        {
            _pieceTimer.Interval = TimeSpan.FromSeconds(Math.Max(_pieces[index + 1].Start - now, 0) + 0.005);
            _pieceTimer.Start();
        }
    }

    void Animate(LyricLine piece, double position) => _sung.Animate(piece, _textWidth, position, _model.IsPlaying, Measure);
}
