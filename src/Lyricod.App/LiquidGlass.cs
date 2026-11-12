using System.Numerics;
using System.Runtime.InteropServices;
using Lyricod.Core;
using Lyricod.Platform;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Windows.Graphics;
using Windows.Graphics.Capture;
using WinRT;

namespace Lyricod;

/// <summary>
/// Lyricod Glass's liquid effect: what's behind the island, captured from the screen and drawn as
/// its background, lightly blurred and bent at the edges like the rim of a lens, with the light its
/// edge catches drawn along the same curve. On supported builds the island is excluded from its own
/// capture session, so other recorders still see it; older builds need global capture exclusion to
/// avoid feedback. Windows 11 only: Windows 10 outlines the whole screen in yellow while anything
/// captures it.
/// <para>
/// Windows sends a new frame whenever anything on the screen changes, and drawing the glass is such
/// a change, so a frame is drawn only when what's behind the island differs from the last one drawn
/// (see <see cref="Unchanged"/>); otherwise the glass would redraw itself for ever. Frames come no
/// more often than the animation rate from Settings.
/// </para>
/// </summary>
sealed class LiquidGlass : IDisposable
{
    public static bool IsSupported => Environment.OSVersion.Version.Build >= 22000 && GraphicsCaptureSession.IsSupported();

    /// <summary>What to draw: the island's pixels on its screen, its corners and the look (<c>Shine</c>: width of the edge light in pixels, 0 for none).</summary>
    sealed record Look(RectInt32 Source, float Radius, bool SquareTop, float Blur, float Refraction, Windows.UI.Color Tint, float Shine);

    readonly DispatcherQueue _ui;
    readonly Action _changed;
    readonly nint _window;
    CanvasDevice _device = null!; // shared; not owned here
    CompositionGraphicsDevice _graphics = null!;
    CompositionDrawingSurface _surface = null!;
    CompositionRoundedRectangleGeometry _shape = null!;
    CompositionSurfaceBrush _brush = null!;
    CompositionGeometricClip _clip = null!;
    SpriteVisual _visual = null!;
    Transform2DEffect _behind = null!;
    DisplacementMapEffect _bend = null!;
    GaussianBlurEffect _blur = null!;
    SaturationEffect _vivid = null!;

    GraphicsCaptureItem? _item;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    SizeInt32 _poolSize;
    nint _monitor;
    /// <summary>The newest frame, kept to draw again when the island moves or resizes.</summary>
    Direct3D11CaptureFrame? _frame;
    CanvasBitmap? _screen;
    CanvasBitmap? _lens;
    (int Width, int Height, float Radius, bool SquareTop) _lensFor;
    CanvasBitmap? _light;
    (int Width, int Height, float Radius, bool SquareTop, float Rim) _lightFor;
    Look? _look;
    int _pending, _generation, _startGeneration;
    readonly DispatcherQueueTimer _startDelay;
    /// <summary>Starting a session: 0, waiting for the island to show; 1, it showed a moment ago; 2 (older Windows),
    /// waiting for it to leave every capture; 3, capturing.</summary>
    int _startStep;
    /// <summary>With session exclusion: the capture configuration that leaves the island out. Earlier frames aren't used.</summary>
    ulong _excludedFrom;
    /// <summary>When session exclusion was set (<see cref="Environment.TickCount64"/>), for frames that don't say their configuration.</summary>
    long _excludedAt;
    /// <summary>Diagnostics: frames left unused while the exclusion took effect, and the newest frame's configuration.</summary>
    int _earlyFrames;
    ulong? _newestIteration;
    volatile bool _disposed;
    bool _ownsAffinity;
    bool _appeared;
    /// <summary>The screen behind the island as last drawn from, and as now (see <see cref="Unchanged"/>); they only grow.</summary>
    byte[] _seen = [], _seeing = [];
    RectInt32 _seenFrom;
    bool _hasSeen;
    /// <summary>How a frame's pixels are read back: 0, straight from the frame; 1, through a copy
    /// (where the frame can't be read); 2, not at all: every frame is drawn.</summary>
    int _reading;
    CanvasRenderTarget? _copy;
    /// <summary>Diagnostics: frames taken from Windows, drawn, and left as they were.</summary>
    int _frames, _draws, _same;
    Windows.Foundation.TypedEventHandler<GraphicsCaptureItem, object>? _itemClosed;
    Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object>? _frameArrived;
    /// <summary>Until when (<see cref="Environment.TickCount64"/>) new frames are left unused after a freeze.</summary>
    long _skipUntil;

    /// <summary>Capturing or drawing failed (the graphics card reset, the screen went away): the island falls back.</summary>
    public bool Failed { get; private set; }
    /// <summary>It failed because Windows ended the capture (the screen went away, or to sleep), not on an error.</summary>
    public bool Ended { get; private set; }
    /// <summary>Windows ended a capture and no glass has run since: the island retries (see <c>IslandWindow.RetryLiquidLater</c>); only the first error is logged meanwhile.</summary>
    static bool _sinceEnded, _erredSinceEnded;

    /// <summary>Only our monitor session excludes the island; other recorders can see it live.</summary>
    public bool SessionExclusion { get; private set; }

    /// <summary>Windows limits how often frames come (Windows 11 24H2 and later).</summary>
    public bool RateLimited { get; private set; }

    /// <summary>The glass has faded in over the island: until then the island keeps its own fill underneath.</summary>
    public bool Settled { get; private set; }

    /// <summary>How often the glass follows the screen at most, in frames a second (Settings › General › Animation).</summary>
    public int FramesPerSecond
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            try { if (_session != null && !_disposed) RateLimited = LimitRate(_session, value); }
            catch (Exception) { RateLimited = false; }
        }
    } = 30;

    public SpriteVisual Visual => _visual;

    /// <summary>
    /// Keeps showing what was behind the island instead of capturing afresh (it would capture
    /// itself while the island shows in screenshots); the glass still follows the island's size.
    /// </summary>
    public bool Frozen
    {
        get;
        set
        {
            // Frames from the next moment may still show the island, until Windows hides it from capture again.
            if (field && !value) _skipUntil = Environment.TickCount64 + 250;
            field = value;
        }
    }

    /// <param name="changed">Called (on the UI thread) when capturing or drawing fails, and once the glass has faded in.</param>
    public LiquidGlass(Compositor compositor, DispatcherQueue ui, nint window, Action changed)
    {
        _ui = ui;
        _changed = changed;
        _window = window;
        _startDelay = ui.CreateTimer();
        _startDelay.IsRepeating = false;
        _startDelay.Tick += (_, _) =>
        {
            int generation = _startGeneration;
            try { if (IsCurrent(generation)) Begin(); }
            catch (Exception e) { try { if (IsCurrent(generation)) Fail(e); } catch (Exception) { } }
        };
        try
        {
            _device = CanvasDevice.GetSharedDevice();
            _behind = new Transform2DEffect();
            _bend = new DisplacementMapEffect { XChannelSelect = EffectChannelSelect.Red, YChannelSelect = EffectChannelSelect.Green };
            _blur = new GaussianBlurEffect { BorderMode = EffectBorderMode.Hard, Optimization = EffectOptimization.Speed };
            _vivid = new SaturationEffect { Saturation = 1.2f };
            _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
            _surface = _graphics.CreateDrawingSurface(new Windows.Foundation.Size(1, 1),
                Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            _brush = compositor.CreateSurfaceBrush(_surface);
            _brush.Stretch = CompositionStretch.Fill;
            _shape = compositor.CreateRoundedRectangleGeometry();
            _visual = compositor.CreateSpriteVisual();
            _visual.Brush = _brush;
            _visual.Opacity = 0; // until the first frame is drawn (see Appear)
            // Smooth rounded corners, unlike a clipped window's.
            _clip = compositor.CreateGeometricClip(_shape);
            _visual.Clip = _clip;
        }
        catch
        {
            DisposeDrawingResources();
            throw;
        }
    }

    /// <summary>
    /// Follows the island: its screen (<paramref name="monitor"/>, whose top-left is <paramref
    /// name="origin"/>), its bounds there (<paramref name="pill"/>, pixels), its size and corner
    /// radius in view pixels, and the look (blur and refraction in view pixels; whether the edge
    /// catches the light).
    /// </summary>
    public void Update(nint monitor, PointInt32 origin, RectInt32 pill, Vector2 size, float radius, bool squareTop,
        float blur, float refraction, Windows.UI.Color tint, bool shine)
    {
        if (_disposed || Failed) return;
        try
        {
            Visual.Size = size;
            // The default island's top is the screen's edge: round only the bottom corners.
            _shape.Offset = new Vector2(0, squareTop ? -radius : 0);
            _shape.Size = new Vector2(size.X, size.Y + (squareTop ? radius : 0));
            _shape.CornerRadius = new Vector2(radius, radius);
            float scale = size.X > 0 ? pill.Width / size.X : 1;
            _look = new Look(new RectInt32(pill.X - origin.X, pill.Y - origin.Y, pill.Width, pill.Height), radius * scale, squareTop,
                blur * scale, refraction * scale, tint, shine ? 1.5f * scale : 0);
            if (monitor != _monitor) Start(monitor);
            else if (_screen != null)
            {
                Draw(_screen, _look);
                Appear();
            }
        }
        catch (Exception e) { Fail(e); }
    }

    void Start(nint monitor)
    {
        Stop();
        if (_disposed) return;
        int generation = Volatile.Read(ref _generation);
        _monitor = monitor;
        _item = ForMonitor(monitor);
        _itemClosed = (_, _) =>
        {
            try
            {
                if (!IsCurrent(generation)) return;
                _ui.TryEnqueue(() =>
                {
                    try { if (IsCurrent(generation)) Fail(null); }
                    catch (Exception) { }
                });
            }
            catch (Exception) { }
        };
        _item.Closed += _itemClosed;
        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _poolSize);
        DispatcherQueueHandler drawNewest = () =>
        {
            try
            {
                if (!IsCurrent(generation)) return;
                Interlocked.CompareExchange(ref _pending, 0, generation);
                DrawNewest();
            }
            catch (Exception e) { if (IsCurrent(generation)) Fail(e); }
        };
        _frameArrived = (_, _) => OnFrameArrived(generation, drawNewest);
        _pool.FrameArrived += _frameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        Borderless(_session);
        try { RateLimited = LimitRate(_session, FramesPerSecond); }
        catch (Exception) { RateLimited = false; }
        _startGeneration = generation;
        _startStep = 0;
        Begin();
    }

    /// <summary>
    /// Starts the session capturing, step by step. Windows leaves a window out of capture only once
    /// it's on screen, and an exclusion set before that is ignored (the glass would capture itself
    /// into solid colour). So nothing starts until the island has been showing a moment.
    /// </summary>
    void Begin()
    {
        if (_session == null) return;
        if (_startStep == 2)
        {
            _session.StartCapture();
            _startStep = 3;
            return;
        }
        if (_startStep > 2) return;
        if (!Win32.IsWindowVisible(_window) || _startStep == 0)
        {
            _startStep = Win32.IsWindowVisible(_window) ? 1 : 0;
            Next(100);
            return;
        }
        nint native = ((IWinRTObject)_session).NativeObject.ThisPtr;
        int build = Environment.OSVersion.Version.Build;
        if (DisplayCaptureExclusion.IsAvailable(native, build))
        {
            // Each frame says which capture configuration it made with; frames from before the one
            // that leaves the island out are left unused.
            _session.StartCapture();
            _startStep = 3;
            if (DisplayCaptureExclusion.TryExcludeWindow(native, _window, build, out ulong iteration))
            {
                _excludedFrom = iteration;
                _excludedAt = Environment.TickCount64;
                SessionExclusion = true;
                return;
            }
            // Capturing already: leave the island out of every capture, and skip frames until it's out.
            SetCaptureAffinity(Frozen ? Win32.WDA_NONE : Win32.WDA_EXCLUDEFROMCAPTURE);
            _skipUntil = Environment.TickCount64 + 400;
            return;
        }
        // Older Windows: the island goes out of every capture, which takes a moment. Capturing at
        // once could put the island inside its own glass, and on a still screen that frame stays
        // (no new ones until something changes), so capturing starts later.
        SetCaptureAffinity(Frozen ? Win32.WDA_NONE : Win32.WDA_EXCLUDEFROMCAPTURE);
        _startStep = 2;
        Next(400);
    }

    void Next(int milliseconds)
    {
        _startDelay.Stop();
        _startDelay.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _startDelay.Start();
    }

    /// <summary>Lets screenshots see the island by lifting its capture exclusion; not needed with session exclusion.</summary>
    public bool ShowInScreenshots()
    {
        if (_disposed || _session == null || SessionExclusion || Failed) return false;
        try
        {
            Frozen = true; // stop consuming frames before letting capture see this window
            SetCaptureAffinity(Win32.WDA_NONE);
            return true;
        }
        catch (Exception e) { Fail(e); return false; }
    }

    public void EndScreenshot()
    {
        if (_disposed || Failed) return;
        try
        {
            if (!SessionExclusion) SetCaptureAffinity(Win32.WDA_EXCLUDEFROMCAPTURE);
            Frozen = false;
        }
        catch (Exception e) { Fail(e); }
    }

    void SetCaptureAffinity(uint affinity)
    {
        // Never run live capture without an exclusion mechanism: the island would feed back into
        // itself. Failure takes the clear-glass fallback instead.
        if (_disposed) return;
        if (!Win32.SetWindowDisplayAffinity(_window, affinity))
            throw new InvalidOperationException("Could not set the island's capture affinity.");
        _ownsAffinity = affinity != Win32.WDA_NONE;
    }

    internal int Generation => Volatile.Read(ref _generation);

    internal bool IsCurrent(int generation) => !_disposed && !Failed && generation == Volatile.Read(ref _generation);

    /// <summary>Frames arrive on a capture thread; the newest is drawn on the UI thread.</summary>
    void OnFrameArrived(int generation, DispatcherQueueHandler drawNewest)
    {
        try
        {
            if (!IsCurrent(generation) || Interlocked.Exchange(ref _pending, generation) == generation) return;
            if (!_ui.TryEnqueue(drawNewest)) Interlocked.CompareExchange(ref _pending, 0, generation);
        }
        catch (Exception) { Interlocked.CompareExchange(ref _pending, 0, generation); }
    }

    void DrawNewest()
    {
        if (_disposed || _pool == null || Failed) return;
        Direct3D11CaptureFrame? newest = null;
        try
        {
            while (_pool.TryGetNextFrame() is { } frame)
            {
                var previous = newest;
                newest = frame;
                previous?.Dispose();
            }
            if (newest == null) return;
            if (newest.ContentSize.Width != _poolSize.Width || newest.ContentSize.Height != _poolSize.Height)
            {
                // Return every checked-out surface before resizing the pool.
                _poolSize = newest.ContentSize;
                RetireFrame();
                DisposeOwned(ref newest);
                _pool.Recreate(_device, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, _poolSize);
                return;
            }
            if (Frozen || Environment.TickCount64 < _skipUntil) return;
            if (SessionExclusion && !LeavesIslandOut(newest)) return;
            RetireFrame();
            _screen = CanvasBitmap.CreateFromDirect3D11Surface(_device, newest.Surface);
            _frame = newest;
            newest = null; // owned by _frame now
            _frames++;
            if (_look == null) return;
            if (Unchanged(_screen, _look)) { _same++; return; }
            Draw(_screen, _look);
            Appear();
        }
        catch (Exception e) { Fail(e); }
        finally { DisposeOwned(ref newest); }
    }

    /// <summary>
    /// Whether a frame was made once the session's exclusion took effect, by its capture
    /// configuration; where frames don't say, the first moment after setting it is left unused
    /// instead.
    /// </summary>
    bool LeavesIslandOut(Direct3D11CaptureFrame frame)
    {
        _newestIteration = DisplayCaptureExclusion.FrameIteration(((IWinRTObject)frame).NativeObject.ThisPtr);
        bool after = _newestIteration is { } iteration ? iteration >= _excludedFrom : Environment.TickCount64 - _excludedAt >= 300;
        if (!after) _earlyFrames++;
        return after;
    }

    internal string ExclusionDetail => SessionExclusion
        ? $"excluded from configuration {_excludedFrom}, newest frame from configuration {(_newestIteration is { } i ? i.ToString() : "unknown")}"
            + $", {_earlyFrames} earlier frame{(_earlyFrames == 1 ? "" : "s")} skipped"
        : "";

    internal string FrameDetail =>
        $"{_frames} frames, {_draws} drawn, {_same} unchanged ({(_reading == 0 ? "read directly" : _reading == 1 ? "read through a copy" : "can't be read")})"
        + $", {(RateLimited ? $"at most {FramesPerSecond} a second" : "no rate limit")}";

    /// <summary>
    /// Whether the screen behind the island is as it was when the glass last drew from it: the
    /// island's part of the frame plus as far around it as the blur and bend reach, compared byte
    /// for byte. Reading that back costs far less than drawing, and it stops the glass redrawing
    /// itself (each redraw brings a new frame).
    /// </summary>
    bool Unchanged(CanvasBitmap screen, Look look)
    {
        int margin = (int)Math.Ceiling(look.Refraction + 3 * look.Blur) + 2;
        var size = screen.SizeInPixels;
        int left = Math.Max(look.Source.X - margin, 0), top = Math.Max(look.Source.Y - margin, 0);
        int right = Math.Min(look.Source.X + look.Source.Width + margin, (int)size.Width);
        int bottom = Math.Min(look.Source.Y + look.Source.Height + margin, (int)size.Height);
        int width = right - left, height = bottom - top;
        if (width <= 0 || height <= 0) return false;
        int length = width * height * 4;
        if (_seeing.Length < length) _seeing = new byte[length + length / 4];
        if (!Read(screen, _seeing, length, left, top, width, height)) return false;
        bool same = _hasSeen && left == _seenFrom.X && top == _seenFrom.Y && width == _seenFrom.Width && height == _seenFrom.Height
            && _seeing.AsSpan(0, length).SequenceEqual(_seen.AsSpan(0, length));
        (_seen, _seeing) = (_seeing, _seen);
        _seenFrom = new RectInt32(left, top, width, height);
        _hasSeen = true;
        return same;
    }

    /// <summary>
    /// Reads part of a frame into <paramref name="into"/>, directly or else through a copy. If
    /// neither works the check is given up (and logged) and every frame is drawn.
    /// </summary>
    bool Read(CanvasBitmap screen, byte[] into, int length, int left, int top, int width, int height)
    {
        var buffer = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(into, 0, length);
        if (_reading == 0)
        {
            try
            {
                screen.GetPixelBytes(buffer, left, top, width, height);
                return true;
            }
            catch (Exception e)
            {
                _reading = 1;
                Log.Write("liquid glass: a frame can't be read directly, trying a copy: " + e.Message);
            }
        }
        if (_reading == 1)
        {
            try
            {
                if (_copy == null || _copy.SizeInPixels.Width < width || _copy.SizeInPixels.Height < height)
                {
                    DisposeOwned(ref _copy);
                    // Room to grow, so a pill that's widening doesn't need a new one every frame.
                    _copy = new CanvasRenderTarget(_device, width + width / 4, height + height / 4, 96);
                }
                _copy.CopyPixelsFromBitmap(screen, 0, 0, left, top, width, height);
                _copy.GetPixelBytes(buffer, 0, 0, width, height);
                return true;
            }
            catch (Exception e)
            {
                _reading = 2;
                Log.Write("liquid glass: a frame can't be read through a copy either; every frame is drawn: " + e.Message);
            }
        }
        return false;
    }

    /// <summary>Fades the glass in with its first frame (see <see cref="Begin"/>) rather than all at once under lyrics that are already there.</summary>
    void Appear()
    {
        if (_appeared) return;
        _appeared = true;
        var compositor = _visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 1, compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0.2f, 1)));
        fade.Duration = TimeSpan.FromSeconds(0.22);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _visual.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) =>
        {
            try
            {
                if (_disposed) return;
                _visual.Opacity = 1;
                Settled = true;
                if (_sinceEnded)
                {
                    _sinceEnded = _erredSinceEnded = false;
                    Log.Write("liquid glass: following the screen again");
                }
                _changed();
            }
            catch (Exception) { }
        };
    }

    /// <summary>The part of the screen behind the island: bent at the edges, blurred, a little more vivid, tinted, lit.</summary>
    void Draw(CanvasBitmap screen, Look look)
    {
        _draws++;
        int width = look.Source.Width, height = look.Source.Height;
        if (width <= 0 || height <= 0) return;
        if (_surface.Size.Width != width || _surface.Size.Height != height)
            CanvasComposition.Resize(_surface, new Windows.Foundation.Size(width, height));
        _behind.Source = screen;
        _behind.TransformMatrix = Matrix3x2.CreateTranslation(-look.Source.X, -look.Source.Y);
        ICanvasImage image = _behind;
        if (look.Refraction > 0)
        {
            _bend.Source = image;
            _bend.Displacement = Lens(width, height, look.Radius, look.SquareTop);
            _bend.Amount = look.Refraction * 2; // the map's full swing is half the amount each way
            image = _bend;
        }
        if (look.Blur > 0)
        {
            _blur.Source = image;
            _blur.BlurAmount = look.Blur;
            image = _blur;
        }
        _vivid.Source = image;
        using var session = CanvasComposition.CreateDrawingSession(_surface);
        session.Clear(Microsoft.UI.Colors.Transparent);
        session.DrawImage(_vivid);
        session.FillRectangle(0, 0, width, height, look.Tint);
        if (look.Shine > 0) session.DrawImage(Light(width, height, look.Radius, look.SquareTop, look.Shine));
    }

    /// <summary>The lens map (see <see cref="LiquidLens"/>), made again only when the island's shape changes.</summary>
    CanvasBitmap Lens(int width, int height, float radius, bool squareTop)
    {
        if (_lens != null && _lensFor == (width, height, radius, squareTop)) return _lens;
        DisposeOwned(ref _lens);
        _lens = CanvasBitmap.CreateFromBytes(_device, LiquidLens.Map(width, height, radius, squareTop), width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        _lensFor = (width, height, radius, squareTop);
        return _lens;
    }

    /// <summary>The light along the edge (see <see cref="LiquidLens.Light"/>), made again only when the island's shape changes.</summary>
    CanvasBitmap Light(int width, int height, float radius, bool squareTop, float rim)
    {
        if (_light != null && _lightFor == (width, height, radius, squareTop, rim)) return _light;
        DisposeOwned(ref _light);
        _light = CanvasBitmap.CreateFromBytes(_device, LiquidLens.Light(width, height, radius, squareTop, rim), width, height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        _lightFor = (width, height, radius, squareTop, rim);
        return _light;
    }

    internal void Fail(Exception? e)
    {
        if (_disposed || Failed) return;
        Failed = true;
        // With no exception, Windows ended the capture: the screen went away, or went to sleep.
        Ended = e == null;
        if (!_sinceEnded || e != null && !_erredSinceEnded)
            try { Log.Write(e != null ? "liquid glass: " + e : "liquid glass: the screen's capture ended"); } catch (Exception) { }
        if (e != null && _sinceEnded) _erredSinceEnded = true;
        if (Ended) _sinceEnded = true;
        try { Stop(); } catch (Exception) { }
        try { _changed(); } catch (Exception) { }
    }

    void Stop()
    {
        try { _startDelay.Stop(); } catch (Exception) { }
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _pending, 0);
        _monitor = 0;
        SessionExclusion = false;
        _excludedFrom = 0;
        _earlyFrames = 0;
        _newestIteration = null;
        _hasSeen = false;
        RateLimited = false;
        Frozen = false; // the old session's screenshot timer is retired too
        var item = _item;
        var closed = _itemClosed;
        _item = null;
        _itemClosed = null;
        if (item != null && closed != null)
            try { item.Closed -= closed; } catch (Exception) { }
        DisposeOwned(ref _session);
        var pool = _pool;
        _pool = null;
        var arrived = _frameArrived;
        _frameArrived = null;
        if (pool != null)
        {
            try { if (arrived != null) pool.FrameArrived -= arrived; } catch (Exception) { }
            DisposeOwned(ref pool);
        }
        RetireFrame();
        bool resetAffinity = _ownsAffinity;
        _ownsAffinity = false;
        if (resetAffinity)
            try { Win32.SetWindowDisplayAffinity(_window, Win32.WDA_NONE); } catch (Exception) { }
    }

    void RetireFrame()
    {
        try { _behind.Source = null; } catch (Exception) { }
        DisposeOwned(ref _screen);
        DisposeOwned(ref _frame);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch (Exception) { }
        DisposeDrawingResources();
    }

    void DisposeDrawingResources()
    {
        DisposeOwned(ref _visual);
        DisposeOwned(ref _clip);
        DisposeOwned(ref _brush);
        DisposeOwned(ref _shape);
        DisposeOwned(ref _vivid);
        DisposeOwned(ref _blur);
        DisposeOwned(ref _bend);
        DisposeOwned(ref _behind);
        DisposeOwned(ref _lens);
        DisposeOwned(ref _light);
        DisposeOwned(ref _copy);
        DisposeOwned(ref _surface);
        DisposeOwned(ref _graphics);
    }

    static void DisposeOwned<T>(ref T resource) where T : class, IDisposable
    {
        var owned = resource;
        resource = null!;
        try { owned?.Dispose(); }
        catch (Exception) { /* keep releasing the other resources */ }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    /// <summary>A capture item for a whole screen, which WinRT only offers through this COM interface.</summary>
    static GraphicsCaptureItem ForMonitor(nint monitor)
    {
        var interop = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem").AsInterface<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
        nint pointer = interop.CreateForMonitor(monitor, ref iid);
        try { return GraphicsCaptureItem.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    /// <summary>
    /// Asks Windows 11 not to outline the screen while capturing. The property is newer than the
    /// SDK Lyricod builds against, so it's set through its COM interface where Windows has it.
    /// </summary>
    static unsafe void Borderless(GraphicsCaptureSession session)
    {
        var iid = new Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE"); // IGraphicsCaptureSession3
        nint unknown = ((IWinRTObject)session).NativeObject.ThisPtr;
        if (Marshal.QueryInterface(unknown, in iid, out nint session3) != 0) return;
        try
        {
            // IUnknown (3) and IInspectable (3), then get_IsBorderRequired, put_IsBorderRequired.
            var put = (delegate* unmanaged[Stdcall]<nint, byte, int>)(*(void***)session3)[7];
            put(session3, 0);
        }
        finally { Marshal.Release(session3); }
    }

    /// <summary>
    /// Asks Windows for frames no more often than <paramref name="perSecond"/> times a second, so a
    /// screen that changes all the time (a video, a game) costs only that much. Newer than the SDK,
    /// like the border (24H2).
    /// </summary>
    static unsafe bool LimitRate(GraphicsCaptureSession session, int perSecond)
    {
        if (perSecond <= 0) return false;
        var iid = new Guid("67C0EA62-1F85-5061-925A-239BE0AC09CB"); // IGraphicsCaptureSession5
        nint unknown = ((IWinRTObject)session).NativeObject.ThisPtr;
        if (Marshal.QueryInterface(unknown, in iid, out nint session5) != 0) return false;
        try
        {
            // IUnknown (3) and IInspectable (3), then get_MinUpdateInterval, put_MinUpdateInterval (a TimeSpan: 100 ns units).
            var put = (delegate* unmanaged[Stdcall]<nint, long, int>)(*(void***)session5)[7];
            return put(session5, TimeSpan.FromSeconds(1.0 / perSecond).Ticks) >= 0;
        }
        finally { Marshal.Release(session5); }
    }
}
