namespace Lyricod.Core;

/// <summary>
/// Where the song is, between player reports: a position known at one instant, carried
/// forward while playing. Instants are in seconds on any steady clock (the app uses
/// <see cref="System.Diagnostics.Stopwatch"/>), so tests can drive it by hand.
/// </summary>
public sealed class Playhead
{
    double _position;
    double _at;

    public bool IsPlaying { get; private set; }

    /// <summary>Changes whenever the playhead is re-anchored (seek, pause, correction), so
    /// animations that run on their own know to re-plan.</summary>
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
/// Plans the word-by-word colour sweep as keyframes, like the macOS app: positions come
/// from measuring the line's text, and the plan runs from "now" to the end of the line, so
/// it can be re-planned whenever the clock re-anchors.
/// </summary>
public static class Sweep
{
    /// <summary>Each word's start and end time and the sung width at each, for a line of
    /// <paramref name="width"/>. Empty for a line without word timing.</summary>
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

    /// <summary>The sung width at <paramref name="t"/>.</summary>
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
    /// Keyframes from <paramref name="position"/> to the end of the piece: times in seconds
    /// from now, and sung widths. A line without word timing lights up as it starts.
    /// </summary>
    public static (List<double> Times, List<double> Values) Plan(IReadOnlyList<SweepStop> stops, LyricLine piece, double width, double position)
    {
        var times = new List<double> { 0 };
        var values = new List<double> { SungWidth(stops, piece, width, position) };
        if (stops.Count == 0)
        {
            if (piece.Start > position)
            {
                times.AddRange([piece.Start - position, piece.Start - position + 0.08]);
                values.AddRange([0, width]);
            }
        }
        else
        {
            foreach (var w in stops.Where(w => w.End > position))
            {
                if (w.Start > position) { times.Add(w.Start - position); values.Add(w.From); }
                times.Add(Math.Max(w.End - position, times[^1]));
                values.Add(w.To);
            }
        }
        for (int i = 1; i < times.Count; i++) times[i] = Math.Max(times[i], times[i - 1]);
        return (times, values);
    }
}
