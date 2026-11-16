namespace Lyricod.Core;

/// <summary>
/// A moment of the smooth sweep's plan: seconds from now and the sung width then. <c>Ease1</c> and
/// <c>Ease2</c> shape the way there from the moment before: the heights of a cubic Bézier's control
/// points at a third and two thirds of the time (1/3 and 2/3 is a straight line).
/// </summary>
public readonly record struct SweepKey(double Time, double Value, double Ease1 = 1.0 / 3, double Ease2 = 2.0 / 3);

/// <summary>
/// The sweep without <see cref="Sweep.Plan"/>'s jumps in speed between words of different lengths.
/// It passes each word's edges at the same moments, on a monotone cubic: the speed where two words
/// meet is the harmonic mean of theirs (at most twice the slower, so it can't overshoot or turn
/// back). A rest is still a stop, reached and left at the word's own speed. The one change to the
/// timing: a word timed shorter than <see cref="Least"/> (sources give some a few milliseconds)
/// borrows the rest from its neighbours, or the sweep would cross it in a frame. Asked to be early
/// (<see cref="Even"/>), it also lets a run of quick words start ahead of its time, so the sweep
/// doesn't crawl and then dash.
/// </summary>
public sealed class SweepCurve
{
    /// <summary>A gap between words this short (seconds) isn't a rest: the word before lasts until the next starts.</summary>
    public const double Bridge = 0.08;

    /// <summary>The least a word's sweep takes (seconds).</summary>
    public const double Least = 0.08;

    /// <summary>
    /// For the early sweep: neighbours whose speeds differ by more than <see cref="Ratio"/> have
    /// their boundary moved, at most <see cref="Lead"/> ahead of its time or <see cref="Lag"/>
    /// behind it (seconds). Lit a little early reads fine; lit late doesn't.
    /// </summary>
    public const double Ratio = 3, Lead = 0.12, Lag = 0.04;

    /// <summary>The least time between two points, so a word of no length still takes a moment.</summary>
    const double Step = 0.001;

    // Points in time order; between point i and i + 1 the speed starts at _out[i] and ends at _in[i + 1].
    readonly double[] _t, _x, _out, _in;

    SweepCurve(List<double> t, List<double> x)
    {
        int n = t.Count;
        (_t, _x, _out, _in) = ([.. t], [.. x], new double[n], new double[n]);
        // Each stretch's own speed; 0 is a rest.
        var speed = new double[Math.Max(n - 1, 0)];
        for (int i = 0; i + 1 < n; i++) speed[i] = (_x[i + 1] - _x[i]) / (_t[i + 1] - _t[i]);
        for (int i = 0; i < n; i++)
        {
            double before = i > 0 ? speed[i - 1] : 0, after = i + 1 < n ? speed[i] : 0;
            double shared = before > 0 && after > 0 ? 2 * before * after / (before + after) : 0;
            _in[i] = shared > 0 ? shared : before;
            _out[i] = shared > 0 ? shared : after;
        }
    }

    /// <summary>The curve through <paramref name="stops"/>, or null when they sweep nothing.</summary>
    /// <param name="early">Quick words after a slow one may start ahead of their time (<see cref="Even"/>).</param>
    public static SweepCurve? From(IReadOnlyList<SweepStop> stops, bool early = false)
    {
        var words = stops.Where(s => s.To - s.From > 0.01).ToList();
        if (words.Count == 0) return null;
        var t = new List<double> { words[0].Start };
        var x = new List<double> { words[0].From };
        void Add(double time, double at)
        {
            t.Add(Math.Max(time, t[^1] + Step));
            x.Add(Math.Max(at, x[^1]));
        }
        for (int k = 0; k < words.Count; k++)
        {
            var word = words[k];
            if (k + 1 == words.Count) { Add(word.End, word.To); break; }
            double next = words[k + 1].Start;
            if (next - word.End > Bridge) Add(word.End, word.To);
            Add(next, word.To);
        }
        var timed = t.ToArray();
        Spread(t, x);
        if (early) Even(t, x, timed);
        return new SweepCurve(t, x);
    }

    /// <summary>
    /// Evens out speeds that change more than <see cref="Ratio"/> to one between two words that
    /// follow each other, by moving their boundary into the slower word: earlier before quick
    /// words (by <see cref="Lead"/> at most, from <paramref name="timed"/>, its time in the
    /// source), later after them (by <see cref="Lag"/>). Several passes, so a run of quick words
    /// shares what it gains. A boundary beside a rest stays: a word after a rest never starts early.
    /// </summary>
    static void Even(List<double> t, List<double> x, double[] timed)
    {
        bool Sweeps(int i) => x[i + 1] - x[i] > 0;
        for (int pass = 0; pass < 8; pass++)
            for (int i = 1; i + 1 < t.Count; i++)
            {
                if (!Sweeps(i - 1) || !Sweeps(i)) continue;
                double before = t[i] - t[i - 1], after = t[i + 1] - t[i], a = x[i] - x[i - 1], b = x[i + 1] - x[i];
                double move = b / after > Ratio * a / before ? -(b * before - Ratio * a * after) / (b + Ratio * a)
                    : a / before > Ratio * b / after ? (a * after - Ratio * b * before) / (a + Ratio * b) : 0;
                // Neither word is left with less than Least (or than it has).
                double from = Math.Max(timed[i] - Lead, t[i - 1] + Math.Min(Least, before)), to = Math.Min(timed[i] + Lag, t[i + 1] - Math.Min(Least, after));
                if (move != 0 && from <= to) t[i] = Math.Clamp(t[i] + move, from, to);
            }
    }

    /// <summary>
    /// Gives each stretch shorter than <see cref="Least"/> the time it lacks: from a rest after it
    /// if there is one (no word then starts off its time), else from the words either side, which
    /// keep at least as much themselves. A word after a rest never starts early.
    /// </summary>
    static void Spread(List<double> t, List<double> x)
    {
        bool Sweeps(int i) => x[i + 1] - x[i] > 0;
        for (int i = 0; i + 1 < t.Count; i++)
        {
            if (!Sweeps(i)) continue;
            double need = Least - (t[i + 1] - t[i]);
            if (need <= 0) continue;
            double before = i > 0 && Sweeps(i - 1) ? Math.Max(t[i] - t[i - 1] - Least, 0) : 0;
            bool rest = i + 2 < t.Count && !Sweeps(i + 1);
            double after = i + 2 < t.Count ? Math.Max(t[i + 2] - t[i + 1] - (rest ? Step : Least), 0) : 0;
            double late = Math.Min(after, rest ? need : need / 2), early = Math.Min(before, need - late);
            late = Math.Min(after, need - early);
            t[i] -= early;
            t[i + 1] += late;
        }
    }

    /// <summary>The sung width at <paramref name="time"/>.</summary>
    public double At(double time) => Value(time).Width;

    /// <summary>How fast the sung width is growing at <paramref name="time"/> (pixels a second).</summary>
    public double Speed(double time) => Value(time).Speed;

    (double Width, double Speed) Value(double time)
    {
        if (time <= _t[0]) return (time < _t[0] ? 0 : _x[0], 0);
        if (time >= _t[^1]) return (_x[^1], 0);
        int i = Array.BinarySearch(_t, time);
        if (i < 0) i = ~i - 1;
        double h = _t[i + 1] - _t[i], s = (time - _t[i]) / h, rise = _x[i + 1] - _x[i];
        if (rise <= 0) return (_x[i], 0);
        // Cubic Hermite: the two points and the speed at each.
        double s2 = s * s, s3 = s2 * s;
        double width = _x[i] + (3 * s2 - 2 * s3) * rise + h * ((s3 - 2 * s2 + s) * _out[i] + (s3 - s2) * _in[i + 1]);
        double speed = (6 * s - 6 * s2) * rise / h + (3 * s2 - 4 * s + 1) * _out[i] + (3 * s2 - 2 * s) * _in[i + 1];
        return (Math.Clamp(width, _x[i], _x[i + 1]), Math.Max(speed, 0));
    }

    /// <summary>
    /// The rest of the sweep from <paramref name="position"/>: the first key is now, then one for
    /// each point still to come, each with the easing that gets there exactly as the curve does.
    /// </summary>
    public List<SweepKey> Plan(double position)
    {
        var (width, speed) = Value(position);
        var keys = new List<SweepKey> { new(0, width) };
        double from = position;
        for (int i = 0; i < _t.Length; i++)
        {
            if (_t[i] <= position) continue;
            double time = _t[i] - position, rise = _x[i] - width, took = _t[i] - from;
            // A cubic in time is a Bézier with its control points at 1/3 and 2/3 of the way.
            keys.Add(rise > 0
                ? new(time, _x[i], Math.Clamp(speed * took / (3 * rise), 0, 1), Math.Clamp(1 - _in[i] * took / (3 * rise), 0, 1))
                : new(time, _x[i]));
            (from, width, speed) = (_t[i], _x[i], _out[i]);
        }
        return keys;
    }
}
