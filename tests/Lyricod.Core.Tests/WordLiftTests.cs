namespace Lyricod.Core.Tests;

public class WordLiftTests
{
    /// <summary>Every character 10 wide; spaces at the end aren't measured, as a TextBlock doesn't.</summary>
    static double Measure(string s) => s.TrimEnd().Length * 10;

    static LyricLine Row(params (double Start, double Duration, string Text)[] words) => new()
    {
        Start = words[0].Start, End = words[^1].Start + words[^1].Duration,
        Text = string.Concat(words.Select(w => w.Text)),
        Words = [.. words.Select(w => new LyricWord(w.Start, w.Duration, w.Text))],
    };

    [Fact]
    public void EachWordRisesByItself()
    {
        var words = WordLift.Words(Row((10, 0.5, "Hello "), (10.5, 0.5, "there "), (11.2, 1, "world")));
        Assert.Equal([new LiftWord(0, 6, 10, 10.5), new LiftWord(6, 12, 10.5, 11), new LiftWord(12, 17, 11.2, 12.2)], words);
    }

    [Fact]
    public void SyllablesOfOneWordRiseTogether()
    {
        // "beau", "ti", "ful " are one word; so are "don" and "'t".
        var words = WordLift.Words(Row((10, 0.25, "beau"), (10.25, 0.25, "ti"), (10.5, 0.25, "ful "), (11, 0.25, "don"), (11.25, 0.25, "'t")));
        Assert.Equal([new LiftWord(0, 10, 10, 10.75), new LiftWord(10, 15, 11, 11.5)], words);
    }

    [Fact]
    public void ChineseJapaneseAndKoreanCharactersEachRiseAlone()
    {
        var words = WordLift.Words(Row((10, 0.25, "夢"), (10.25, 0.25, "な"), (10.5, 0.25, "ら"), (10.75, 0.25, "ば"), (11, 0.25, "사"), (11.25, 0.25, "랑")));
        Assert.Equal(6, words.Count);
        Assert.Equal(new LiftWord(3, 4, 10.75, 11), words[3]);
        // A Latin word right after one doesn't join it, nor does a character after a Latin word.
        var mixed = WordLift.Words(Row((10, 0.25, "君"), (10.25, 0.25, "love"), (10.5, 0.25, "だ")));
        Assert.Equal([new LiftWord(0, 1, 10, 10.25), new LiftWord(1, 5, 10.25, 10.5), new LiftWord(5, 6, 10.5, 10.75)], mixed);
    }

    [Fact]
    public void MarksAndSpacesGoWithAWord()
    {
        // An opening mark goes with the first word; a comma and a space timed by themselves with the word before.
        var words = WordLift.Words(Row((10, 0.25, "「"), (10.25, 0.25, "夢"), (10.5, 0.25, "、"), (10.75, 0.25, "love"), (11, 0.25, " "), (11.25, 0.25, "you")));
        Assert.Equal([new LiftWord(0, 3, 10.25, 10.5), new LiftWord(3, 8, 10.75, 11), new LiftWord(8, 11, 11.25, 11.5)], words);
        // Between them the words cover the whole text.
        Assert.Equal(0, words[0].From);
        for (int k = 1; k < words.Count; k++) Assert.Equal(words[k - 1].To, words[k].From);
    }

    [Fact]
    public void NoWordTimingNoLift()
    {
        Assert.Empty(WordLift.Words(new LyricLine { Start = 10, End = 12, Text = "Hello there" }));
        Assert.Empty(WordLift.Words(Row((10, 1, "…"))));
    }

    [Fact]
    public void WordsAreCutHalfwayThroughTheSpaceBetweenThem()
    {
        var row = Row((10, 0.5, "Hello "), (10.5, 0.5, "there "), (11.2, 1, "world"));
        var spans = WordLift.Spans(WordLift.Words(row), row.Text, Measure);
        // "Hello" is 0…50, the space 50…60, "there" 60…110, the space 110…120, "world" 120…170.
        Assert.Equal([new LiftSpan(0, 55, 25), new LiftSpan(55, 115, 85), new LiftSpan(115, 170, 145)], spans);
        // Characters that touch are cut where they meet.
        var kana = Row((10, 0.3, "な"), (10.3, 0.3, "ら"));
        Assert.Equal([new LiftSpan(0, 10, 5), new LiftSpan(10, 20, 15)], WordLift.Spans(WordLift.Words(kana), kana.Text, Measure));
    }

    [Fact]
    public void AWordRisesAsItsSungAndStaysUp()
    {
        var word = new LiftWord(0, 5, 10, 11);
        Assert.Equal(0, WordLift.Up(word, 9.9));
        Assert.Equal(0, WordLift.Up(word, 10));
        // Up quickly at first, at its top well before the word ends.
        Assert.InRange(WordLift.Up(word, 10.1), 0.4, 0.9);
        Assert.Equal(1, WordLift.Up(word, 10.45), 9);
        // And there it stays, however long the rest of the line takes.
        Assert.Equal(1, WordLift.Up(word, 11));
        Assert.Equal(1, WordLift.Up(word, 11.6));
        Assert.Equal(1, WordLift.Up(word, 60));
        for (double t = 10; t < 10.6; t += 0.01) Assert.True(WordLift.Up(word, t + 0.01) >= WordLift.Up(word, t));
        // A quick syllable goes all the way up too, taking its time.
        var quick = new LiftWord(0, 1, 10, 10.1);
        Assert.InRange(WordLift.Up(quick, 10.1), 0.5, 0.99);
        Assert.Equal(1, WordLift.Up(quick, 10.16), 9);
    }

    [Fact]
    public void AWordSwellsAsItsSungAndEasesBack()
    {
        var word = new LiftWord(0, 5, 10, 11);
        Assert.Equal(0, WordLift.Swell(word, 9.9));
        Assert.Equal(0, WordLift.Swell(word, 10));
        Assert.InRange(WordLift.Swell(word, 10.1), 0.4, 0.9);
        Assert.Equal(1, WordLift.Swell(word, 10.45), 9);
        Assert.Equal(1, WordLift.Swell(word, 11), 9);
        // Then back to its size, gently at both ends.
        Assert.InRange(WordLift.Swell(word, 11.05), 0.98, 1);
        Assert.Equal(0.5, WordLift.Swell(word, 11 + WordLift.Settle / 2), 9);
        Assert.InRange(WordLift.Swell(word, 11 + WordLift.Settle - 0.05), 0, 0.02);
        Assert.Equal(0, WordLift.Swell(word, 11 + WordLift.Settle));
        for (double t = 10; t < 10.45; t += 0.01) Assert.True(WordLift.Swell(word, t + 0.01) >= WordLift.Swell(word, t));
        for (double t = 11; t < 11.6; t += 0.01) Assert.True(WordLift.Swell(word, t + 0.01) <= WordLift.Swell(word, t));
    }

    [Fact]
    public void AQuickSyllableSwellsLess()
    {
        var quick = new LiftWord(0, 1, 10, 10.1);
        double top = Enumerable.Range(0, 100).Max(k => WordLift.Swell(quick, 10 + k * 0.01));
        Assert.InRange(top, 0.6, 0.75);
        // It still takes its time growing and easing back.
        Assert.True(WordLift.Swell(quick, 10.15) > WordLift.Swell(quick, 10.05));
        Assert.True(WordLift.Swell(quick, 10.4) > 0);
    }

    [Fact]
    public void ThePlanRunsFromNowUntilTheWordIsUpAndItsOwnSize()
    {
        var word = new LiftWord(0, 5, 10, 11);
        var (times, up, swell) = WordLift.Plan(word, position: 9);
        Assert.Equal(0, times[0]);
        Assert.Equal(0, up[0]);
        Assert.Equal(0, swell[0]);
        Assert.Equal(1, times[1], 9); // waits at rest until the word starts
        Assert.Equal(0, up[1]);
        Assert.Equal(2 + WordLift.Settle, times[^1], 9);
        Assert.Equal(1, up[^1], 9);   // still up
        Assert.Equal(0, swell[^1], 9); // its own size again
        Assert.Equal(1, swell.Max(), 9);
        Assert.Equal(times.Count, up.Count);
        Assert.Equal(times.Count, swell.Count);
        for (int k = 1; k < times.Count; k++)
        {
            Assert.True(times[k] > times[k - 1]);
            Assert.True(up[k] >= up[k - 1]);
        }

        // From the middle of the word: starts where the word is now.
        (times, up, swell) = WordLift.Plan(word, position: 10.7);
        Assert.Equal(1, up[0], 9);
        Assert.Equal(1, swell[0], 9);
        Assert.Equal(0.3 + WordLift.Settle, times[^1], 9);
        // Long after it: up, and nothing left to do.
        (times, up, swell) = WordLift.Plan(word, position: 12);
        Assert.Equal([0.0], times);
        Assert.Equal([1.0], up);
        Assert.Equal([0.0], swell);
    }

    [Fact]
    public void WhenTheLineIsOverItsWordsComeBackDown()
    {
        var (times, values) = WordLift.Release(1);
        Assert.Equal(0, times[0]);
        Assert.Equal(1, values[0]);
        Assert.Equal(WordLift.Settle, times[^1], 9);
        Assert.Equal(0, values[^1]);
        // Gently at both ends, halfway at half time.
        Assert.InRange(values[1], 0.97, 1);
        Assert.Equal(0.5, values[values.Count / 2], 9);
        Assert.InRange(values[^2], 0, 0.03);
        for (int k = 1; k < times.Count; k++)
        {
            Assert.True(times[k] > times[k - 1]);
            Assert.True(values[k] <= values[k - 1]);
        }
        // From partway up: the same way down, from there. From rest: stays there.
        Assert.Equal(0.4, WordLift.Release(0.4).Values[0], 9);
        Assert.Equal(0.2, WordLift.Release(0.4).Values[values.Count / 2], 9);
        Assert.All(WordLift.Release(0).Values, v => Assert.Equal(0, v));
    }
}
