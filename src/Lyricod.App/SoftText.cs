using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Lyricod;

/// <summary>
/// Text that can go soft, for lines away from the one being sung (as the Mac blurs them). The
/// blurred copy is a shadow of the text's own shape drawn by the compositor, with the sharp text
/// faded out under it: WinUI has no blur for elements, and this costs nothing per frame. Changes
/// in blur animate. The text itself stays opaque and a wrapper fades instead: a text's shape
/// includes its own opacity, so fading the text would take the blurred copy with it.
/// </summary>
sealed class SoftText
{
    static readonly TimeSpan Transition = TimeSpan.FromSeconds(0.35);

    readonly DropShadow _shadow;
    readonly Grid _fader;
    readonly Compositor _compositor;
    double _radius;

    public SoftText(TextBlock text)
    {
        Text = text;
        var host = new Grid { IsHitTestVisible = false };
        _fader = new Grid { OpacityTransition = new ScalarTransition { Duration = Transition } };
        _fader.Children.Add(text);
        Element = new Grid { HorizontalAlignment = text.HorizontalAlignment };
        Element.Children.Add(host);
        Element.Children.Add(_fader);
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _shadow = _compositor.CreateDropShadow();
        _shadow.Mask = text.GetAlphaMask();
        _shadow.Color = Colors.White;
        _shadow.Offset = Vector3.Zero;
        _shadow.Opacity = 0;
        var sprite = _compositor.CreateSpriteVisual();
        sprite.Shadow = _shadow;
        ElementCompositionPreview.SetElementChildVisual(host, sprite);
        text.SizeChanged += (_, e) => sprite.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
    }

    public Grid Element { get; }
    public TextBlock Text { get; }

    /// <summary>Softens the text by <paramref name="radius"/> points (0 is sharp).</summary>
    public void Blur(double radius)
    {
        if (Math.Abs(radius - _radius) < 0.01) return;
        bool soft = radius >= 0.5;
        _radius = radius;
        // Not quite 0: WinUI doesn't draw a fully transparent text's shape, and the blurred copy needs it.
        _fader.Opacity = soft ? 0.01 : 1;
        Animate("Opacity", soft ? 1 : 0);
        // A shadow's blur radius spreads about twice as far as a Gaussian's; this matches the Mac's look.
        if (soft) Animate("BlurRadius", (float)(radius * 2));
    }

    void Animate(string property, float to)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, to, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.3f, 1)));
        animation.Duration = Transition;
        _shadow.StartAnimation(property, animation);
    }
}
