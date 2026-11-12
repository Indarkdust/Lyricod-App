using System.Numerics;
using Lyricod.Platform;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;

namespace Lyricod;

/// <summary>
/// Lyricod Glass's liquid effect (see <see cref="LiquidGlass"/>). It runs only while
/// it's the chosen glass effect and the island shows (not while the pointer hides it or it's being
/// moved). Older Windows builds hide the island globally from capture; updated builds exclude
/// it only from its own session, so screenshots and other recordings can see the live glass.
/// </summary>
public sealed partial class IslandWindow
{
    /// <summary>How long the island shows in screenshots when asked (see <see cref="ShowInScreenshots"/>).</summary>
    public const int ScreenshotSeconds = 10;

    LiquidGlass? _liquid;
    DispatcherQueueTimer? _screenshotTimer;
    /// <summary>Tries the liquid glass again after Windows ended its capture (see <see cref="RetryLiquidLater"/>).</summary>
    DispatcherQueueTimer? _liquidRetry;
    /// <summary>Tries since the capture ended; back to 0 once the glass runs again.</summary>
    int _liquidRetries;

    internal string LiquidCaptureStatus => _liquid == null ? "inactive" : _liquid.Failed ? "failed"
        : (_liquid.SessionExclusion ? $"session-only exclusion, island stays in recordings ({_liquid.ExclusionDetail})"
            : _liquid.Frozen ? "global exclusion fallback, frozen for a screenshot" : "global exclusion fallback")
        + "; " + _liquid.FrameDetail;

    bool WantsLiquid => Glass && _settings.GlassEffect == GlassEffect.Liquid && LiquidGlass.IsSupported && !Movable;

    /// <summary>Starts, follows or stops the liquid glass; returns whether it's showing.</summary>
    bool ApplyLiquid()
    {
        try
        {
            bool on = WantsLiquid && _shown && !(_hovered && _hoverAction == IslandHover.Hide) && _liquid?.Failed != true;
            if (!on)
            {
                if (_liquid is { Failed: true } failed && (failed.Ended || _liquidRetries > 0) && WantsLiquid) RetryLiquidLater();
                StopLiquid();
                return false;
            }
            if (_liquid == null)
            {
                // If it fails later (the graphics card reset, the screen went away), fall back to clear glass;
                // once it has faded in, the pill's own fill underneath goes (see ApplyTheme).
                LiquidGlass? created = null;
                created = new LiquidGlass(ElementCompositionPreview.GetElementVisual(LiquidHost).Compositor, DispatcherQueue, _overlay.Handle,
                    () =>
                    {
                        try
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                    if (_closed || !ReferenceEquals(_liquid, created)) return;
                                    if (created.Settled) _liquidRetries = 0;
                                    ApplyTheme();
                                }
                                catch (Exception) { StopLiquid(); }
                            });
                        }
                        catch (Exception) { StopLiquid(); }
                    });
                created.FramesPerSecond = (int)_settings.FrameRate;
                _liquid = created;
                ElementCompositionPreview.SetElementChildVisual(LiquidHost, _liquid.Visual);
            }
            UpdateLiquid();
            if (!_liquid.Failed) return true;
            if (_liquid.Ended || _liquidRetries > 0) RetryLiquidLater();
            StopLiquid();
            return false;
        }
        catch (Exception)
        {
            StopLiquid();
            return false;
        }
    }

    /// <summary>
    /// Lets screenshots, recordings and screen sharing see the liquid glass island for a few
    /// seconds. Meanwhile the glass keeps showing what was behind it instead of capturing, so it
    /// can't capture itself.
    /// </summary>
    public void ShowInScreenshots()
    {
        try
        {
            var liquid = _liquid;
            if (liquid?.ShowInScreenshots() != true) return;
            var oldTimer = _screenshotTimer;
            _screenshotTimer = null;
            try { oldTimer?.Stop(); } catch (Exception) { }
            var timer = DispatcherQueue.CreateTimer();
            int generation = liquid.Generation;
            timer.Interval = TimeSpan.FromSeconds(ScreenshotSeconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (ReferenceEquals(_screenshotTimer, timer) && ReferenceEquals(_liquid, liquid) && liquid.IsCurrent(generation))
                        EndScreenshot();
                }
                catch (Exception) { StopLiquid(); }
            };
            _screenshotTimer = timer;
            timer.Start();
        }
        catch (Exception e) { _liquid?.Fail(e); }
    }

    /// <summary>Hides the island from capture again, then lets the glass follow what's behind it.</summary>
    void EndScreenshot()
    {
        try
        {
            _screenshotTimer?.Stop();
            if (_liquid == null) return;
            _liquid.EndScreenshot();
        }
        catch (Exception e) { _liquid?.Fail(e); }
    }

    /// <summary>
    /// Windows ended the liquid glass's capture (the screen slept or went away), which left the
    /// island on clear glass until something else (a new song, a hover) built the glass again. So
    /// it's built again a few seconds later, and if that fails too, however it fails (the screen
    /// may not be back yet), again less and less often, up to every 30 seconds, until it runs or
    /// isn't wanted. A failure with no capture ended before isn't retried.
    /// </summary>
    void RetryLiquidLater()
    {
        if (_liquidRetry == null)
        {
            _liquidRetry = DispatcherQueue.CreateTimer();
            _liquidRetry.IsRepeating = false;
            _liquidRetry.Tick += (_, _) =>
            {
                try
                {
                    if (_closed) return;
                    if (!WantsLiquid) { _liquidRetries = 0; return; }
                    if (_liquid == null) ApplyTheme();
                }
                catch (Exception e) { Log.Write(e); }
            };
        }
        _liquidRetry.Interval = TimeSpan.FromSeconds(Math.Min(3 << Math.Min(_liquidRetries, 4), 30));
        _liquidRetries++;
        _liquidRetry.Stop();
        _liquidRetry.Start();
    }

    void StopLiquid()
    {
        var timer = _screenshotTimer;
        _screenshotTimer = null;
        try { timer?.Stop(); } catch (Exception) { }
        var liquid = _liquid;
        _liquid = null;
        if (liquid == null) return;
        try { ElementCompositionPreview.SetElementChildVisual(LiquidHost, null); } catch (Exception) { }
        try { liquid.Dispose(); } catch (Exception) { }
    }

    /// <summary>Keeps the liquid glass on what's behind the island as it moves, grows and changes look.</summary>
    void UpdateLiquid()
    {
        try
        {
            if (_liquid == null) return;
            var (left, top, width, height) = PillBounds();
            if (width <= 0 || height <= 0) return;
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            var screen = display.OuterBounds;
            _liquid.Update(Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(display.DisplayId), new PointInt32(screen.X, screen.Y),
                new RectInt32((int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(width), (int)Math.Round(height)),
                new Vector2((float)Pill.ActualWidth, (float)Pill.ActualHeight), (float)Pill.CornerRadius.BottomLeft, !Floating,
                blur: _settings.LiquidBlur / 100f * 16, refraction: _settings.LiquidRefraction / 100f * 14, GlassTintColour(), shine: GlassLit);
        }
        catch (Exception e) { _liquid?.Fail(e); }
    }
}
