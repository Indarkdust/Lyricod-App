using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Lyricod;

/// <summary>
/// The island's theme (Settings › Lyricod Island › Theme): Classic black, or Lyricod Glass, a
/// see-through tint. With the liquid effect, glass's 3D effect lights it as glass catches light: the
/// liquid glass draws the light along its edge itself, following the curve of its lens, and a
/// floating island gets a shadow underneath.
/// </summary>
public sealed partial class IslandWindow
{
    static readonly Color MovingColour = Color.FromArgb(255, 0x26, 0x26, 0x2A);
    static readonly Color NeutralGlass = Color.FromArgb(255, 18, 18, 22);

    SpriteVisual? _shadow;
    CompositionRoundedRectangleGeometry? _shadowShape;
    ShapeVisual? _shadowCaster;
    CompositionVisualSurface? _shadowSurface;

    bool Glass => _settings.IslandTheme == IslandTheme.Glass;

    /// <summary>The liquid glass is showing, with its 3D effect on (it doesn't run while a floating island is being moved).</summary>
    bool GlassLit => _liquid != null && _settings.GlassShine;

    /// <summary>The pill's fill and shadow, and the glass effect behind it.</summary>
    void ApplyTheme()
    {
        bool frosted = ApplyFrost();
        bool liquid = ApplyLiquid();
        UpdateShadow();
        if (Movable) { Pill.Background = new SolidColorBrush(MovingColour); return; }
        if (!Glass) { Pill.Background = new SolidColorBrush(Microsoft.UI.Colors.Black); return; }
        // The liquid layer draws the glass, tint and all.
        if (liquid) { Pill.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); return; }
        var tint = GlassColour();
        double clear = _settings.GlassTransparency / 100.0;
        byte alpha = (byte)Math.Round(255 * (1 - clear));
        if (frosted)
        {
            // As see-through as acrylic goes: its tint and brightness layers fade with transparency, down
            // to almost only the blur; the pill adds just a touch of tint on top.
            _frost!.SetTint(tint, (float)((1 - clear) * 0.35), (float)((1 - clear) * 0.45));
            alpha = (byte)(alpha * 0.12);
        }
        Pill.Background = new SolidColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
    }

    /// <summary>The glass's colour: neutral, or the album's darkened (it's tuned to stand out on black) so white lyrics stay readable on it.</summary>
    Color GlassColour() => _settings.GlassTint == GlassTint.Album && _model.ArtworkColor is { } c ? Darken(c, 0.45) : NeutralGlass;

    /// <summary>The glass's colour at its opacity (the transparency setting).</summary>
    Color GlassTintColour()
    {
        var tint = GlassColour();
        return Color.FromArgb((byte)Math.Round(255 * (1 - _settings.GlassTransparency / 100.0)), tint.R, tint.G, tint.B);
    }

    /// <summary>
    /// The shadow under a floating liquid glass island, shaped like the pill and kept its size as it grows
    /// and shrinks. It's drawn from a rounded-rectangle mask, so nothing but the shadow shows.
    /// </summary>
    void UpdateShadow()
    {
        ShadowHost.HorizontalAlignment = Pill.HorizontalAlignment;
        ShadowHost.VerticalAlignment = Pill.VerticalAlignment;
        bool on = GlassLit && Floating;
        ShadowHost.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var size = new Vector2((float)Pill.ActualWidth, (float)Pill.ActualHeight);
        if (!on || size.X <= 0 || size.Y <= 0) return;
        ShadowHost.Width = size.X;
        ShadowHost.Height = size.Y;
        if (_shadow == null)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(ShadowHost).Compositor;
            _shadowShape = compositor.CreateRoundedRectangleGeometry();
            var shape = compositor.CreateSpriteShape(_shadowShape);
            shape.FillBrush = compositor.CreateColorBrush(Microsoft.UI.Colors.Black);
            _shadowCaster = compositor.CreateShapeVisual();
            _shadowCaster.Shapes.Add(shape);
            _shadowSurface = compositor.CreateVisualSurface();
            _shadowSurface.SourceVisual = _shadowCaster;
            var shadow = compositor.CreateDropShadow();
            shadow.Mask = compositor.CreateSurfaceBrush(_shadowSurface);
            shadow.Color = Microsoft.UI.Colors.Black;
            shadow.BlurRadius = 18;
            shadow.Opacity = 0.45f;
            shadow.Offset = new Vector3(0, 6, 0);
            _shadow = compositor.CreateSpriteVisual();
            _shadow.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(ShadowHost, _shadow);
        }
        float radius = (float)Pill.CornerRadius.BottomLeft;
        _shadowShape!.Size = size;
        _shadowShape.CornerRadius = new Vector2(radius, radius);
        _shadowCaster!.Size = size;
        _shadowSurface!.SourceSize = size;
        _shadow.Size = size;
    }

    static Color Darken(Color c, double k) => Color.FromArgb(255, (byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
}
