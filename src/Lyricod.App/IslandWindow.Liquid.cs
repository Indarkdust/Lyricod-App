using System.Numerics;
using Lyricod.Platform;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;

namespace Lyricod;

/// <summary>
/// Lyricod Glass's liquid effect (see <see cref="LiquidGlass"/>). It runs only while
/// it's the chosen glass effect and the island shows (not while the pointer hides it or it's being
/// moved); only then is the island hidden from screen capture.
/// </summary>
public sealed partial class IslandWindow
{
    LiquidGlass? _liquid;

    bool WantsLiquid => Glass && _settings.GlassEffect == GlassEffect.Liquid && LiquidGlass.IsSupported && !Movable;

    /// <summary>Starts, follows or stops the liquid glass; returns whether it's showing.</summary>
    bool ApplyLiquid()
    {
        bool on = WantsLiquid && _shown && !(_hovered && _hoverAction == IslandHover.Hide) && _liquid?.Failed != true;
        if (!on)
        {
            StopLiquid();
            return false;
        }
        if (_liquid == null)
        {
            // It would capture itself otherwise; visible to screenshots again as soon as it stops.
            Win32.SetWindowDisplayAffinity(_overlay.Handle, Win32.WDA_EXCLUDEFROMCAPTURE);
            // If it fails later (the graphics card reset, the screen went away), fall back to clear glass.
            _liquid = new LiquidGlass(ElementCompositionPreview.GetElementVisual(LiquidHost).Compositor, DispatcherQueue,
                () => DispatcherQueue.TryEnqueue(() => { if (_liquid?.Failed == true) ApplyTheme(); }));
            ElementCompositionPreview.SetElementChildVisual(LiquidHost, _liquid.Visual);
        }
        UpdateLiquid();
        if (!_liquid.Failed) return true;
        StopLiquid();
        return false;
    }

    void StopLiquid()
    {
        if (_liquid == null) return;
        ElementCompositionPreview.SetElementChildVisual(LiquidHost, null);
        _liquid.Dispose();
        _liquid = null;
        Win32.SetWindowDisplayAffinity(_overlay.Handle, Win32.WDA_NONE);
    }

    /// <summary>Keeps the liquid glass on what's behind the island as it moves, grows and changes look.</summary>
    void UpdateLiquid()
    {
        if (_liquid == null) return;
        var (left, top, width, height) = PillBounds();
        if (width <= 0 || height <= 0) return;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var screen = display.OuterBounds;
        _liquid.Update(Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(display.DisplayId), new PointInt32(screen.X, screen.Y),
            new RectInt32((int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(width), (int)Math.Round(height)),
            new Vector2((float)Pill.ActualWidth, (float)Pill.ActualHeight), (float)Pill.CornerRadius.BottomLeft, !Floating,
            blur: _settings.LiquidBlur / 100f * 16, refraction: _settings.LiquidRefraction / 100f * 14, GlassTintColour(), shine: GlassLit);
    }
}
