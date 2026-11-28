using Lyricod.Platform;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;

namespace Lyricod;

/// <summary>
/// A press that carries an overlay window. The pointer is captured on the pressed element and, at once or
/// once it has been held, the window's top-left is asked to keep the pressed point under the pointer. The
/// window places itself (<see cref="Moved"/>) and saves its spot when the press is over (<see cref="Ended"/>).
/// </summary>
sealed class OverlayDrag
{
    /// <summary>The four-way move cursor, for whatever can be dragged.</summary>
    public static readonly InputCursor Cursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

    /// <summary>How far the pointer may stray while it holds, in view pixels. Further, and it was no hold.</summary>
    const double HoldSlop = 6;

    readonly AppWindow _window;
    readonly Overlay _overlay;
    readonly DispatcherQueueTimer _holdTimer;
    /// <summary>The press: what captured which pointer, and where in the window (the pressed element itself may shift).</summary>
    (UIElement Element, uint Pointer, Point From)? _press;
    /// <summary>
    /// Carrying: where in the window the pointer took hold, in view pixels, and where the window was, in
    /// screen pixels. A screen of another scale resizes the window: the point taken hold of stays under the pointer.
    /// </summary>
    (double X, double Y, PointInt32 Window)? _carry;

    public OverlayDrag(Window window, Overlay overlay)
    {
        _window = window.AppWindow;
        _overlay = overlay;
        _holdTimer = window.DispatcherQueue.CreateTimer();
        _holdTimer.IsRepeating = false;
        _holdTimer.Tick += (_, _) => { try { Carry(); } catch (Exception e) { Log.Write(e); } };
        window.Closed += (_, _) => _holdTimer.Stop();
    }

    /// <summary>The pointer is down on the window to drag it: held, or carrying.</summary>
    public bool Active => _press != null;

    /// <summary>The window is following the pointer.</summary>
    public bool Carrying => _carry != null;

    /// <summary>The window starts following the pointer.</summary>
    public event Action? Started;

    /// <summary>Where the window's top-left would be, the point taken hold of kept under the pointer (screen pixels).</summary>
    public event Action<PointInt32>? Moved;

    /// <summary>The press is over; whether it left the window somewhere else.</summary>
    public event Action<bool>? Ended;

    /// <summary>An element whose presses can drag (see <see cref="Press"/>): it's what captures the pointer.</summary>
    public void Watch(UIElement element)
    {
        element.PointerMoved += OnMoved;
        element.PointerReleased += OnLetGo;
        element.PointerCanceled += OnLetGo;
        element.PointerCaptureLost += OnLetGo;
    }

    /// <summary>
    /// A press on a watched element: the window is carried at once, or once the press has been held
    /// <paramref name="hold"/> seconds without straying.
    /// </summary>
    public void Press(UIElement element, PointerRoutedEventArgs e, double hold = 0)
    {
        var point = e.GetCurrentPoint(null);
        if (_press != null || !point.Properties.IsLeftButtonPressed || !element.CapturePointer(e.Pointer)) return;
        _press = (element, e.Pointer.PointerId, point.Position);
        if (hold > 0)
        {
            _holdTimer.Interval = TimeSpan.FromSeconds(hold);
            _holdTimer.Start();
        }
        else Carry();
    }

    void Carry()
    {
        if (_press == null || _carry != null || !Win32.GetCursorPos(out var p)) return;
        var at = _window.Position;
        double k = _overlay.Scale;
        _carry = ((p.X - at.X) / k, (p.Y - at.Y) / k, at);
        _overlay.Carried = true;
        Started?.Invoke();
    }

    void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_press is not { } press || e.Pointer.PointerId != press.Pointer) return;
            var point = e.GetCurrentPoint(null);
            if (!point.Properties.IsLeftButtonPressed) { End(); return; }
            if (_carry is { } carry)
            {
                // By the screen's own measure: the window's would shift with it.
                if (!Win32.GetCursorPos(out var p)) return;
                double k = _overlay.Scale;
                Moved?.Invoke(new PointInt32(p.X - (int)Math.Round(carry.X * k), p.Y - (int)Math.Round(carry.Y * k)));
                Win32.SetCursor(Win32.LoadCursor(0, Win32.MoveCursor));
            }
            else if (Math.Abs(point.Position.X - press.From.X) > HoldSlop || Math.Abs(point.Position.Y - press.From.Y) > HoldSlop) End();
        }
        catch (Exception) { /* the screen or window went away */ }
    }

    void OnLetGo(object sender, PointerRoutedEventArgs e)
    {
        try { if (_press is { } press && e.Pointer.PointerId == press.Pointer) End(); }
        catch (Exception) { /* the screen or window went away */ }
    }

    /// <summary>
    /// The press is over: the button came up, the hold strayed, or the window is hiding or changing how it's
    /// moved. A carried window stays where it is.
    /// </summary>
    public void End()
    {
        if (_press is not { } press) return;
        var at = _window.Position;
        bool moved = _carry is { } carry && (at.X != carry.Window.X || at.Y != carry.Window.Y);
        _press = null;
        _carry = null;
        _overlay.Carried = false;
        _holdTimer.Stop();
        press.Element.ReleasePointerCaptures();
        Ended?.Invoke(moved);
    }
}

/// <summary>
/// The small round handle an overlay shows under a resting pointer, to be dragged by. Dark with a light
/// edge, so it shows over anything.
/// </summary>
sealed partial class MoveHandle : Grid
{
    /// <summary>Its width and height, in view pixels.</summary>
    public const double Size = 24;

    public MoveHandle()
    {
        Width = Height = Size;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Opacity = 0;
        Visibility = Visibility.Collapsed;
        ProtectedCursor = OverlayDrag.Cursor;
        // All of its square takes the press, as all of it takes the click from what's underneath (see Holds).
        Background = new SolidColorBrush(Colors.Transparent);
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Size / 2), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xC0, 0x20, 0x20, 0x24)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)),
            // SIPMove: the four-way arrows that Windows 10's icon font has too.
            Child = new FontIcon { Glyph = "\uE759", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) },
        });
    }

    public bool Shown { get; private set; }

    /// <summary>Its top-left in its window, in view pixels.</summary>
    Point _spot;

    public void MoveTo(double x, double y)
    {
        if (x == _spot.X && y == _spot.Y) return;
        _spot = new Point(x, y);
        Margin = new Thickness(x, y, 0, 0);
    }

    /// <summary>Whether a point in its window (view pixels) is on it.</summary>
    public bool Holds(double x, double y) => x >= _spot.X && x <= _spot.X + Size && y >= _spot.Y && y <= _spot.Y + Size;

    /// <summary>Fades it in or out. Hidden, it takes no presses.</summary>
    public void Show(bool show)
    {
        if (show == Shown) return;
        Shown = show;
        IsHitTestVisible = show;
        if (show) Visibility = Visibility.Visible;
        var fade = new DoubleAnimation { To = show ? 1 : 0, Duration = TimeSpan.FromSeconds(0.15) };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var story = new Storyboard { Children = { fade } };
        story.Completed += (_, _) =>
        {
            try { if (!Shown) Visibility = Visibility.Collapsed; }
            catch (Exception e) { Log.Write(e); }
        };
        story.Begin();
    }
}
