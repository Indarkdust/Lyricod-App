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
sealed class Overlay
{
    readonly Window _window;
    readonly nint _hwnd;
    // Kept alive for the window's lifetime: it hooks the window's messages.
    readonly WindowMessageMonitor _messages;

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
    }

    public nint Handle => _hwnd;

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
        _window.SetExtendedWindowStyle(style);
        // A layered window needs its opacity set once, or it doesn't draw.
        Win32.SetLayeredWindowAttributes(_hwnd, 0, 255, Win32.LWA_ALPHA);
    }

    public double Scale => Win32.Scale(_hwnd);
}
