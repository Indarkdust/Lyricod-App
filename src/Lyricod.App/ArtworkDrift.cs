using System.Numerics;
using Lyricod.Core;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Lyricod;

/// <summary>
/// The lyrics window's background, from the Mac's (ArtworkDrift.swift): the cover softened once at
/// small size, then copies slowly turned and moved by the compositor: a big one, a smaller one
/// fading out towards its edges that wanders around the middle, and a third that drifts the other
/// way a little faster, so the colours keep mixing. The whole picture slowly breathes in and out.
/// Nothing runs in the app per frame; while the music is paused, all of it pauses in place.
/// </summary>
sealed class ArtworkDrift
{
    readonly Compositor _compositor;
    readonly ContainerVisual _root;
    /// <summary>The copies of the cover now showing; the old set fades out under a new one.</summary>
    ContainerVisual? _current;
    byte[]? _shown;
    Vector2 _size;
    bool _moving = true;

    public ArtworkDrift(UIElement host)
    {
        var hostVisual = ElementCompositionPreview.GetElementVisual(host);
        _compositor = hostVisual.Compositor;
        _root = _compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(host, _root);
        hostVisual.Clip = _compositor.CreateInsetClip();
        ((FrameworkElement)host).SizeChanged += (_, e) => Resize(new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));
    }

    /// <summary>Shows a new cover, cross-fading from the last; null shows none.</summary>
    public async Task SetCover(byte[]? bytes)
    {
        if (ReferenceEquals(bytes, _shown)) return;
        _shown = bytes;
        var surface = bytes == null ? null : await Softened(bytes);
        if (!ReferenceEquals(bytes, _shown)) return; // the song moved on while this was made
        var old = _current;
        _current = null;
        if (surface != null)
        {
            _current = Build(surface);
            _root.Children.InsertAtTop(_current);
            Layout(_current);
            Fade(_current, 1, null);
        }
        if (old != null) Fade(old, 0, () => _root.Children.Remove(old));
    }

    /// <summary>Drifts while music plays; pauses where it is otherwise.</summary>
    public bool Moving
    {
        set
        {
            if (value == _moving) return;
            _moving = value;
            if (_current != null) SetPaused(_current, !value);
        }
    }

    /// <summary>The cover at 128 px, blurred and a little more saturated, as an image surface.</summary>
    static async Task<LoadedImageSurface?> Softened(byte[] bytes)
    {
        try
        {
            const int side = 128;
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes));
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = side, ScaledHeight = side, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
            await Task.Run(() => ArtworkBlur.Soften(pixels, side, side, 6, 1.6));
            var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, side, side, 96, 96, pixels);
            await encoder.FlushAsync();
            output.Seek(0);
            return LoadedImageSurface.StartLoadFromStream(output);
        }
        catch (Exception) { return null; } // not an image Windows can read
    }

    ContainerVisual Build(LoadedImageSurface surface)
    {
        var set = _compositor.CreateContainerVisual();
        set.Opacity = 0;
        var big = _compositor.CreateSpriteVisual();
        big.Brush = Brush(surface);
        set.Children.InsertAtTop(big);
        // The smaller copies fade out towards their edges, so they have no visible border.
        foreach (float opacity in new[] { 1f, 0.8f })
        {
            var mask = _compositor.CreateRadialGradientBrush();
            mask.EllipseRadius = new Vector2(0.5f, 0.5f); // clear by the middle of each edge, as on the Mac
            mask.ColorStops.Add(_compositor.CreateColorGradientStop(0, Colors.Black));
            mask.ColorStops.Add(_compositor.CreateColorGradientStop(0.45f, Colors.Black));
            mask.ColorStops.Add(_compositor.CreateColorGradientStop(1, Colors.Transparent));
            var masked = _compositor.CreateMaskBrush();
            masked.Source = Brush(surface);
            masked.Mask = mask;
            var copy = _compositor.CreateSpriteVisual();
            copy.Brush = masked;
            copy.Opacity = opacity;
            set.Children.InsertAtTop(copy);
        }
        return set;
    }

    CompositionSurfaceBrush Brush(LoadedImageSurface surface)
    {
        var brush = _compositor.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.UniformToFill;
        return brush;
    }

    void Resize(Vector2 size)
    {
        if (size == _size) return;
        _size = size;
        if (_current != null) Layout(_current);
    }

    /// <summary>Sizes the copies for the window and (re)starts their movement.</summary>
    void Layout(ContainerVisual set)
    {
        var copies = set.Children.OfType<SpriteVisual>().ToList();
        if (_size.X <= 0 || copies.Count < 3) return;
        var (big, small, third) = (copies[0], copies[1], copies[2]);
        var middle = _size / 2;
        // Big enough that its corners never come into view as it turns.
        float bigSide = _size.Length() * 1.1f, smallSide = Math.Max(_size.X, _size.Y) * 1.04f, thirdSide = Math.Max(_size.X, _size.Y) * 0.8f;
        big.Size = new Vector2(bigSide);
        big.CenterPoint = new Vector3(bigSide / 2, bigSide / 2, 0);
        big.Offset = new Vector3(middle - big.Size / 2, 0);
        small.Size = new Vector2(smallSide);
        small.CenterPoint = new Vector3(smallSide / 2, smallSide / 2, 0);
        third.Size = new Vector2(thirdSide);
        third.CenterPoint = new Vector3(thirdSide / 2, thirdSide / 2, 0);
        set.CenterPoint = new Vector3(middle, 0);

        if (big.TryGetAnimationController("RotationAngleInDegrees") == null)
        {
            big.StartAnimation("RotationAngleInDegrees", Turn(120, 360));
            small.StartAnimation("RotationAngleInDegrees", Turn(90, -360));
            third.StartAnimation("RotationAngleInDegrees", Turn(70, 360));
            // Breathing: the whole picture swells a little and settles, slowly.
            var breathe = _compositor.CreateVector3KeyFrameAnimation();
            var ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1));
            breathe.InsertKeyFrame(0, Vector3.One, ease);
            breathe.InsertKeyFrame(0.5f, new Vector3(1.08f, 1.08f, 1), ease);
            breathe.InsertKeyFrame(1, Vector3.One, ease);
            breathe.Duration = TimeSpan.FromSeconds(18);
            breathe.IterationBehavior = AnimationIterationBehavior.Forever;
            set.StartAnimation("Scale", breathe);
        }
        // The smaller copies wander around the middle on slow loops (the third the other way round,
        // and faster); their paths depend on the size. The small one goes across in 60 s and down in
        // 75 s, so its path comes back to where it started after 5 minutes; the third, 29.2 s and 38 s.
        small.StartAnimation("Offset", Wander(small.Size, 300, 0.2f, 0.15f, 5, 4));
        third.StartAnimation("Offset", Wander(third.Size, 380, -0.26f, 0.2f, 13, 10));
        SetPaused(set, !_moving);
    }

    /// <summary>
    /// A loop of <paramref name="seconds"/> going across and back <paramref name="acrossTurns"/> times
    /// and down and back <paramref name="downTurns"/> times: whole turns, so it ends where it starts
    /// and runs on with no jump.
    /// </summary>
    Vector3KeyFrameAnimation Wander(Vector2 copy, double seconds, float across, float down, int acrossTurns, int downTurns)
    {
        var middle = _size / 2;
        var wander = _compositor.CreateVector3KeyFrameAnimation();
        var linear = _compositor.CreateLinearEasingFunction();
        // 24 points a turn of the faster way: at least as smooth as before.
        int points = 24 * Math.Max(acrossTurns, downTurns);
        for (int i = 0; i <= points; i++)
        {
            double t = (double)i / points * 2 * Math.PI;
            var at = middle - copy / 2 + new Vector2((float)(_size.X * across * Math.Sin(t * acrossTurns)), (float)(_size.Y * down * Math.Cos(t * downTurns)));
            wander.InsertKeyFrame((float)i / points, new Vector3(at, 0), linear);
        }
        wander.Duration = TimeSpan.FromSeconds(seconds);
        wander.IterationBehavior = AnimationIterationBehavior.Forever;
        return wander;
    }

    ScalarKeyFrameAnimation Turn(double seconds, float degrees)
    {
        var turn = _compositor.CreateScalarKeyFrameAnimation();
        turn.InsertKeyFrame(0, 0, _compositor.CreateLinearEasingFunction());
        turn.InsertKeyFrame(1, degrees, _compositor.CreateLinearEasingFunction());
        turn.Duration = TimeSpan.FromSeconds(seconds);
        turn.IterationBehavior = AnimationIterationBehavior.Forever;
        return turn;
    }

    static void SetPaused(ContainerVisual set, bool paused)
    {
        foreach (var visual in set.Children.Append(set))
            foreach (var property in new[] { "RotationAngleInDegrees", "Offset", "Scale" })
                if (visual.TryGetAnimationController(property) is { } controller)
                {
                    if (paused) controller.Pause(); else controller.Resume();
                }
    }

    void Fade(ContainerVisual set, float to, Action? done)
    {
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to);
        fade.Duration = TimeSpan.FromSeconds(0.8);
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        set.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) =>
        {
            set.Opacity = to;
            done?.Invoke();
        };
    }
}
