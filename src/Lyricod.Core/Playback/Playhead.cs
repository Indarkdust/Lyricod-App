namespace Lyricod.Core;

/// <summary>
/// Where the song is between player reports: a position known at one instant, carried forward while
/// playing. Instants are seconds on any steady clock (the app uses <see
/// cref="System.Diagnostics.Stopwatch"/>), so tests can drive it by hand.
/// </summary>
public sealed class Playhead
{
    double _position;
    double _at;

    public bool IsPlaying { get; private set; }

    /// <summary>Changes whenever the playhead is re-anchored (seek, pause, correction), so animations know to re-plan.</summary>
    public int Version { get; private set; }

    /// <summary>Seconds, positive shows lyrics earlier (global plus this song's).</summary>
    public double Offset { get; set; }

    /// <summary>The player's position (without offset) at <paramref name="now"/>.</summary>
    public double RawPosition(double now) => _position + (IsPlaying ? now - _at : 0);

    /// <summary>The lyric position at <paramref name="now"/>: the player's, plus the offset.</summary>
    public double Position(double now) => RawPosition(now) + Offset;

    public void Anchor(double position, double at, bool playing)
    {
        _position = Math.Max(position, 0);
        _at = at;
        IsPlaying = playing;
        Version++;
    }

    /// <summary>
    /// Takes a player report. Small jitter is ignored so animations stay smooth; a changed
    /// play state, a seek, or a big drift re-anchors. Returns whether it did.
    /// </summary>
    public bool Report(double position, double at, bool playing, bool force = false)
    {
        if (!force && playing == IsPlaying && Math.Abs(position - RawPosition(at)) <= Tolerance) return false;
        Anchor(position, at, playing);
        return true;
    }

    /// <summary>Drift tolerated before re-anchoring, in seconds.</summary>
    public const double Tolerance = 0.25;
}

/// <summary>How a line's sung part grows: per word, from x to x, in its own seconds.</summary>
public readonly record struct SweepStop(double Start, double End, double From, double To);

/// <summary>
/// Plans the word-by-word colour sweep as keyframes: positions come from measuring the line's text,
/// and the plan runs from "now" to the end of the line so it can be re-planned.
/// </summary>
public static class Sweep
{
    /// <summary>Each word's start and end time and the sung width at each, for a line of <paramref name="width"/>. Empty without word timing.</summary>
    public static List<SweepStop> Stops(LyricLine piece, double width, Func<string, double> measure)
    {
        var stops = new List<SweepStop>();
        if (piece.Words is not { } words) return stops;
        double total = Math.Max(measure(piece.Text), 1);
        var prefix = "";
        double x = 0;
        foreach (var w in words)
        {
            prefix += w.Text;
            double from = x;
            x = Math.Min(measure(prefix) / total, 1) * width;
            stops.Add(new SweepStop(w.Start, w.End, from, x));
        }
        return stops;
    }

    public static double SungWidth(IReadOnlyList<SweepStop> stops, LyricLine piece, double width, double t)
    {
        if (stops.Count == 0) return t >= piece.Start ? width : 0;
        int i = -1;
        for (int k = 0; k < stops.Count; k++) if (stops[k].Start <= t) i = k;
        if (i < 0) return 0;
        var s = stops[i];
        double p = s.End > s.Start ? Math.Clamp((t - s.Start) / (s.End - s.Start), 0, 1) : 1;
        return s.From + (s.To - s.From) * p;
    }

    /// <summary>
    /// Keyframes from <paramref name="position"/> to the end of the piece, each straight from the
    /// one before. A line without word timing lights up as it starts.
    /// </summary>
    public static List<SweepKey> Plan(IReadOnlyList<SweepStop> stops, LyricLine piece, double width, double position)
    {
        var keys = new List<SweepKey> { new(0, SungWidth(stops, piece, width, position)) };
        if (stops.Count == 0)
        {
            if (piece.Start > position)
                keys.AddRange([new(piece.Start - position, 0), new(piece.Start - position + 0.08, width)]);
        }
        else
        {
            foreach (var w in stops.Where(w => w.End > position))
            {
                if (w.Start > position) keys.Add(new(w.Start - position, w.From));
                keys.Add(new(Math.Max(w.End - position, keys[^1].Time), w.To));
            }
        }
        for (int i = 1; i < keys.Count; i++) keys[i] = keys[i] with { Time = Math.Max(keys[i].Time, keys[i - 1].Time) };
        return keys;
    }
}
