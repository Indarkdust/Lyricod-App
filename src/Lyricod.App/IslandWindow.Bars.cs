using System.Numerics;

namespace Lyricod;

public sealed partial class IslandWindow
{
    /// <summary>
    /// As on the Mac: while the island shows and music plays, the bars are redrawn at the
    /// animation rate from Settings, each frame gliding into the next on the compositor. They
    /// follow the player's sound when "Bars follow the music" is on and Windows lets Lyricod
    /// listen to it (Windows 11, or Server 2022), and bob on their own otherwise. Paused, they
    /// settle low. The player is listened to only while the bars move.
    /// </summary>
    void UpdateBars()
    {
        bool running = _shown && _model.IsPlaying;
        string? target = running && _settings.IslandAudioBars ? _model.NowPlaying?.AppId : null;
        if (target != _meteredApp)
        {
            _meteredApp = target;
            if (target != null) _meter.Start(target); else _meter.Stop();
        }

        int rate = (int)_settings.FrameRate;
        if (running == _barsRunning && rate == _barRate) return;
        _barsRunning = running;
        _barRate = rate;
        double interval = 1.0 / rate;
        Glide(running ? interval * 1.1 : 0.35, linear: running);
        if (running)
        {
            _barTimer.Interval = TimeSpan.FromSeconds(interval);
            _barTimer.Start();
        }
        else
        {
            _barTimer.Stop();
            DrawBars();
        }
    }

    /// <summary>A new height eases in over <paramref name="seconds"/>, so the compositor draws the
    /// frames in between (smooth at the display's rate, no extra work here).</summary>
    void Glide(double seconds, bool linear)
    {
        var compositor = _barVisuals[0].Compositor;
        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.Target = "Scale";
        animation.InsertExpressionKeyFrame(1, "this.FinalValue", linear
            ? compositor.CreateLinearEasingFunction()
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0.58f, 1)));
        animation.Duration = TimeSpan.FromSeconds(seconds);
        var animations = compositor.CreateImplicitAnimationCollection();
        animations["Scale"] = animation;
        foreach (var visual in _barVisuals) visual.ImplicitAnimations = animations;
    }

    void DrawBars()
    {
        bool listening = _barsRunning && _settings.IslandAudioBars && !_meter.Failed;
        var levels = listening ? _meter.Levels() : null;
        double t = PlaybackModel.Now;
        for (int i = 0; i < _barVisuals.Count; i++)
        {
            double phase = t * (3.1 + i * 1.3) + i * 1.7;
            // Listening but nothing to hear (between songs, or the meter is still starting): stay low
            // rather than make up a bounce. The bounce is only for when listening can't work.
            double height = !_barsRunning ? 3
                : levels != null ? 3 + 11 * Math.Clamp(levels[i], 0, 1)
                : listening ? 3
                : 4 + 10 * Math.Abs(Math.Sin(phase));
            _barVisuals[i].Scale = new Vector3(1, (float)(height / 14), 1);
        }
    }
}
