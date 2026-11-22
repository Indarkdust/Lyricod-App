namespace Lyricod.Core;

/// <summary>A player report: its timeline (the position at an instant), if it has one, and whether it plays.</summary>
public readonly record struct MediaClockReport(double Position, DateTimeOffset PositionAt, bool IsPlaying, bool HasTimeline);

/// <summary>Moves a <see cref="Playhead"/> from player reports.</summary>
public sealed class MediaClock(Playhead playhead)
{
    (double, DateTimeOffset)? _timeline;

    /// <summary>Returns whether the playhead was re-anchored. <paramref name="utcNow"/> is the time of day, <paramref name="now"/> the playhead's clock.</summary>
    public bool Apply(MediaClockReport report, bool trackChanged, DateTimeOffset utcNow, double now)
    {
        (double, DateTimeOffset)? timeline = report.HasTimeline ? (report.Position, report.PositionAt) : null;
        // Players often resend the pause-time timeline on resume. Carried on from its stamp, it would
        // count the pause as played, so a repeated timeline keeps the playhead where it is.
        bool fresh = timeline != null && (trackChanged || timeline != _timeline);
        _timeline = timeline;
        double position = trackChanged ? 0 : playhead.RawPosition(now);
        if (fresh)
        {
            position = report.Position;
            double elapsed = (utcNow - report.PositionAt).TotalSeconds;
            // Some players report a stale or empty update time; then the position is as of now.
            if (report.IsPlaying && elapsed >= 0 && elapsed <= TimeSpan.FromDays(1).TotalSeconds) position += elapsed;
        }
        return playhead.Report(position, now, report.IsPlaying, force: trackChanged);
    }
}
