using Lyricod.Core;
using Lyricod.Platform;
using Microsoft.UI.Dispatching;
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
/// left where it was moved to. What moves it goes by what the pointer does to it: its open mini player,
/// held a moment, carries it; one kept as it is shows a handle under a resting pointer; one that hides
/// under the pointer can be dragged whole, once, after Move in Settings. Near a screen edge
/// it grows away from it (see <see cref="IslandPlacement"/>).
/// </summary>
public sealed partial class IslandWindow
{
    bool Floating => _settings.IslandStyle == IslandStyle.Floating;

    /// <summary>
    /// Waiting to be dragged, after Move in Settings (see <see cref="BeginMove"/>): it takes clicks, with no
    /// hover action, until it has been dragged once. Never saved.
    /// </summary>
    bool Movable { get; set; }

    /// <summary>Kept as it is under the pointer, a floating island has no mini player to hold: a handle moves it.</summary>
    bool HasHandle => Floating && _settings.IslandHover == IslandHover.Nothing;

    /// <summary>How long the open mini player is held before it carries the island: a slip on the way to a button mustn't move it.</summary>
    const double HoldSeconds = 0.3;
    /// <summary>Gap between the pill and its handle, in view pixels.</summary>
    const double HandleGap = 6;

    readonly OverlayDrag _drag;
    readonly MoveHandle _handle;
    readonly HandleReveal _reveal = new();
    /// <summary>The pointer is on the handle: the window takes clicks.</summary>
    bool _handleHot;
    /// <summary>Where the pointer's travel last had the window in this carry, before snapping.</summary>
    PointInt32? _wanted;

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
        var screen = new ScreenRect(area.X, area.Y, area.Width, area.Height);
        _screenWidth = area.Width / k;
        double x = display != null ? IslandPlacement.Across(_settings.IslandX!.Value, screen) : IslandPlacement.CentreLine(screen);
        double y = display != null ? _settings.IslandY!.Value : area.Y + 16 * k;
        var anchors = IslandPlacement.Anchors(x, y, screen, MaxPillWidth * k, MaxPillHeight * k);
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
    /// Waiting to be dragged, the island takes clicks and turns dark grey (see <see cref="ApplyTheme"/>), so
    /// it's clear it can be. Otherwise it's click-through until the pointer opens it or is on its handle.
    /// </summary>
    void ApplyMoveMode()
    {
        Movable &= Floating && _settings.IslandHover == IslandHover.Hide;
        _drag.End();
        HideHandle();
        SetHovered(false);
        _overlay.SetClickThrough(!Movable);
        _grip.Cursor = Movable ? OverlayDrag.Cursor : null;
        ApplyTheme();
    }

    /// <summary>
    /// Move in Settings, for an island that hides under the pointer and so has nothing to hold: all of it can
    /// be dragged, once, until it hides or is reset, or the hover action or style changes.
    /// </summary>
    public void BeginMove()
    {
        try
        {
            // Not showing, there's nothing to drag: it mustn't come up later still waiting.
            if (_closed || !_shown || Movable || !Floating || _settings.IslandHover != IslandHover.Hide) return;
            Movable = true;
            ApplyMoveMode();
        }
        catch (Exception e) { Log.Write(e); }
    }

    /// <summary>No longer waiting to be dragged: back to click-through and its hover action.</summary>
    void EndMove()
    {
        if (!Movable) return;
        Movable = false;
        ApplyMoveMode();
    }

    /// <summary>Reset to top centre in Settings, which changes nothing for an island already there.</summary>
    public void CancelMove()
    {
        try { if (!_closed) EndMove(); }
        catch (Exception e) { Log.Write(e); }
    }

    void OnPillPressed(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (Movable) _drag.Press(_grip, e);
            // The open mini player is a handle too, once held: its buttons and progress bar keep their own presses.
            else if (Floating && _expanded) _drag.Press(_grip, e, HoldSeconds);
        }
        catch (Exception) { /* the screen or window went away */ }
    }

    void OnHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        try { _drag.Press(_handle, e); }
        catch (Exception) { /* the screen or window went away */ }
    }

    /// <summary>From here the island follows the pointer.</summary>
    void OnDragStarted()
    {
        _wanted = null;
        _grip.Cursor = OverlayDrag.Cursor;
        // It's about to come over other parts of the screen: the glass follows at its full rate from the start.
        // Queued, as for a window coming forward: a carry can begin inside the press itself.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try { _liquid?.Wake(); }
            catch (Exception e) { Log.Write(e); }
        });
    }

    /// <summary>
    /// Carried, the window goes where the pointer takes it, except across while the centre of the pill as it
    /// will rest is near the centre line of the screen under it: there it sits on the line, where a reset
    /// island sits. The pointer's travel is still counted from the press, so it leaves the line without a jump.
    /// </summary>
    void OnDragMoved(PointInt32 to)
    {
        _wanted = to;
        var from = AppWindow.Position;
        var (left, top, width, height) = RestingBounds();
        double centre = to.X + left - from.X + width / 2, middle = to.Y + top - from.Y + height / 2;
        var area = DisplayArea.GetFromPoint(new PointInt32((int)Math.Round(centre), (int)Math.Round(middle)), DisplayAreaFallback.Nearest).WorkArea;
        double snapped = IslandPlacement.SnapCentre(centre, new ScreenRect(area.X, area.Y, area.Width, area.Height),
            IslandPlacement.SnapRange * _overlay.Scale);
        AppWindow.Move(new PointInt32((int)Math.Round(to.X + snapped - centre), to.Y));
    }

    /// <summary>
    /// Let go: a moved island is left, and saved, where it is. One press is the one move: dropped where it
    /// started, on the centre line say, it's no longer waiting to be dragged either.
    /// </summary>
    void OnDragEnded(bool moved)
    {
        _grip.Cursor = null;
        if (_closed) return;
        if (moved) Dropped();
        EndMove();
    }

    /// <summary>Saved where it was let go.</summary>
    void Dropped()
    {
        // A line that came since the pointer last moved may have changed the pill's width, and so the centre
        // of a pill held by a side: it's snapped again, by the width it's saved with.
        if (_wanted is { } to) OnDragMoved(to);
        SaveSpot();
        // The pointer was on it all the way: nothing it did meanwhile is a gesture or a leaving.
        double now = PlaybackModel.Now;
        _lastInside = now;
        _trail.Clear();
        if (_handle.Shown)
        {
            // Saving placed the window afresh; the handle keeps to the pill, and stays its moment from now,
            // not from the press, should it have come out from under the pointer.
            PlaceHandle();
            _reveal.Update(false, true, now);
        }
    }

    /// <summary>
    /// Shows the handle once the pointer has rested on the pill, and takes clicks only while the pointer is on
    /// the handle. Returns whether it is.
    /// </summary>
    bool UpdateHandle(Win32.POINT pointer, bool overPill, double now)
    {
        if (!HasHandle) return false;
        var origin = AppWindow.Position;
        double k = _overlay.Scale;
        bool onHandle = _handle.Shown && _handle.Holds((pointer.X - origin.X) / k, (pointer.Y - origin.Y) / k);
        bool show = _reveal.Update(overPill, onHandle, now);
        // Placed as it comes up. After that it keeps to the pill's end only under a pointer at rest on the
        // pill: one on its way to the handle finds it where it was, whatever line has come meanwhile.
        bool resting = overPill && _trail.Count > 0 && _trail[^1].X == pointer.X && _trail[^1].Y == pointer.Y;
        if (show && !onHandle && (resting || !_handle.Shown)) PlaceHandle();
        _handle.Show(show);
        bool hot = show && onHandle;
        if (hot != _handleHot)
        {
            _handleHot = hot;
            _overlay.SetClickThrough(!hot);
        }
        return hot;
    }

    /// <summary>
    /// Just past the pill's trailing end, level with its middle; before its leading end when there's no room
    /// after it; over its bars when there's none either side, or the window is cut to the pill's shape.
    /// </summary>
    void PlaceHandle()
    {
        double k = _overlay.Scale, size = MoveHandle.Size;
        var (left, top, width, height) = PillBounds();
        var origin = AppWindow.Position;
        double start = (left - origin.X) / k, end = start + width / k, window = AppWindow.Size.Width / k;
        // Room is what the widest layout takes up: that much is kept on screen (see IslandPlacement).
        double from = Pill.HorizontalAlignment switch
        {
            HorizontalAlignment.Left => 0,
            HorizontalAlignment.Right => window - MaxPillWidth,
            _ => (window - MaxPillWidth) / 2,
        };
        double to = from + MaxPillWidth;
        if (_shaped) (from, to) = (start, end);
        double x = IslandPlacement.HandleLeft(start, end, size, HandleGap, from, to, inset: (CoverInset + CoverSize / 2) * LayoutScale - size / 2);
        _handle.MoveTo(x, (top - origin.Y) / k + (height / k - size) / 2);
    }

    void HideHandle()
    {
        _reveal.Reset();
        _handle.Show(false);
        if (!_handleHot) return;
        _handleHot = false;
        _overlay.SetClickThrough(true);
    }

    /// <summary>
    /// The pill's bounds with the mini player closed, in physical pixels. Carried by its player, it's dropped
    /// open, and the island's place is where it closes to: the open pill's own bounds would move an island held
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
