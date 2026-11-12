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
/// the island. In the lyrics window it can also draw the line as its words, each rising a little
/// as it's sung (<see cref="Lift"/>).
/// </summary>
sealed class SweepPainter
{
    /// <summary>The soft edge between sung and unsung (view pixels).</summary>
    public const double Edge = 8;

    readonly Compositor _compositor;
    readonly CompositionLinearGradientBrush _gradient;
    readonly SpriteVisual _sprite;
    readonly CompositionMaskBrush _masked;
    readonly TextBlock _text;
    readonly Panel _host;
    SungEffect _effect = SungEffect.Sweep;
    Windows.UI.Color _color = Colors.White;
    /// <summary>The row's words while they're drawn one by one, to rise as they're sung; empty otherwise.</summary>
    readonly List<Lifted> _lifted = [];
    /// <summary>How far a word at its top is raised (view pixels) and how much it has grown.</summary>
    double _rise;
    const double Growth = 0.04;

    /// <summary>One word of the row: the row drawn again, cut down to that word, so it can move by itself.</summary>
    sealed record Lifted(LiftWord Word, ContainerVisual Holder, SpriteVisual Unsung, SpriteVisual Sung, double Centre);

    /// <param name="text">The line, drawn in the unsung colour.</param>
    /// <param name="host">An empty panel laid over <paramref name="text"/> at the same origin.</param>
    public SweepPainter(TextBlock text, Panel host)
    {
        _text = text;
        _host = host;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _gradient = _compositor.CreateLinearGradientBrush();
        _gradient.MappingMode = CompositionMappingMode.Absolute;
        // The ramp ends at the gradient's offset: everything left of it minus the edge is sung.
        _gradient.StartPoint = new Vector2((float)-Edge, 0);
        _gradient.EndPoint = Vector2.Zero;
        _gradient.ColorStops.Add(_compositor.CreateColorGradientStop(0, Colors.White));
        _gradient.ColorStops.Add(_compositor.CreateColorGradientStop(1, Colors.Transparent));
        _masked = _compositor.CreateMaskBrush();
        _masked.Source = _gradient;
        _masked.Mask = text.GetAlphaMask();
        _sprite = _compositor.CreateSpriteVisual();
        _sprite.Brush = _masked;
        ElementCompositionPreview.SetElementChildVisual(host, _sprite);
        text.SizeChanged += (_, e) => Resize(new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));
    }

    void Resize(Vector2 size)
    {
        _sprite.Size = size;
        foreach (var word in _lifted)
        {
            word.Unsung.Size = word.Sung.Size = size;
            // A word grows from the middle of its letters, at about the line they stand on.
            word.Holder.CenterPoint = new Vector3((float)word.Centre, size.Y * 0.78f, 0);
        }
    }

    /// <summary>
    /// Draws the row as its words rather than as one text, so each can rise a little and grow as
    /// it's sung, then settle back (<see cref="WordLift"/>). Every word is the whole row again, cut
    /// down to that word: at rest they add up to exactly the text, however it was shaped. The text
    /// itself then has to be all but hidden by the caller (not quite: a fully transparent text has no
    /// shape to draw from), since the words now draw its unsung part too, at <paramref name="dim"/>.
    /// False, and nothing changes, for a row without word timing.
    /// </summary>
    /// <param name="rise">How far a word at its top is raised (view pixels).</param>
    public bool Lift(LyricLine piece, Func<string, double> measure, double dim, double rise)
    {
        if (_lifted.Count > 0) return true;
        var words = WordLift.Words(piece);
        if (words.Count == 0) return false;
        var spans = WordLift.Spans(words, piece.Text, measure);
        _rise = rise;
        var unsung = _compositor.CreateMaskBrush();
        unsung.Source = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(dim * 255), 255, 255, 255));
        unsung.Mask = _text.GetAlphaMask();
        var root = _compositor.CreateContainerVisual();
        for (int k = 0; k < words.Count; k++)
        {
            var holder = _compositor.CreateContainerVisual();
            // Each word keeps its own width of the row; the first and last also what's beyond the ends.
            holder.Clip = _compositor.CreateRectangleClip(
                k == 0 ? -Beyond : (float)spans[k].Left, -Beyond, k == words.Count - 1 ? 100000 : (float)spans[k].Right, Beyond * 10);
            var under = _compositor.CreateSpriteVisual();
            under.Brush = unsung;
            var over = _compositor.CreateSpriteVisual();
            over.Brush = _masked;
            holder.Children.InsertAtTop(under);
            holder.Children.InsertAtTop(over);
            root.Children.InsertAtTop(holder);
            _lifted.Add(new Lifted(words[k], holder, under, over, spans[k].Centre));
        }
        ElementCompositionPreview.SetElementChildVisual(_host, root);
        Resize(new Vector2((float)_text.ActualWidth, (float)_text.ActualHeight));
        return true;
    }

    /// <summary>How far past the row's edges a word's cut reaches (view pixels).</summary>
    const float Beyond = 200;

    /// <summary>Puts each word where it is at <paramref name="position"/> and, while playing, moves it through the rest of its lift.</summary>
    void AnimateLift(double position, bool playing)
    {
        if (_lifted.Count == 0) return;
        var linear = _compositor.CreateLinearEasingFunction();
        foreach (var word in _lifted)
        {
            word.Holder.StopAnimation("Offset");
            word.Holder.StopAnimation("Scale");
            var (times, values) = WordLift.Plan(word.Word, position);
            word.Holder.Offset = Raised(values[0]);
            word.Holder.Scale = Grown(values[0]);
            double total = times[^1];
            if (!playing || total < 0.01) continue;
            var offset = _compositor.CreateVector3KeyFrameAnimation();
            var scale = _compositor.CreateVector3KeyFrameAnimation();
            for (int k = 0; k < times.Count; k++)
            {
                offset.InsertKeyFrame((float)(times[k] / total), Raised(values[k]), linear);
                scale.InsertKeyFrame((float)(times[k] / total), Grown(values[k]), linear);
            }
            offset.Duration = scale.Duration = TimeSpan.FromSeconds(total);
            word.Holder.StartAnimation("Offset", offset);
            word.Holder.StartAnimation("Scale", scale);
        }
    }

    Vector3 Raised(double lift) => new(0, (float)(-lift * _rise), 0);

    static Vector3 Grown(double lift) => new((float)(1 + lift * Growth), (float)(1 + lift * Growth), 1);

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
    /// Lift: the sweep, over words that rise as they're sung (where <see cref="Lift"/> was asked for).
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
        AnimateLift(position, playing);
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
