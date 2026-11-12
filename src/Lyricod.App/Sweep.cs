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
/// as it's sung and staying up, softly lit, until the line is over (<see cref="Lift"/>).
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
    /// <summary>How far a word that's up is raised (view pixels), how much one grows as it's sung, and how strong the light behind the sung part is.</summary>
    double _rise;
    const double Growth = 0.03;
    const double Glow = 0.45;
    /// <summary>
    /// The soft light behind the sung part of a row whose words rise: the text's blurred shape,
    /// several times over, each shown up to a little further back from where the singing is, so the
    /// light ends softly there rather than at an edge. It's blurred once; only how much of it shows moves.
    /// </summary>
    readonly List<(SpriteVisual Sprite, DropShadow Shadow, RectangleClip Shown)> _halo = [];
    const int HaloLayers = 4;
    /// <summary>How far back from the last each layer of the light ends, and how far the light spreads beyond the text (view pixels).</summary>
    const float HaloStep = 7;
    float _haloReach;
    /// <summary>Where the song was when the words were last set moving, when that was, and whether it was playing: where they are now.</summary>
    double _liftedAt;
    long _liftedWhen;
    bool _liftedPlaying;

    /// <summary>
    /// One word of the row: the row drawn again, cut down to that word, so it can move by itself.
    /// </summary>
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
        foreach (var (sprite, _, _) in _halo) sprite.Size = size;
        foreach (var word in _lifted)
        {
            word.Unsung.Size = word.Sung.Size = size;
            // A word grows from the middle of its letters, across and up and down: growing doesn't
            // carry it any higher, which it would then have to come back from.
            word.Holder.CenterPoint = new Vector3((float)word.Centre, size.Y * 0.55f, 0);
        }
    }

    /// <summary>
    /// Draws the row as its words rather than as one text, so each can rise a little as it's sung
    /// and stay up, softly lit, until the line is over (<see cref="WordLift"/>, <see cref="Release"/>);
    /// it also swells a little while it's sung. Every word is the whole row again, cut
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
        var shape = _text.GetAlphaMask();
        var unsung = _compositor.CreateMaskBrush();
        unsung.Source = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(dim * 255), 255, 255, 255));
        unsung.Mask = shape;
        var root = _compositor.CreateContainerVisual();
        float blur = (float)Math.Clamp(rise * 3.5, 6, 14);
        _haloReach = blur * 1.5f;
        for (int k = 0; k < HaloLayers; k++)
        {
            // A shadow of the text's shape, in the sung colour, from a sprite that draws nothing itself.
            var shadow = _compositor.CreateDropShadow();
            shadow.Mask = shape;
            shadow.Color = _color;
            shadow.BlurRadius = blur;
            shadow.Opacity = HaloOpacity;
            shadow.Offset = Vector3.Zero;
            var sprite = _compositor.CreateSpriteVisual();
            sprite.Shadow = shadow;
            // Where the sung words are: up.
            var layer = _compositor.CreateContainerVisual();
            layer.Offset = new Vector3(0, (float)-rise, 0);
            var shown = _compositor.CreateRectangleClip(-Beyond, -Beyond, -Beyond, Beyond * 10);
            layer.Clip = shown;
            layer.Children.InsertAtTop(sprite);
            root.Children.InsertAtTop(layer);
            _halo.Add((sprite, shadow, shown));
        }
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

    /// <summary>Each layer of the light's share: together they're as strong as <see cref="Glow"/>.</summary>
    static readonly float HaloOpacity = (float)(1 - Math.Pow(1 - Glow, 1.0 / HaloLayers));

    /// <summary>
    /// Where layer <paramref name="layer"/> of the light ends for <paramref name="sung"/> pixels sung:
    /// before the text (and its own spread) when nothing is, past them when all of it is.
    /// </summary>
    float HaloEnd(int layer, double sung, double width) =>
        (float)(-_haloReach + sung * (width + 2 * _haloReach + (HaloLayers - 1) * HaloStep) / Math.Max(width, 1) - layer * HaloStep);

    /// <summary>Puts each word where it is at <paramref name="position"/> and, while playing, moves it through the rest of its lift.</summary>
    void AnimateLift(double position, bool playing)
    {
        if (_lifted.Count == 0) return;
        _liftedAt = position;
        _liftedWhen = System.Diagnostics.Stopwatch.GetTimestamp();
        _liftedPlaying = playing;
        foreach (var word in _lifted)
        {
            var (times, up, swell) = WordLift.Plan(word.Word, position);
            Put(word, up[0], swell[0]);
            if (playing) Move(word, times, up, swell);
        }
    }

    /// <summary>
    /// The line is over: its words come back down together, from wherever each is, and their light
    /// goes out. False when none was up (nothing to wait for). The row is to be drawn plain once
    /// they're down, <see cref="WordLift.Settle"/> later.
    /// </summary>
    public bool Release()
    {
        double position = _liftedAt + (_liftedPlaying ? System.Diagnostics.Stopwatch.GetElapsedTime(_liftedWhen).TotalSeconds : 0);
        bool any = false;
        foreach (var word in _lifted)
        {
            double up = WordLift.Up(word.Word, position), swell = WordLift.Swell(word.Word, position);
            Put(word, up, swell);
            if (up < 0.01 && swell < 0.01) continue;
            any = true;
            var (times, down) = WordLift.Release(up);
            Move(word, times, down, WordLift.Release(swell).Values);
        }
        if (!any) return false;
        var (fades, light) = WordLift.Release(HaloOpacity);
        var linear = _compositor.CreateLinearEasingFunction();
        foreach (var (_, shadow, _) in _halo)
        {
            var fade = _compositor.CreateScalarKeyFrameAnimation();
            for (int k = 0; k < fades.Count; k++) fade.InsertKeyFrame((float)(fades[k] / fades[^1]), (float)light[k], linear);
            fade.Duration = TimeSpan.FromSeconds(fades[^1]);
            shadow.StartAnimation("Opacity", fade);
        }
        return true;
    }

    /// <summary>Stops <paramref name="word"/> where it's <paramref name="up"/> and has grown by <paramref name="swell"/>.</summary>
    void Put(Lifted word, double up, double swell)
    {
        word.Holder.StopAnimation("Offset");
        word.Holder.StopAnimation("Scale");
        word.Holder.Offset = Raised(up);
        word.Holder.Scale = Grown(swell);
    }

    /// <summary>Moves <paramref name="word"/> through keyframes (seconds from now), straight from each to the next.</summary>
    void Move(Lifted word, List<double> times, List<double> up, List<double> swell)
    {
        double total = times[^1];
        if (total < 0.01) return;
        var linear = _compositor.CreateLinearEasingFunction();
        var offset = _compositor.CreateVector3KeyFrameAnimation();
        var scale = _compositor.CreateVector3KeyFrameAnimation();
        for (int k = 0; k < times.Count; k++)
        {
            float at = (float)(times[k] / total);
            offset.InsertKeyFrame(at, Raised(up[k]), linear);
            scale.InsertKeyFrame(at, Grown(swell[k]), linear);
        }
        offset.Duration = scale.Duration = TimeSpan.FromSeconds(total);
        word.Holder.StartAnimation("Offset", offset);
        word.Holder.StartAnimation("Scale", scale);
    }

    Vector3 Raised(double up) => new(0, (float)(-up * _rise), 0);

    static Vector3 Grown(double swell) => new((float)(1 + swell * Growth), (float)(1 + swell * Growth), 1);

    public Windows.UI.Color Color
    {
        set
        {
            _color = value;
            _gradient.ColorStops[0].Color = value;
            _gradient.ColorStops[1].Color = Windows.UI.Color.FromArgb(0, value.R, value.G, value.B);
            if (_sprite.Shadow is DropShadow glow) glow.Color = value;
            foreach (var (_, shadow, _) in _halo) shadow.Color = value;
        }
    }

    /// <summary>
    /// Sweep: the colour moves word by word. Glow: the same, and the sung part glows in its colour
    /// (a soft shadow of the sung shape). Line: the whole line lights as it starts. As on the Mac.
    /// Lift: the sweep, over words that rise as they're sung and stay up, softly lit (where <see cref="Lift"/> was asked for).
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
        for (int k = 0; k < _halo.Count; k++)
        {
            _halo[k].Shown.StopAnimation("Right");
            _halo[k].Shown.Right = HaloEnd(k, sung, width);
        }
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
        // The light follows, a little behind.
        for (int layer = 0; layer < _halo.Count; layer++)
        {
            var shown = _compositor.CreateScalarKeyFrameAnimation();
            for (int k = 0; k < times.Count; k++)
                shown.InsertKeyFrame((float)(times[k] / total), HaloEnd(layer, values[k], width), linear);
            shown.Duration = animation.Duration;
            _halo[layer].Shown.StartAnimation("Right", shown);
        }
    }

    /// <summary>
    /// Where the fade ends for <paramref name="sung"/> pixels sung: at 0 when nothing is, and just
    /// past the text when all of it is, so both ends are crisp (as on the Mac).
    /// </summary>
    static Vector2 OffsetFor(double sung, double width) => new((float)(sung * (width + Edge) / Math.Max(width, 1)), 0);
}
