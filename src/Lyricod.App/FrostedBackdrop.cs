using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Lyricod;

/// <summary>
/// Windows' acrylic (the frosted material of Start and the quick settings) behind a window that
/// never takes focus. Acrylic turns into a flat colour for inactive windows, so this one is always
/// treated as active.
/// </summary>
sealed class FrostedBackdrop : SystemBackdrop
{
    static readonly Lazy<bool> Supported = new(DesktopAcrylicController.IsSupported);

    readonly SystemBackdropConfiguration _configuration = new() { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
    DesktopAcrylicController? _controller;
    Windows.UI.Color _tint = Windows.UI.Color.FromArgb(255, 18, 18, 22);
    float _tintOpacity = 0.4f, _luminosityOpacity = 0.5f;

    /// <summary>Whether this PC's Windows draws acrylic.</summary>
    public static bool IsSupported => Supported.Value;

    /// <summary>The colour laid over the blur, and how strongly.</summary>
    public void SetTint(Windows.UI.Color tint, float tintOpacity, float luminosityOpacity)
    {
        (_tint, _tintOpacity, _luminosityOpacity) = (tint, tintOpacity, luminosityOpacity);
        Apply();
    }

    void Apply()
    {
        if (_controller == null) return;
        _controller.TintColor = _tint;
        _controller.TintOpacity = _tintOpacity;
        _controller.LuminosityOpacity = _luminosityOpacity;
        _controller.FallbackColor = _tint;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _controller = new DesktopAcrylicController();
        Apply();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }
}
