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
    public void AWordRisesAsItsSungAndSettlesAfter()
    {
        var word = new LiftWord(0, 5, 10, 11);
        Assert.Equal(0, WordLift.At(word, 9.9));
        Assert.Equal(0, WordLift.At(word, 10));
        // Up quickly at first, at its top well before the word ends, and held there.
        Assert.InRange(WordLift.At(word, 10.1), 0.4, 0.9);
        Assert.Equal(1, WordLift.At(word, 10.45), 9);
        Assert.Equal(1, WordLift.At(word, 11), 9);
        // Then back down, gently at both ends.
        Assert.InRange(WordLift.At(word, 11.05), 0.98, 1);
        Assert.Equal(0.5, WordLift.At(word, 11 + WordLift.Settle / 2), 9);
        Assert.InRange(WordLift.At(word, 11 + WordLift.Settle - 0.05), 0, 0.02);
        Assert.Equal(0, WordLift.At(word, 11 + WordLift.Settle));
        // Never lower as it rises, never higher as it settles.
        for (double t = 10; t < 10.45; t += 0.01) Assert.True(WordLift.At(word, t + 0.01) >= WordLift.At(word, t));
        for (double t = 11; t < 11.6; t += 0.01) Assert.True(WordLift.At(word, t + 0.01) <= WordLift.At(word, t));
    }

    [Fact]
    public void AQuickSyllableRisesLess()
    {
        var quick = new LiftWord(0, 1, 10, 10.1);
        double top = Enumerable.Range(0, 100).Max(k => WordLift.At(quick, 10 + k * 0.01));
        Assert.InRange(top, 0.6, 0.75);
        // It still takes its time going up and coming down.
        Assert.True(WordLift.At(quick, 10.15) > WordLift.At(quick, 10.05));
        Assert.True(WordLift.At(quick, 10.4) > 0);
    }

    [Fact]
    public void ThePlanRunsFromNowToRest()
    {
        var word = new LiftWord(0, 5, 10, 11);
        var (times, values) = WordLift.Plan(word, position: 9);
        Assert.Equal(0, times[0]);
        Assert.Equal(0, values[0]);
        Assert.Equal(1, times[1], 9); // waits at rest until the word starts
        Assert.Equal(0, values[1]);
        Assert.Equal(2 + WordLift.Settle, times[^1], 9);
        Assert.Equal(0, values[^1], 9);
        Assert.Equal(1, values.Max(), 9);
        for (int k = 1; k < times.Count; k++) Assert.True(times[k] > times[k - 1]);

        // From the middle of the word: starts where the word is now.
        (times, values) = WordLift.Plan(word, position: 10.7);
        Assert.Equal(1, values[0], 9);
        Assert.Equal(0.3 + WordLift.Settle, times[^1], 9);
        // After it: nothing left to do.
        (times, values) = WordLift.Plan(word, position: 12);
        Assert.Equal([0.0], times);
        Assert.Equal([0.0], values);
    }
}
