using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Lyricod;

/// <summary>
/// The island's theme (Settings › Lyricod Island › Theme): Classic black, or Lyricod Glass, a
/// see-through tint with a soft highlight at the top and a bright rim. Windows can't blur what's
/// behind a shaped, moving overlay like the island, so the glass is clear rather than frosted.
/// </summary>
public sealed partial class IslandWindow
{
    static readonly Color MovingColour = Color.FromArgb(255, 0x26, 0x26, 0x2A);
    static readonly Color NeutralGlass = Color.FromArgb(255, 18, 18, 22);

    bool Glass => _settings.IslandTheme == IslandTheme.Glass;

    /// <summary>The pill's fill and rim. A floating island being moved is dark grey whatever the theme.</summary>
    void ApplyTheme()
    {
        bool shine = Glass && _settings.GlassShine && !Movable;
        Rim.Visibility = shine ? Visibility.Visible : Visibility.Collapsed;
        Rim.BorderBrush ??= Vertical((Microsoft.UI.Colors.White, 140, 0), (Microsoft.UI.Colors.White, 30, 1));
        if (Movable) { Pill.Background = new SolidColorBrush(MovingColour); return; }
        if (!Glass) { Pill.Background = new SolidColorBrush(Microsoft.UI.Colors.Black); return; }
        // The album's colour is tuned to stand out on black; darkened, white lyrics stay readable on it.
        var tint = _settings.GlassTint == GlassTint.Album && _model.ArtworkColor is { } c ? Darken(c, 0.45) : NeutralGlass;
        byte alpha = (byte)Math.Round(255 * (1 - _settings.GlassTransparency / 100.0));
        Pill.Background = shine
            ? Vertical((Mix(tint, Microsoft.UI.Colors.White, 0.22), (byte)Math.Min(255, alpha + 25), 0), (tint, alpha, 0.6))
            : new SolidColorBrush(Color.FromArgb(alpha, tint.R, tint.G, tint.B));
    }

    /// <summary>A top-to-bottom gradient through (colour, opacity, offset) stops.</summary>
    static LinearGradientBrush Vertical(params (Color Colour, byte Alpha, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (colour, alpha, offset) in stops)
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(alpha, colour.R, colour.G, colour.B), Offset = offset });
        return brush;
    }

    static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb(255, (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    static Color Darken(Color c, double k) => Color.FromArgb(255, (byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
}
