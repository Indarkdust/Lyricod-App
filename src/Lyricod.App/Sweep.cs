using System.Numerics;
using Lyricod.Core;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Lyricod;

/// <summary>
/// The sung colour over a line of text, in the text's shape, fading out over a soft edge where
/// the singing is (as on the Mac). It's a gradient masked by the text and slid across on the
/// compositor: no per-frame work in the app. Used by the desktop strip, the lyrics window and
/// the island.
/// </summary>
sealed class SweepPainter
{
    /// <summary>The soft edge between sung and unsung (view pixels).</summary>
    public const double Edge = 8;

    readonly Compositor _compositor;
    readonly CompositionLinearGradientBrush _gradient;
    readonly SpriteVisual _sprite;
    SungEffect _effect = SungEffect.Sweep;
    Windows.UI.Color _color = Colors.White;

    /// <param name="text">The line, drawn in the unsung colour.</param>
    /// <param name="host">An empty panel laid over <paramref name="text"/> at the same origin.</param>
    public SweepPainter(TextBlock text, Panel host)
    {
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _gradient = _compositor.CreateLinearGradientBrush();
        _gradient.MappingMode = CompositionMappingMode.Absolute;
        // The ramp ends at the gradient's offset: everything left of it minus the edge is sung.
        _gradient.StartPoint = new Vector2((float)-Edge, 0);
        _gradient.EndPoint = Vector2.Zero;
        _gradient.ColorStops.Add(_compositor.CreateColorGradientStop(0, Colors.White));
        _gradient.ColorStops.Add(_compositor.CreateColorGradientStop(1, Colors.Transparent));
        var masked = _compositor.CreateMaskBrush();
        masked.Source = _gradient;
        masked.Mask = text.GetAlphaMask();
        _sprite = _compositor.CreateSpriteVisual();
        _sprite.Brush = masked;
        ElementCompositionPreview.SetElementChildVisual(host, _sprite);
        text.SizeChanged += (_, e) => _sprite.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
    }

    public Windows.UI.Color Color
    {
        set
        {
            _color = value;
            _gradient.ColorStops[0].Color = value;
            _gradient.ColorStops[1].Color = Windows.UI.Color.FromArgb(0, value.R, value.G, value.B);
            if (_sprite.Shadow is DropShadow glow) glow.Color = value;
        }
    }

    /// <summary>
    /// Sweep: the colour moves word by word. Glow: the same, and the sung part glows in its colour
    /// (a soft shadow of the sung shape). Line: the whole line lights as it starts. As on the Mac.
    /// </summary>
    public SungEffect Effect
    {
        set
        {
            _effect = value;
            if (value == SungEffect.Glow && _sprite.Shadow == null)
            {
                var glow = _compositor.CreateDropShadow();
                glow.Color = _color;
                glow.BlurRadius = 12;
                glow.Opacity = 0.9f;
                glow.Offset = Vector3.Zero;
                glow.SourcePolicy = CompositionDropShadowSourcePolicy.InheritFromVisualContent;
                _sprite.Shadow = glow;
            }
            else if (value != SungEffect.Glow) _sprite.Shadow = null;
        }
    }

    /// <summary>Shows <paramref name="sung"/> of <paramref name="width"/> pixels sung, with no animation.</summary>
    public void Set(double sung, double width)
    {
        _gradient.StopAnimation("Offset");
        _gradient.Offset = OffsetFor(sung, width);
    }

    /// <summary>
    /// Sets the sung width for <paramref name="position"/> and, while playing, animates it through
    /// the rest of the piece at each word's timing. A piece without word timing lights up as it starts.
    /// </summary>
    public void Animate(LyricLine piece, double width, double position, bool playing, Func<string, double> measure)
    {
        // The whole line at once: no word stops, so it lights as it starts.
        List<SweepStop> stops = _effect == SungEffect.Line ? [] : Sweep.Stops(piece, width, measure);
        Set(Sweep.SungWidth(stops, piece, width, position), width);
        if (!playing) return;
        var (times, values) = Sweep.Plan(stops, piece, width, position);
        double total = times[^1];
        if (total < 0.01) return;
        var linear = _compositor.CreateLinearEasingFunction();
        var animation = _compositor.CreateVector2KeyFrameAnimation();
        for (int k = 0; k < times.Count; k++)
            animation.InsertKeyFrame((float)(times[k] / total), OffsetFor(values[k], width), linear);
        animation.Duration = TimeSpan.FromSeconds(total);
        _gradient.StartAnimation("Offset", animation);
    }

    /// <summary>
    /// Where the fade ends for <paramref name="sung"/> pixels sung: at 0 when nothing is, and just
    /// past the text when all of it is, so both ends are crisp (as on the Mac).
    /// </summary>
    static Vector2 OffsetFor(double sung, double width) => new((float)(sung * (width + Edge) / Math.Max(width, 1)), 0);
}
