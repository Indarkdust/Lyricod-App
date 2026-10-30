using Lyricod.Platform;
using Microsoft.UI.Xaml.Media;

namespace Lyricod;

/// <summary>
/// Lyricod Glass's frosted blur (experimental): Windows' acrylic behind the island window, with the
/// window clipped to the pill's shape so the blur takes the island's outline. Windows doesn't smooth
/// a clipped window's edges, so its corners can look slightly stepped. The blur is off while the
/// island is hidden by the pointer, fading away, or being moved.
/// </summary>
public sealed partial class IslandWindow
{
    FrostedBackdrop? _frost;
    SystemBackdrop? _clearBackdrop;
    /// <summary>The acrylic is on.</summary>
    bool _frosted;
    /// <summary>The window is clipped to the pill.</summary>
    bool _shaped;

    bool WantsFrost => Glass && _settings.GlassEffect == GlassEffect.Frosted && FrostedBackdrop.IsSupported && !Movable;

    /// <summary>Puts the acrylic on or off and the window's shape to match; returns whether it's on.</summary>
    bool ApplyFrost()
    {
        bool shaped = WantsFrost;
        bool frosted = shaped && _shown && !(_hovered && _hoverAction == IslandHover.Hide);
        if (frosted != _frosted)
        {
            _frosted = frosted;
            _clearBackdrop ??= SystemBackdrop; // the overlay's own, fully transparent
            SystemBackdrop = frosted ? (_frost ??= new FrostedBackdrop()) : _clearBackdrop;
            _overlay.StripFrame(); // Windows may bring its frame back with a new backdrop
        }
        if (shaped) Shape();
        else if (_shaped)
        {
            Win32.SetWindowRgn(_overlay.Handle, 0, true);
            _shaped = false;
        }
        return frosted;
    }

    /// <summary>Clips the window to the pill (the default island's top corners stay square).</summary>
    void Shape()
    {
        var (left, top, width, height) = PillBounds();
        if (width <= 0 || height <= 0) return;
        var pos = AppWindow.Position;
        int x = (int)Math.Round(left - pos.X), y = (int)Math.Round(top - pos.Y);
        int right = x + (int)Math.Round(width), bottom = y + (int)Math.Round(height);
        int round = (int)Math.Round(2 * Pill.CornerRadius.BottomLeft * _overlay.Scale);
        // Default: start the rounding above the window, so only the bottom corners are round.
        Win32.SetWindowRgn(_overlay.Handle, Win32.CreateRoundRectRgn(x, Floating ? y : y - round, right + 1, bottom + 1, round, round), true);
        _shaped = true;
    }
}
