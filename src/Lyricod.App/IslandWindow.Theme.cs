using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Lyricod;

/// <summary>
/// The island's theme (Settings › Lyricod Island › Theme): Classic black, or Lyricod Glass, a
/// see-through tint. Glass's 3D effect lights it gently, as glass catches light: a faint shine along
/// the top edge (no outline), a soft highlight behind the lyrics, and (floating) a shadow underneath.
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

    /// <summary>Glass with its 3D effect on, and not being moved (a floating island being moved is plain dark grey).</summary>
    bool GlassLit => Glass && _settings.GlassShine && !Movable;

    /// <summary>The pill's fill, highlight, bevel and shadow.</summary>
    void ApplyTheme()
    {
        bool lit = GlassLit;
        Rim.Visibility = lit ? Visibility.Visible : Visibility.Collapsed;
        // Light catching the top edge only, gone a third of the way down: no outline round the island.
        Rim.BorderBrush ??= Vertical((Microsoft.UI.Colors.White, 80, 0), (Microsoft.UI.Colors.White, 0, 0.35));
        Sheen.Background = lit ? Highlight() : null;
        UpdateShadow();
        if (Movable) { Pill.Background = new SolidColorBrush(MovingColour); return; }
        if (!Glass) { Pill.Background = new SolidColorBrush(Microsoft.UI.Colors.Black); return; }
        // The album's colour is tuned to stand out on black; darkened, white lyrics stay readable on it.
        var tint = _settings.GlassTint == GlassTint.Album && _model.ArtworkColor is { } c ? Darken(c, 0.45) : NeutralGlass;
        byte alpha = (byte)Math.Round(255 * (1 - _settings.GlassTransparency / 100.0));
        Pill.Background = new SolidColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
    }

    /// <summary>Light falling on the top of the glass: brightest at the top centre, gone by the bottom.</summary>
    static RadialGradientBrush Highlight()
    {
        var brush = new RadialGradientBrush { Center = new Point(0.5, -0.1), GradientOrigin = new Point(0.5, -0.1), RadiusX = 0.8, RadiusY = 1.0 };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(38, 255, 255, 255), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(10, 255, 255, 255), Offset = 0.6 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 });
        return brush;
    }

    /// <summary>
    /// The shadow under a floating glass island, shaped like the pill and kept its size as it grows
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

    /// <summary>A top-to-bottom gradient through (colour, opacity, offset) stops.</summary>
    static LinearGradientBrush Vertical(params (Color Colour, byte Alpha, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (colour, alpha, offset) in stops)
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(alpha, colour.R, colour.G, colour.B), Offset = offset });
        return brush;
    }

    static Color Darken(Color c, double k) => Color.FromArgb(255, (byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
}
