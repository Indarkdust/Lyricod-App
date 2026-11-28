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
using Windows.Foundation;
using Windows.Graphics;
using System.Runtime.InteropServices;
using WinUIEx;
using WinUIEx.Messaging;

namespace Lyricod;

/// <summary>
/// The floating lyric strip over every app. The sung part of the line is the sung colour in the
/// text's shape, fading out over a soft edge; that edge moves on the compositor (no per-frame work
/// in the app), re-planned whenever the line or the clock changes.
/// </summary>
public sealed partial class DesktopLyricsWindow : Window
{
    /// <summary>Strip width in view pixels.</summary>
    const double StripWidth = 900;
    const double SidePadding = 24;

    readonly PlaybackModel _model;
    readonly Settings _settings;
    readonly nint _hwnd;
    readonly Compositor _compositor;
    readonly SweepPainter _sung;
    /// <summary>The sung colour over the row under the line, when that's a timed pronunciation.</summary>
    readonly SweepPainter _sungUnder;
    readonly TextRuler _underRuler;
    /// <summary>That pronunciation as a line to light (null for any other row).</summary>
    LyricLine? _under;
    readonly DispatcherQueueTimer _pieceTimer;
    readonly Dictionary<string, double> _widths = [];

    List<LyricLine> _pieces = [];
    LyricLine? _piece;
    double _textWidth;
    bool? _shown;
    /// <summary>Whether the window has its strip size yet (before that, its size is Windows' default).</summary>
    bool _placed;
    readonly Overlay _overlay;
    readonly OverlayDrag _drag;
    readonly MoveHandle _handle;
    readonly HandleReveal _reveal = new();
    readonly DispatcherQueueTimer _pointerTimer;
    /// <summary>The pointer is on the handle: the window takes clicks.</summary>
    bool _handleHot;
    /// <summary>Where the pointer was when last checked.</summary>
    Win32.POINT _pointer;

    public DesktopLyricsWindow(PlaybackModel model, Settings settings)
    {
        InitializeComponent();
        _model = model;
        _settings = settings;
        // A borderless, always-on-top, fully transparent window that never takes focus
        // and stays out of Alt-Tab.
        _overlay = new Overlay(this);
        _hwnd = _overlay.Handle;
        // Clicks reach whatever is underneath, except on the move handle (see CheckPointer).
        _overlay.SetClickThrough(true);

        _compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
        _sung = new SweepPainter(Dim, LitHost);
        _sungUnder = new SweepPainter(Second, SecondLit);
        _underRuler = new TextRuler(UnderMeasurer);
        AttachShadow(Dim, LineShadow);
        AttachShadow(Second, SecondShadow);
        AttachShadow(Third, ThirdShadow);
        LineHost.RenderTransformOrigin = new Point(0.5, 0.5);

        _handle = new MoveHandle();
        Root.Children.Add(_handle);
        _drag = new OverlayDrag(this, _overlay);
        _drag.Watch(_handle);
        _handle.PointerPressed += (_, e) => { try { _drag.Press(_handle, e); } catch (Exception x) { Log.Write(x); } };
        _drag.Moved += to => AppWindow.Move(to);
        _drag.Ended += OnDragEnded;
        // The pointer is checked often only near the lyrics; a few times a second otherwise.
        _pointerTimer = DispatcherQueue.CreateTimer();
        _pointerTimer.Interval = TimeSpan.FromMilliseconds(250);
        _pointerTimer.Tick += (_, _) => { try { CheckPointer(); } catch (Exception e) { Log.Write(e); } };

        _pieceTimer = DispatcherQueue.CreateTimer();
        _pieceTimer.IsRepeating = false;
        _pieceTimer.Tick += (_, _) => { try { ShowPiece(); } catch (Exception e) { Log.Write(e); } };

        _model.Changed += OnModelChanged;
        Fonts.Changed += OnFontChanged;
        _settings.PropertyChanged += OnSettingChanged;
        DisplayPower.Changed += OnDisplayChanged;
        Closed += (_, _) =>
        {
            _model.Changed -= OnModelChanged;
            Fonts.Changed -= OnFontChanged;
            _settings.PropertyChanged -= OnSettingChanged;
            DisplayPower.Changed -= OnDisplayChanged;
            _pieceTimer.Stop();
            _pointerTimer.Stop();
        };

        ApplyStyle();
        Place();
        Render();
    }

    // ---- Window ----

    /// <summary>Main line, plus the rows the layout puts under it, plus padding (view pixels).</summary>
    double StripHeight()
    {
        double f = _settings.DesktopFontSize;
        double under = _settings.SecondLine.MaxRows() switch
        {
            0 => 0,
            1 => f * UnderScale * 1.25 + 4,
            _ => f * PronunciationScale * 1.25 + 4 + f * UnderScale * 1.25 + 4,
        };
        return Math.Round(f * 1.25 + under + 32);
    }

    /// <summary>The rows under the line, as a share of the line's size; a pronunciation over a translation is a little smaller.</summary>
    const double UnderScale = 0.6, PronunciationScale = 0.55;

    /// <summary>Sizes the strip and puts it where the user left it, or at the bottom centre.</summary>
    void Place()
    {
        // It's under the pointer: its saved spot would pull it away. It's placed when let go.
        if (_drag.Carrying) return;
        double scale = Win32.Scale(_hwnd);
        var size = new SizeInt32((int)(StripWidth * scale), (int)(StripHeight() * scale));
        try
        {
            // The first time, the saved spot is the strip's own top-left.
            int grow = _placed ? size.Height - AppWindow.Size.Height : 0;
            if (_settings.DesktopX is int x && _settings.DesktopY is int y
                && StripPlacement.Top(y, grow, ScreenTop(x, y, AppWindow.Size)) is var top && OnSomeScreen(x, top, size))
            {
                AppWindow.MoveAndResize(new RectInt32(x, top, size.Width, size.Height));
                // Keep the saved top-left in step, or the next placing (or start) moves the strip by the change.
                _settings.DesktopY = top;
            }
            else
            {
                var area = DisplayArea.Primary.WorkArea;
                AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2,
                    area.Y + area.Height - size.Height - (int)(40 * scale), size.Width, size.Height));
            }
        }
        finally { _placed = true; }
    }

    static int ScreenTop(int x, int y, SizeInt32 size) =>
        DisplayArea.GetFromRect(new RectInt32(x, y, size.Width, size.Height), DisplayAreaFallback.Nearest).WorkArea.Y;

    static bool OnSomeScreen(int x, int y, SizeInt32 size)
    {
        var area = DisplayArea.GetFromRect(new RectInt32(x, y, size.Width, size.Height), DisplayAreaFallback.None);
        return area != null;
    }

    // ---- Moving ----

    /// <summary>Gap between the line's end and the handle, in view pixels.</summary>
    const double HandleGap = 6;

    /// <summary>The main line's drawn width (a piece too wide for the strip is drawn smaller).</summary>
    double LineWidth => Dim.ActualWidth * LineScale.ScaleX;

    /// <summary>The rows showing, in the window (view pixels), or null with nothing to show: the pointer resting there brings up the handle.</summary>
    Rect? RowsBounds()
    {
        double width = LineWidth;
        if (Second.Visibility == Visibility.Visible) width = Math.Max(width, Second.ActualWidth);
        if (Third.Visibility == Visibility.Visible) width = Math.Max(width, Third.ActualWidth);
        if (width <= 0) return null;
        // The rows are centred in the strip, inside the panel's padding.
        width = Math.Min(width, Root.ActualWidth);
        double height = Math.Min(Rows.ActualHeight - Rows.Padding.Top - Rows.Padding.Bottom, Root.ActualHeight);
        return new Rect((Root.ActualWidth - width) / 2, (Root.ActualHeight - height) / 2, width, height);
    }

    /// <summary>
    /// Just after the main line's end, level with it; over its end when there's no room after it on the strip,
    /// or on the screen the pointer is on: the strip is wider than its text and may hang past that screen's edge.
    /// </summary>
    void PlaceHandle(Rect rows, Win32.POINT pointer)
    {
        double k = Win32.Scale(_hwnd), end = (Root.ActualWidth + LineWidth) / 2;
        int left = AppWindow.Position.X;
        var area = DisplayArea.GetFromPoint(new PointInt32(pointer.X, pointer.Y), DisplayAreaFallback.Nearest).WorkArea;
        double x = StripPlacement.HandleLeft(end, MoveHandle.Size, HandleGap, Root.ActualWidth, (area.X - left) / k, (area.X + area.Width - left) / k);
        _handle.MoveTo(x, rows.Top + (LineHost.ActualHeight - MoveHandle.Size) / 2);
    }

    /// <summary>
    /// Follows the pointer, often only while it's near the lyrics. Resting on them brings up the move handle, and
    /// only while the pointer is on that does the window take clicks.
    /// </summary>
    void CheckPointer()
    {
        // Being dragged, it stays as it is.
        if (_shown != true || _drag.Active || !Win32.GetCursorPos(out var p)) return;
        double k = Win32.Scale(_hwnd);
        var origin = AppWindow.Position;
        double x = (p.X - origin.X) / k, y = (p.Y - origin.Y) / k;
        var rows = RowsBounds();
        bool over = rows is { } r && r.Contains(new Point(x, y));
        bool onHandle = _handle.Shown && _handle.Holds(x, y);
        bool near = rows is { } n && x >= n.Left - 160 && x <= n.Right + 160 && y >= n.Top - 120 && y <= n.Bottom + 120;
        _pointerTimer.Interval = TimeSpan.FromMilliseconds(near || _handle.Shown ? 33 : 250);
        bool show = _reveal.Update(over, onHandle, PlaybackModel.Now);
        // Placed as it comes up. After that it keeps to the line's end only under a pointer at rest on the
        // lyrics: one on its way to the handle finds it where it was, whatever line has come meanwhile.
        bool resting = over && p.X == _pointer.X && p.Y == _pointer.Y;
        _pointer = p;
        if (show && !onHandle && (resting || !_handle.Shown) && rows is { } at) PlaceHandle(at, p);
        _handle.Show(show);
        bool hot = show && onHandle;
        if (hot == _handleHot) return;
        _handleHot = hot;
        _overlay.SetClickThrough(!hot);
    }

    void HideHandle()
    {
        _reveal.Reset();
        _handle.Show(false);
        if (!_handleHot) return;
        _handleHot = false;
        _overlay.SetClickThrough(true);
    }

    /// <summary>Let go: saved where it is, as the strip's own top-left.</summary>
    void OnDragEnded(bool moved)
    {
        if (moved)
        {
            _settings.DesktopX = AppWindow.Position.X;
            _settings.DesktopY = AppWindow.Position.Y;
        }
        // A placing that came while it was carried (a new height, say) waited for this.
        Place();
    }

    /// <summary>With the display off, the pointer isn't followed.</summary>
    void OnDisplayChanged()
    {
        try
        {
            if (DisplayPower.Off) _pointerTimer.Stop();
            else if (_shown == true) _pointerTimer.Start();
        }
        catch (Exception e) { Log.Write(e); }
    }

    void UpdateVisibility()
    {
        bool visible = _settings.ShowDesktopLyrics && _model.IsSong;
        if (visible == _shown) return;
        _shown = visible;
        if (visible)
        {
            AppWindow.Show(false);
            _overlay.StripFrame();
            _overlay.KeepOnTop();
            if (!DisplayPower.Off) _pointerTimer.Start();
        }
        else
        {
            _pointerTimer.Stop();
            _drag.End();
            HideHandle();
            AppWindow.Hide();
        }
    }

    // ---- Text ----

    void ApplyStyle()
    {
        double f = _settings.DesktopFontSize;
        Dim.FontSize = Measurer.FontSize = f;
        Dim.FontWeight = Measurer.FontWeight = Fonts.BoldWeight;
        bool both = _settings.SecondLine.MaxRows() == 2;
        Second.FontSize = Math.Round(f * (both ? PronunciationScale : UnderScale));
        Third.FontSize = Math.Round(f * UnderScale);
        Second.FontWeight = Third.FontWeight = Fonts.SemiboldWeight;
        Second.MaxWidth = Third.MaxWidth = StripWidth - 2 * SidePadding;
        _sung.Color = _sungUnder.Color = ParseColor(_settings.SungColor);
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
        catch (Exception e) { Log.Write("font refresh: " + e); }
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
            case nameof(Settings.DesktopFontSize) or nameof(Settings.SungColor):
                ApplyStyle();
                Place();
                Render();
                break;
            case nameof(Settings.SecondLine):
                ApplyStyle();
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
        if (_shown != true) return; // showing renders afresh
        var lyrics = _model.Lyrics;
        var line = lyrics is { IsSynced: true } ? _model.CurrentLine : null;
        if (line == null)
        {
            // No line to sing: the song.
            _pieces = [];
            _piece = null;
            _pieceTimer.Stop();
            Dim.FontSize = Measurer.FontSize = Math.Round(_settings.DesktopFontSize * 0.8);
            Dim.Text = _model.IsSong && _model.Track is { } t ? (_model.IsSearching ? L.Get("Searching") : $"{t.Title} — {t.Artist}") : "";
            Dim.FontFamily = Measurer.FontFamily = Fonts.For(Dim.Text, null);
            // A long title ends in "…" rather than running off the strip.
            Dim.MaxWidth = StripWidth - 2 * SidePadding;
            _widths.Clear();
            _sung.Set(0, 1);
            _under = null;
            _sungUnder.Set(0, 1);
            LineScale.ScaleX = LineScale.ScaleY = 1;
            Second.Visibility = Third.Visibility = Visibility.Collapsed;
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

        List<(string Text, bool IsExtra)> rows = _model.CurrentIndex is int i && lyrics != null ? _settings.SecondLine.RowsUnder(i, lyrics) : [];
        ShowUnder(Second, rows.Count > 0 ? rows[0] : null, song);
        ShowUnder(Third, rows.Count > 1 ? rows[1] : null, song);
        // The pronunciation is always the first row under the line.
        _under = rows.Count > 0 ? SecondLineExtensions.SungRow(rows[0].Text, line) : null;
        ShowPiece();
    }

    static void ShowUnder(TextBlock row, (string Text, bool IsExtra)? under, LyricLanguage? song)
    {
        row.Text = under?.Text ?? "";
        if (under is { } u) row.FontFamily = Fonts.For(u.Text, song);
        row.Opacity = under is { IsExtra: true } ? 0.9 : 0.6;
        row.Visibility = under == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Puts up the piece being sung and plans its sweep from now; sets a timer for the next piece.</summary>
    void ShowPiece()
    {
        _pieceTimer.Stop();
        if (_pieces.Count == 0 || _shown != true) return;
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

    void Animate(LyricLine piece, double position)
    {
        _sung.Animate(piece, _textWidth, position, _model.IsPlaying, Measure);
        // The row under it, through the whole line (it isn't in pieces). One too wide for the
        // strip ends in "…": it stays as it is rather than be lit out of step.
        if (_under is not { } under) { _sungUnder.Set(0, 1); return; }
        _underRuler.Use(Second.FontFamily, Second.FontSize, Second.FontWeight);
        double width = Math.Ceiling(_underRuler.Measure(under.Text)) + 1;
        if (width > Second.MaxWidth) { _sungUnder.Set(0, 1); return; }
        _sungUnder.Animate(under, width, position, _model.IsPlaying, _underRuler.Measure);
    }
}
