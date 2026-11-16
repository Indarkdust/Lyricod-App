namespace Lyricod.Core.Tests;

public class SweepCurveTests
{
    /// Words one after another from <paramref name="start"/>: how long each is sung, and how wide it is.
    static List<SweepStop> Words(double start, params (double Seconds, double Width, double GapAfter)[] words)
    {
        var stops = new List<SweepStop>();
        double t = start, x = 0;
        foreach (var (seconds, width, gap) in words)
        {
            stops.Add(new SweepStop(t, t + seconds, x, x + width));
            (t, x) = (t + seconds + gap, x + width);
        }
        return stops;
    }

    /// A held syllable, then two quick ones, then an ordinary one: 40 px each.
    static readonly List<SweepStop> HeldThenQuick = Words(10, (2, 40, 0), (0.1, 40, 0), (0.1, 40, 0), (0.4, 40, 0));

    [Fact]
    public void EveryWordStartsAndEndsOnTime()
    {
        var curve = SweepCurve.From(HeldThenQuick)!;
        foreach (var stop in HeldThenQuick)
        {
            Assert.Equal(stop.From, curve.At(stop.Start), 6);
            Assert.Equal(stop.To, curve.At(stop.End), 6);
        }
        Assert.Equal(0, curve.At(9));
        Assert.Equal(160, curve.At(13));
    }

    [Fact]
    public void ItNeverTurnsBackOrRunsPastAWord()
    {
        var curve = SweepCurve.From(HeldThenQuick)!;
        double last = 0;
        for (double t = 9.9; t < 12.8; t += 0.002)
        {
            double at = curve.At(t);
            Assert.True(at >= last - 1e-9);
            // Within the word being sung, as the straight sweep is.
            var stop = HeldThenQuick.LastOrDefault(s => s.Start <= t);
            if (stop.End > stop.Start) Assert.InRange(at, stop.From - 1e-6, stop.To + 1e-6);
            last = at;
        }
    }

    [Fact]
    public void TheSpeedDoesntJumpBetweenWords()
    {
        var curve = SweepCurve.From(HeldThenQuick)!;
        // The straight sweep goes from 20 to 400 pixels a second at 12 s; this one has one speed there.
        Assert.Equal(curve.Speed(12 - 1e-6), curve.Speed(12 + 1e-6), 1);
        // It's between the two words' speeds, and no more than twice the slower.
        Assert.InRange(curve.Speed(12), 20, 40.001);
        foreach (double boundary in new[] { 12.1, 12.2 }) Assert.Equal(curve.Speed(boundary - 1e-6), curve.Speed(boundary + 1e-6), 1);
        // And it never changes much from one millisecond to the next.
        for (double t = 10.001; t < 12.59; t += 0.001) Assert.InRange(Math.Abs(curve.Speed(t + 0.001) - curve.Speed(t)), 0, 25);
    }

    [Fact]
    public void WordsOfOneLengthSweepAsBefore()
    {
        var even = Words(5, (0.5, 30, 0), (0.5, 30, 0), (0.5, 30, 0));
        var curve = SweepCurve.From(even)!;
        var line = new LyricLine { Start = 5, End = 6.5, Text = "abc" };
        for (double t = 4.9; t < 6.6; t += 0.01) Assert.Equal(Sweep.SungWidth(even, line, 90, t), curve.At(t), 6);
    }

    [Fact]
    public void ARestIsAStopAndAShortGapIsnt()
    {
        // Half a second's rest after the first word: it arrives at its own speed, waits, and sets off at the next word's.
        var rest = SweepCurve.From(Words(10, (1, 50, 0.5), (0.5, 50, 0)))!;
        Assert.Equal(50, rest.At(11), 6);
        Assert.Equal(50, rest.At(11.25), 6);
        Assert.Equal(0, rest.Speed(11.25));
        Assert.Equal(50, rest.Speed(10.999), 1);
        Assert.Equal(100, rest.Speed(11.501), 1);

        // 20 ms between them is how the source timed it, not a rest: the first word lasts until the second starts.
        var gap = SweepCurve.From(Words(10, (1, 50, 0.02), (0.5, 50, 0)))!;
        Assert.Equal(50, gap.At(11.02), 6);
        Assert.True(gap.Speed(11.01) > 0);
        Assert.Equal(gap.Speed(11.02 - 1e-6), gap.Speed(11.02 + 1e-6), 1);
    }

    [Fact]
    public void WordsOfNoLengthOrNoWidthDontBreakIt()
    {
        // A space timed by itself (no width), and a word of no length.
        var stops = new List<SweepStop> { new(10, 10.5, 0, 40), new(10.5, 10.5, 40, 40), new(10.5, 10.5, 40, 80), new(10.5, 11, 80, 120) };
        var curve = SweepCurve.From(stops)!;
        double last = 0;
        for (double t = 9.9; t < 11.1; t += 0.001)
        {
            Assert.True(curve.At(t) >= last - 1e-9);
            last = curve.At(t);
        }
        Assert.Equal(120, curve.At(11), 6);
        Assert.Null(SweepCurve.From([]));
        Assert.Null(SweepCurve.From([new(1, 2, 0, 0)]));
    }

    [Fact]
    public void AWordTimedInAFewMillisecondsBorrowsFromItsNeighbours()
    {
        // 40 px in 6 ms, between two ordinary syllables: the sweep would cross it in one frame.
        var stops = Words(10, (0.4, 40, 0), (0.006, 40, 0), (0.4, 40, 0));
        var curve = SweepCurve.From(stops)!;
        // It now takes 80 ms, half from each side: 37 ms early in, 37 ms late out.
        Assert.Equal(40, curve.At(10.4 - 0.037), 3);
        Assert.Equal(80, curve.At(10.406 + 0.037), 3);
        Assert.InRange(curve.At(10.403), 50, 70);
        Assert.Equal(0, curve.At(10), 6);
        Assert.Equal(120, curve.At(10.806), 6);
        // No faster anywhere than a word of that width in that time can be (at most twice its own speed).
        for (double t = 10; t < 10.81; t += 0.001) Assert.InRange(curve.Speed(t), 0, 2 * 40 / SweepCurve.Least + 1);

        // Before a rest it takes the rest's time: nothing else moves, and the next word still starts on time.
        var rest = SweepCurve.From(Words(10, (0.4, 40, 0), (0.01, 40, 0.5), (0.4, 40, 0)))!;
        Assert.Equal(40, rest.At(10.4), 6);
        Assert.Equal(80, rest.At(10.48), 6);
        Assert.Equal(80, rest.At(10.9), 6);
        Assert.Equal(0, rest.Speed(10.905));
        Assert.True(rest.At(10.92) > 80);

        // After a rest it never starts early: it only runs on into the next word's time.
        var onset = SweepCurve.From(Words(10, (0.4, 40, 0.5), (0.01, 40, 0), (0.4, 40, 0)))!;
        Assert.Equal(40, onset.At(10.9), 6);
        Assert.Equal(80, onset.At(10.98), 3);
    }

    [Fact]
    public void AskedToBeEarlyQuickWordsStartAheadOfTheirTime()
    {
        // Held for 2 s, two quick ones of 0.1 s, then 0.4 s: the dash starts 120 ms early (no more)
        // and ends 25 ms late (that leaves it three times the next word's speed, which is allowed).
        var early = SweepCurve.From(HeldThenQuick, early: true)!;
        Assert.Equal(40, early.At(12 - SweepCurve.Lead), 6);
        Assert.Equal(80, early.At(12.1), 6);
        Assert.Equal(120, early.At(12.225), 6);
        Assert.Equal(0, early.At(10), 6);
        Assert.Equal(160, early.At(12.6), 6);
        // It's slower at its fastest than the sweep that keeps to the times, and still never turns back.
        var onTime = SweepCurve.From(HeldThenQuick)!;
        double Top(SweepCurve c) => Enumerable.Range(0, 2700).Max(k => c.Speed(10 + k * 0.001));
        Assert.True(Top(early) < 0.75 * Top(onTime));
        double last = 0;
        for (double t = 9.9; t < 12.7; t += 0.002) { Assert.True(early.At(t) >= last - 1e-9); last = early.At(t); }
        // No boundary is more than Lead ahead of its time or Lag behind it.
        foreach (var stop in HeldThenQuick.Skip(1))
        {
            Assert.True(early.At(stop.Start - SweepCurve.Lead - 0.001) <= stop.From + 1e-6);
            Assert.True(early.At(stop.Start + SweepCurve.Lag + 0.001) >= stop.From - 1e-6);
        }

        // Words that differ less than three to one are left on their times.
        var even = Words(5, (0.5, 30, 0), (0.25, 30, 0), (0.5, 30, 0));
        for (double t = 4.9; t < 6.4; t += 0.01) Assert.Equal(SweepCurve.From(even)!.At(t), SweepCurve.From(even, early: true)!.At(t), 9);

        // A quick word after a rest still starts on time.
        var rest = SweepCurve.From(Words(10, (1, 40, 0.5), (0.1, 40, 0), (0.1, 40, 0), (1, 40, 0)), early: true)!;
        Assert.Equal(40, rest.At(11.5), 6);
        Assert.Equal(0, rest.Speed(11.4));
    }

    /// The sung width a plan gives <paramref name="seconds"/> from its start, as the compositor draws it.
    static double Drawn(List<SweepKey> keys, double seconds)
    {
        for (int k = 1; k < keys.Count; k++)
        {
            if (seconds > keys[k].Time) continue;
            double u = (seconds - keys[k - 1].Time) / (keys[k].Time - keys[k - 1].Time);
            double eased = 3 * (1 - u) * (1 - u) * u * keys[k].Ease1 + 3 * (1 - u) * u * u * keys[k].Ease2 + u * u * u;
            return keys[k - 1].Value + (keys[k].Value - keys[k - 1].Value) * eased;
        }
        return keys[^1].Value;
    }

    [Theory]
    [InlineData(9.5)]   // before the line
    [InlineData(10.7)]  // in the held word
    [InlineData(12.05)] // in a quick one
    [InlineData(11.2)]  // in a rest
    public void ThePlanDrawsTheCurveFromNow(double position)
    {
        var stops = position == 11.2 ? Words(10, (1, 50, 0.5), (0.5, 50, 0)) : HeldThenQuick;
        var curve = SweepCurve.From(stops)!;
        var keys = curve.Plan(position);
        Assert.Equal(0, keys[0].Time);
        Assert.Equal(curve.At(position), keys[0].Value, 9);
        Assert.Equal(stops[^1].End - position, keys[^1].Time, 6);
        Assert.Equal(stops[^1].To, keys[^1].Value, 6);
        for (int k = 1; k < keys.Count; k++)
        {
            Assert.True(keys[k].Time > keys[k - 1].Time);
            Assert.InRange(keys[k].Ease1, 0, 1);
            Assert.InRange(keys[k].Ease2, 0, 1);
        }
        for (double s = 0; s < keys[^1].Time; s += 0.004) Assert.Equal(curve.At(position + s), Drawn(keys, s), 4);
        // After the line there's nothing left to do.
        Assert.Equal([new SweepKey(0, stops[^1].To)], curve.Plan(20));
    }
}
