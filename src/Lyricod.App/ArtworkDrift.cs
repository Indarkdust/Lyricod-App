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
/// The lyrics window's background, as on the Mac (ArtworkDrift.swift): the cover softened once at
/// small size, then two copies slowly turned and moved by the compositor: a big one, and a smaller
/// one fading out towards its edges that wanders around the middle. Nothing runs in the app per
/// frame; while the music is paused, the drift pauses in place.
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
        // The small copy fades out towards its edges, so it has no visible border.
        var mask = _compositor.CreateRadialGradientBrush();
        mask.EllipseRadius = new Vector2(0.5f, 0.5f); // clear by the middle of each edge, as on the Mac
        mask.ColorStops.Add(_compositor.CreateColorGradientStop(0, Colors.Black));
        mask.ColorStops.Add(_compositor.CreateColorGradientStop(0.45f, Colors.Black));
        mask.ColorStops.Add(_compositor.CreateColorGradientStop(1, Colors.Transparent));
        var masked = _compositor.CreateMaskBrush();
        masked.Source = Brush(surface);
        masked.Mask = mask;
        var small = _compositor.CreateSpriteVisual();
        small.Brush = masked;
        set.Children.InsertAtTop(big);
        set.Children.InsertAtTop(small);
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
        if (_size.X <= 0 || set.Children.Count < 2) return;
        var big = (SpriteVisual)set.Children.First();
        var small = (SpriteVisual)set.Children.Last();
        var middle = _size / 2;
        // Big enough that its corners never come into view as it turns.
        float bigSide = _size.Length() * 1.1f, smallSide = Math.Max(_size.X, _size.Y) * 1.04f;
        big.Size = new Vector2(bigSide);
        big.CenterPoint = new Vector3(bigSide / 2, bigSide / 2, 0);
        big.Offset = new Vector3(middle - big.Size / 2, 0);
        small.Size = new Vector2(smallSide);
        small.CenterPoint = new Vector3(smallSide / 2, smallSide / 2, 0);

        if (big.TryGetAnimationController("RotationAngleInDegrees") == null)
        {
            big.StartAnimation("RotationAngleInDegrees", Turn(120, 360));
            small.StartAnimation("RotationAngleInDegrees", Turn(90, -360));
        }
        // The small copy wanders around the middle on a slow loop; its path depends on the size.
        var wander = _compositor.CreateVector3KeyFrameAnimation();
        const int points = 24;
        for (int i = 0; i <= points; i++)
        {
            double t = (double)i / points * 2 * Math.PI;
            var at = middle - small.Size / 2 + new Vector2((float)(_size.X * 0.2 * Math.Sin(t)), (float)(_size.Y * 0.15 * Math.Cos(t * 0.8)));
            wander.InsertKeyFrame((float)i / points, new Vector3(at, 0), _compositor.CreateLinearEasingFunction());
        }
        wander.Duration = TimeSpan.FromSeconds(60);
        wander.IterationBehavior = AnimationIterationBehavior.Forever;
        small.StartAnimation("Offset", wander);
        SetPaused(set, !_moving);
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
        foreach (var visual in set.Children)
            foreach (var property in new[] { "RotationAngleInDegrees", "Offset" })
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
