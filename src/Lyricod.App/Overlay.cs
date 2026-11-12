using System.Runtime.InteropServices;
using Lyricod.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinUIEx;
using WinUIEx.Messaging;

namespace Lyricod;

/// <summary>
/// Makes a window an overlay, like the desktop strip and the island: borderless, always on
/// top, fully transparent around its content, out of Alt-Tab, and never taking focus.
/// </summary>
/// <remarks>
/// Always on top is set once, but another always-on-top window shown later goes above it, and
/// Show Desktop (Win+D) can push it down or minimise it. So whenever another app's window comes
/// forward (the desktop too), and every second besides, an overlay that's showing goes back on top
/// if it needs to: minimised, no longer always on top, or covered by another window. Windows of the
/// app in use (its menus and pop-ups, its own always-on-top window), the taskbar and its pop-ups,
/// and Lyricod's own windows are left above it: a covered overlay goes just above the window
/// covering it, not to the very top. A window that keeps putting itself back on top (another app
/// holding its own there) is left above after three rounds, rather than the two swapping places
/// every second. Nothing here takes focus.
/// </remarks>
sealed class Overlay
{
    static readonly List<Overlay> Showing = [];
    static readonly uint OwnProcess = (uint)Environment.ProcessId;
    static bool _watching;
    static Microsoft.UI.Dispatching.DispatcherQueueTimer? _check;
    /// <summary>Other apps' windows that kept coming back on top, now left there.</summary>
    static readonly HashSet<nint> LeftAbove = [];
    const int Rounds = 3;

    readonly Window _window;
    readonly nint _hwnd;
    // Kept alive for the window's lifetime: it hooks the window's messages.
    readonly WindowMessageMonitor _messages;
    // The window it last went above, with the app in use then, when, and how many times running.
    nint _raisedAbove;
    uint _raisedFor;
    long _raisedAt;
    int _rounds;

    public Overlay(Window window)
    {
        _window = window;
        _hwnd = window.GetWindowHandle();
        window.SystemBackdrop = new TransparentTintBackdrop();
        // The presenter owns the window's frame styles (setting them directly is undone at once),
        // and "no border" only sticks once it's attached, set last.
        window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        window.AppWindow.IsShownInSwitchers = false;
        StripFrame();
        _messages = SuppressFrame();
        Showing.Add(this);
        window.Closed += (_, _) => Showing.Remove(this);
        Watch();
    }

    /// <summary>Back above every always-on-top window, and out of minimised, without taking focus (only while it shows).</summary>
    public void KeepOnTop()
    {
        if (!Win32.IsWindowVisible(_hwnd)) return;
        if (Win32.IsIconic(_hwnd)) Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        Win32.RaiseTopmost(_hwnd);
    }

    /// <summary>Puts every showing overlay back on top that needs it; <paramref name="inUse"/> is the app in use's process.</summary>
    static void GuardAll(uint inUse)
    {
        foreach (var overlay in Showing.ToArray())
        {
            nint hwnd = overlay._hwnd;
            if (!Win32.IsWindowVisible(hwnd)) continue;
            if (Win32.IsIconic(hwnd) || !Win32.IsTopmost(hwnd)) { overlay.KeepOnTop(); continue; }
            nint cover = Win32.CoveredBy(hwnd, LeftAbove, OwnProcess, inUse);
            if (cover != 0) overlay.RaiseAbove(cover, inUse);
        }
    }

    /// <summary>
    /// Just above <paramref name="cover"/>, unless it has come straight back over the overlay, with the
    /// same app in use, <see cref="Rounds"/> times running: then it's left above.
    /// </summary>
    void RaiseAbove(nint cover, uint inUse)
    {
        long now = Environment.TickCount64;
        bool again = cover == _raisedAbove && inUse == _raisedFor && now - _raisedAt < 3000;
        _rounds = again ? _rounds + 1 : 1;
        (_raisedAbove, _raisedFor, _raisedAt) = (cover, inUse, now);
        if (_rounds > Rounds) { LeftAbove.Add(cover); return; }
        Win32.RaiseTopmost(_hwnd, cover);
    }

    static unsafe void Watch()
    {
        if (_watching) return;
        _watching = true;
        Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, 0, &OnWindowEvent, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);
        Win32.SetWinEventHook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZESTART, 0, &OnWindowEvent, OwnProcess, 0, Win32.WINEVENT_OUTOFCONTEXT);
        _check = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _check.Interval = TimeSpan.FromSeconds(1);
        _check.Tick += (_, _) =>
        {
            try
            {
                LeftAbove.RemoveWhere(h => !Win32.IsWindow(h));
                Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out uint inUse);
                GuardAll(inUse);
            }
            catch (Exception e) { Log.Write("keep on top: " + e); }
        };
        _check.Start();
    }

    /// <summary>
    /// Another app's window came forward, or one of ours is being minimised. Lyricod's own windows
    /// coming forward (Settings, the tray panel) leave the overlays where they are.
    /// </summary>
    [UnmanagedCallersOnly]
    static void OnWindowEvent(nint hook, uint ev, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (ev == Win32.EVENT_SYSTEM_MINIMIZESTART)
            {
                foreach (var overlay in Showing.ToArray())
                    if (overlay._hwnd == hwnd) overlay.KeepOnTop();
                return;
            }
            Win32.GetWindowThreadProcessId(hwnd, out uint process);
            if (process == OwnProcess) return;
            GuardAll(process);
        }
        catch (Exception e) { Log.Write("keep on top: " + e); }
    }

    public nint Handle => _hwnd;

    /// <summary>
    /// The screens changed (a resolution, their arrangement, one plugged in or out) or the window's
    /// scale did: whoever placed the window by the screen's size places it again. Raised on the UI
    /// thread, after the message that said so has been dealt with.
    /// </summary>
    public event Action? DisplayChanged;

    void OnDisplayChanged()
    {
        try
        {
            _window.DispatcherQueue.TryEnqueue(() =>
            {
                try { DisplayChanged?.Invoke(); }
                catch (Exception e) { Log.Write("display change: " + e); }
            });
        }
        catch (Exception e) { Log.Write("display change: " + e); }
    }

    /// <summary>No DWM border colour or rounded corners (Windows 11 draws both round every window).
    /// Run again after showing: Windows resets them.</summary>
    public void StripFrame() => Win32.RemoveFrame(_hwnd);

    /// <summary>
    /// Makes the whole window content, with no frame. The presenter insists on a dialog frame
    /// (Windows 11 draws it as a thin light outline) and undoes direct style changes, so the
    /// frame is refused where Windows asks for it: no frame area, no frame painting, and frame
    /// bits dropped from any style change.
    /// </summary>
    WindowMessageMonitor SuppressFrame()
    {
        var messages = new WindowMessageMonitor(_window);
        messages.WindowMessageReceived += (_, e) =>
        {
            switch (e.Message.MessageId)
            {
                case 0x0083 when (long)e.Message.WParam != 0: // WM_NCCALCSIZE: client area = whole window
                    e.Result = 0;
                    e.Handled = true;
                    break;
                case 0x0085: // WM_NCPAINT
                    e.Result = 0;
                    e.Handled = true;
                    break;
                case 0x0086: // WM_NCACTIVATE: don't repaint a frame
                    e.Result = 1;
                    e.Handled = true;
                    break;
                case 0x0112 when ((long)e.Message.WParam & 0xFFF0) == 0xF020: // WM_SYSCOMMAND SC_MINIMIZE: an overlay doesn't minimise
                    e.Result = 0;
                    e.Handled = true;
                    break;
                case 0x007E or 0x02E0: // WM_DISPLAYCHANGE, WM_DPICHANGED: left to Windows and WinUI, and passed on
                    OnDisplayChanged();
                    break;
                case 0x007C when (long)e.Message.WParam == -16: // WM_STYLECHANGING for GWL_STYLE
                    const int frame = 0x00C00000 | 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000; // CAPTION THICKFRAME SYSMENU MINIMIZEBOX MAXIMIZEBOX
                    nint styles = (nint)(long)e.Message.LParam; // STYLESTRUCT { styleOld, styleNew }
                    Marshal.WriteInt32(styles, 4, Marshal.ReadInt32(styles, 4) & ~frame);
                    break;
            }
        };
        Win32.RefreshFrame(_hwnd);
        return messages;
    }

    /// <summary>Click-through (clicks reach whatever is underneath) or not.</summary>
    public void SetClickThrough(bool through)
    {
        var style = _window.GetExtendedWindowStyle() | ExtendedWindowStyle.Layered | ExtendedWindowStyle.ToolWindow | ExtendedWindowStyle.NoActivate;
        style = through ? style | ExtendedWindowStyle.Transparent : style & ~ExtendedWindowStyle.Transparent;
        Win32.SetExtendedStyle(_hwnd, (long)style);
        // A layered window needs its opacity set once, or it doesn't draw.
        Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, Win32.LWA_ALPHA);
    }

    public double Scale => Win32.Scale(_hwnd);
}
