using Lyricod.Core;
using Lyricod.Platform;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Lyricod;

/// <summary>Wraps the island's content, so the pointer over any of it can be given a cursor (a Border can't be).</summary>
sealed partial class Grip : Grid
{
    public InputCursor? Cursor { set => ProtectedCursor = value; }
}

/// <summary>
/// The floating style (Settings › Lyricod Island › Island style): rounded on all four corners and
/// left where it was dragged. Unlocked, it takes clicks and moves with the pointer; locked, it
/// behaves like the default island, and its open mini player, held a moment, carries it. Near a screen edge
/// it grows away from it (see <see cref="IslandPlacement"/>).
/// </summary>
public sealed partial class IslandWindow
{
    bool Floating => _settings.IslandStyle == IslandStyle.Floating;

    /// <summary>Floating and unlocked: takes clicks and can be dragged, with no hover action.</summary>
    bool Movable => Floating && !_settings.IslandLocked;

    /// <summary>The pointer is moving the island: it stays as it is, and isn't put back at its saved spot, until it's let go.</summary>
    bool _dragging;

    /// <summary>How long the open mini player is held before it carries a locked island: a slip on the way to a button mustn't move it.</summary>
    const double HoldSeconds = 0.3;
    /// <summary>How far the pointer may stray meanwhile, in view pixels. Further, sooner, and it was no hold.</summary>
    const double HoldSlop = 6;
    /// <summary>A press on the open mini player: which pointer, and where in the window (the pill itself shifts when a line changes its width).</summary>
    (uint Id, Windows.Foundation.Point From)? _hold;
    /// <summary>Held long enough, the press carries the island: where the pointer and the window were then, in screen pixels.</summary>
    (int X, int Y, PointInt32 Window)? _carry;
    static readonly InputCursor MoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    /// <summary>The widest and tallest the pill gets (a long line, the mini player), in view pixels.</summary>
    double MaxPillWidth => Math.Max(2 * SideWidth + MaxText, ExpandedMinWidth);
    double MaxPillHeight => RowHeight + SecondHeight + ExpandedHeight;

    /// <summary>Where it was dragged to, or just below the top centre of the main display.</summary>
    void PlaceFloating(double k)
    {
        // A saved spot on a screen that's no longer there (unplugged) falls back to the default.
        DisplayArea? display = _settings.IslandX is int sx && _settings.IslandY is int sy
            ? DisplayArea.GetFromPoint(new PointInt32(_settings.IslandDisplayX ?? sx, _settings.IslandDisplayY ?? sy), DisplayAreaFallback.None)
                // A spot saved before the display point (0.5.6 and earlier) can sit just past its display's right or bottom edge.
                ?? (_settings.IslandDisplayX == null ? DisplayArea.GetFromPoint(new PointInt32(sx - 1, sy - 1), DisplayAreaFallback.None) : null)
            : null;
        var area = (display ?? DisplayArea.Primary).WorkArea;
        _screenWidth = area.Width / k;
        double x = display != null ? _settings.IslandX!.Value : area.X + area.Width / 2.0;
        double y = display != null ? _settings.IslandY!.Value : area.Y + 16 * k;
        var anchors = IslandPlacement.Anchors(x, y, new ScreenRect(area.X, area.Y, area.Width, area.Height), MaxPillWidth * k, MaxPillHeight * k);
        Pill.HorizontalAlignment = anchors.X switch
        {
            IslandAnchorX.Left => HorizontalAlignment.Left,
            IslandAnchorX.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        Pill.VerticalAlignment = anchors.Y == IslandAnchorY.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        // Keep room for an old, larger layout until its shrink animation finishes.
        double width = Math.Max(WindowWidth, Pill.Width + 20 * LayoutScale);
        double height = Math.Max(WindowHeight, Pill.Height + 12 * LayoutScale);
        var size = new SizeInt32((int)Math.Ceiling(width * k), (int)Math.Ceiling(height * k));
        var (left, top) = IslandPlacement.Origin(x, y, anchors, size.Width, size.Height);
        var rect = new RectInt32((int)Math.Round(left), (int)Math.Round(top), size.Width, size.Height);
        if (AppWindow.Size.Width != rect.Width || AppWindow.Size.Height != rect.Height || AppWindow.Position.X != rect.X || AppWindow.Position.Y != rect.Y)
            AppWindow.MoveAndResize(rect);
    }

    /// <summary>The pill's bounds on screen, in physical pixels.</summary>
    (double Left, double Top, double Width, double Height) PillBounds()
    {
        double k = _overlay.Scale;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        double width = Pill.ActualWidth * k, height = Pill.ActualHeight * k;
        double left = pos.X + Pill.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => size.Width - width,
            _ => (size.Width - width) / 2,
        };
        double top = pos.Y + (Pill.VerticalAlignment == VerticalAlignment.Bottom ? size.Height - height : 0);
        return (left, top, width, height);
    }

    /// <summary>
    /// Unlocked (floating only): the island takes clicks and turns dark grey (see <see cref="ApplyTheme"/>),
    /// so it's clear it can be dragged. Locked: click-through until the pointer is over it.
    /// </summary>
    void ApplyMoveMode()
    {
        SetHovered(false);
        _overlay.SetClickThrough(!Movable);
        ApplyTheme();
    }

    void OnPillPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (Movable) { Drag(); return; }
            // Locked, the open mini player is a handle too: its buttons and progress bar keep their own presses.
            var point = e.GetCurrentPoint(null);
            if (_hold != null || !Floating || !_expanded || !point.Properties.IsLeftButtonPressed || !_grip.CapturePointer(e.Pointer)) return;
            _hold = (e.Pointer.PointerId, point.Position);
            _holdTimer.Start();
        }
        catch (Exception) { /* the screen or window went away */ }
    }

    /// <summary>The hold is long enough: from here the island follows the pointer.</summary>
    void BeginCarry()
    {
        if (_hold == null || !Floating || !_expanded || !Win32.GetCursorPos(out var p)) return;
        _carry = (p.X, p.Y, AppWindow.Position);
        _dragging = true;
        _grip.Cursor = MoveCursor;
        // It's about to come over other parts of the screen: the glass follows at its full rate from the start.
        _liquid?.Wake();
    }

    /// <summary>
    /// Carried, the window goes where the pointer has gone since the hold was long enough, by the screen's
    /// own measure (the window's would shift with it). Before that, straying drops the press.
    /// </summary>
    void OnPillMoved(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_hold is not { } hold || e.Pointer.PointerId != hold.Id) return;
            var point = e.GetCurrentPoint(null);
            if (!point.Properties.IsLeftButtonPressed) { EndHold(e.Pointer); return; }
            if (_carry is { } carry)
            {
                if (Win32.GetCursorPos(out var p)) AppWindow.Move(new PointInt32(carry.Window.X + p.X - carry.X, carry.Window.Y + p.Y - carry.Y));
                return;
            }
            if (Math.Abs(point.Position.X - hold.From.X) > HoldSlop || Math.Abs(point.Position.Y - hold.From.Y) > HoldSlop) EndHold(e.Pointer);
        }
        catch (Exception) { /* the screen or window went away */ }
    }

    void OnPillLetGo(object sender, PointerRoutedEventArgs e)
    {
        try { if (_hold is { } hold && e.Pointer.PointerId == hold.Id) EndHold(e.Pointer); }
        catch (Exception) { /* the screen or window went away */ }
    }

    /// <summary>The button is up, or the press is no longer a hold: a carried island is left, and saved, where it is.</summary>
    void EndHold(Pointer pointer)
    {
        if (_hold == null) return;
        bool carried = _carry is { } carry && (AppWindow.Position.X != carry.Window.X || AppWindow.Position.Y != carry.Window.Y);
        _hold = null;
        _carry = null;
        _dragging = false;
        _holdTimer.Stop();
        _grip.Cursor = null;
        _grip.ReleasePointerCapture(pointer);
        if (carried) Dropped();
    }

    void Drag()
    {
        _dragging = true;
        try
        {
            // Windows moves the window as if its title bar were dragged, and returns once the button is let go.
            Win32.ReleaseCapture();
            Win32.SendMessage(_overlay.Handle, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, 0);
        }
        finally { _dragging = false; }
        Dropped();
    }

    void Dropped()
    {
        if (_closed) return;
        SaveSpot();
        // The pointer was on it all the way: nothing it did meanwhile is a gesture or a leaving.
        _lastInside = PlaybackModel.Now;
        _trail.Clear();
    }

    /// <summary>
    /// The pill's bounds with the mini player closed, in physical pixels. Carried, it's dropped open, and
    /// the island's place is where it closes to: the open pill's own bounds would move an island held
    /// by its bottom or a side to wherever its player happened to reach.
    /// </summary>
    (double Left, double Top, double Width, double Height) RestingBounds()
    {
        var (left, top, width, height) = PillBounds();
        if (!_expanded) return (left, top, width, height);
        double k = _overlay.Scale;
        double w = (_textWidth + 2 * SideWidth) * k, h = (RowHeight + (HasSecondRow ? SecondHeight : 0)) * k;
        double l = Pill.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => left,
            HorizontalAlignment.Right => left + width - w,
            _ => left + (width - w) / 2,
        };
        return (l, Pill.VerticalAlignment == VerticalAlignment.Bottom ? top + height - h : top, w, h);
    }

    void SaveSpot()
    {
        double k = _overlay.Scale;
        var (left, top, width, height) = RestingBounds();
        var display = DisplayArea.GetFromRect(new RectInt32((int)left, (int)top, Math.Max(1, (int)width), Math.Max(1, (int)height)),
            DisplayAreaFallback.Nearest);
        var area = display.WorkArea;
        var bounds = display.OuterBounds;
        var (dx, dy) = IslandPlacement.DisplayPoint(new ScreenRect(left, top, width, height),
            new ScreenRect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        _settings.IslandDisplayX = dx;
        _settings.IslandDisplayY = dy;
        // The screen it was dropped on decides how wide a line can get.
        _screenWidth = area.Width / k;
        var (x, y) = IslandPlacement.Spot(new ScreenRect(left, top, width, height), new ScreenRect(area.X, area.Y, area.Width, area.Height),
            MaxPillWidth * k, MaxPillHeight * k);
        _settings.IslandX = x;
        _settings.IslandY = y;
        Place();
    }
}
