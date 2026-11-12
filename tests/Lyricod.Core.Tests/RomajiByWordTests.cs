using Lyricod.Core.Romanization;

namespace Lyricod.Core.Tests;

/// Romaji by word (experimental): the source's romaji spaced by Windows' words, and a kanji's kana
/// ending kept with it (Romanizer.ByWord.cs). Off, romaji is as RomanizerTests expects.
public class RomajiByWordTests
{
    [Fact]
    public void SourceRomajiReadingTheSameIsGroupedByWord()
    {
        // QQ Music's romaji, a syllable at a time, reads the same as Windows' words: shown by word.
        var reader = new FakeReader(("惹かれ", "ひかれ"), ("合う", "あう"), ("の", "の"), ("は", "は"), (" ", null), ("なぜ", "なぜ"));
        var l = new Lyrics([new LyricLine { Text = "惹かれ合うのは なぜ", Pronunciation = new("hi ka re a u no wa na ze") }], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(l);
        Assert.Equal(new Pronunciation("hikare au no wa naze", LyricLanguage.Japanese, PronunciationOrigin.SourceByWord), l.Lines[0].Pronunciation);

        // A reading that differs here and there keeps the source's letters, spaced by our words.
        var other = new Lyrics([new LyricLine { Text = "惹かれ合うのは なぜ", Pronunciation = new("hi ka re a u no wa na n de") }], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(other);
        // Off (the default), the source's romaji shows as it came.
        var plain = new Lyrics([new LyricLine { Text = "惹かれ合うのは なぜ", Pronunciation = new("hi ka re a u no wa na ze") }], "test");
        new Romanizer(reader).Fill(plain);
        Assert.Equal(new Pronunciation("hi ka re a u no wa na ze", LyricLanguage.Japanese), plain.Lines[0].Pronunciation);
        Assert.Equal("hikare au no wa nande", other.Lines[0].Pronunciation?.Text);
    }

    [Theory]
    // 明日 read "asu" by the source, "ashita" by Windows: the source's reading, our words.
    [InlineData("a su ka ra mu ko u ma de", "ashita kara mukou made", "asu kara mukou made")]
    // へ as "he" and を as "o": the source's spelling stays.
    [InlineData("to bu to ri wa to o ku he", "tobu tori wa tooku e", "tobu tori wa tooku he")]
    [InlineData("tada \"mi te, kirei\" da to te o hi i ta", "tada “mite, kirei” da to te wo hiita", "tada \"mite, kirei\" da to te o hiita")]
    [InlineData("Tō kyō no so ra", "toukyou no sora", "Tōkyō no sora")]
    // A long vowel ours leaves short, at a break: it stays with its own word.
    [InlineData("kyou wa", "kyo wa", "kyou wa")]
    public void SourceRomajiTakesOurWords(string source, string ours, string expected) =>
        Assert.Equal(expected, Romanizer.Regroup(source, ours));

    /// A line and its romaji as QQ Music times them: the line a character at a time, the romaji a
    /// syllable at a time, each starting within a millisecond of the character it's sung on.
    static (LyricLine Line, List<LyricWord> Sung) Timed(string text, params (string Syllable, double At)[] romaji)
    {
        var sung = text.Select((c, i) => new LyricWord(1 + i, 1, c.ToString())).ToList();
        var syllables = romaji.Select((r, i) => new LyricWord(r.At - 0.001, 0.2, i == 0 ? r.Syllable : " " + r.Syllable)).ToList();
        var line = new LyricLine { Start = 1, End = 1 + text.Length, Text = text, Words = sung,
            Pronunciation = new(string.Concat(syllables.Select(w => w.Text))) { Words = syllables } };
        return (line, sung);
    }

    [Fact]
    public void TimedRomajiGoesWithTheWordBeingSung()
    {
        // どれほどよかったでしょう, as QQ Music has it: っ is "'t", しょ one syllable on し, and a
        // reading here that differs (no reading at all would do) changes nothing.
        var reader = new FakeReader(("どれほど", "どれほど"), ("よかっ", "よかっ"), ("た", "た"), ("でしょう", "でしょう"));
        var (line, _) = Timed("どれほどよかったでしょう", ("do", 1), ("re", 2), ("ho", 3), ("do", 4), ("yo", 5), ("ka", 6), ("'t", 7), ("ta", 8),
            ("de", 9), ("sho", 10), ("u", 12));
        var l = new Lyrics([line], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(l);
        var p = l.Lines[0].Pronunciation!;
        Assert.Equal("dorehodo yokatta deshou", p.Text);
        Assert.Equal(PronunciationOrigin.SourceByWord, p.Origin);
        // The timing stays with the syllables, whose texts make up the line.
        Assert.Equal(["do", "re", "ho", "do", " yo", "ka", "t", "ta", " de", "sho", "u"], p.Words!.Select(w => w.Text));
        Assert.Equal(p.Text, string.Concat(p.Words!.Select(w => w.Text)));
        Assert.Equal(4.999, p.Words![4].Start, 6);

        // A kanji's syllables all start within it: 未だに "imadani", 夢に "yume ni".
        var kanji = new FakeReader(("未だに", "いまだに"), ("夢", "ゆめ"), ("に", "に"));
        var (dream, _) = Timed("未だに夢に", ("i", 1), ("ma", 1.5), ("da", 2), ("ni", 3), ("yu", 4), ("me", 4.4), ("ni", 5));
        var d = new Lyrics([dream], "test");
        new Romanizer(kanji) { GroupJapaneseByWord = true }.Fill(d);
        Assert.Equal("imadani yume ni", d.Lines[0].Pronunciation!.Text);

        // The readings needn't agree: 明日 sung "asu" where the analyser says "ashita" (lining the
        // letters up only gets this right while they're alike).
        var tomorrow = new FakeReader(("明日", "あした"), ("から", "から"));
        var (asu, _) = Timed("明日から", ("a", 1), ("su", 1.5), ("ka", 3), ("ra", 4));
        var a = new Lyrics([asu], "test");
        new Romanizer(tomorrow) { GroupJapaneseByWord = true }.Fill(a);
        Assert.Equal("asu kara", a.Lines[0].Pronunciation!.Text);

        // Off (the default), the romaji shows as it came, and keeps its timing.
        var (plain, _) = Timed("明日から", ("a", 1), ("su", 1.5), ("ka", 3), ("ra", 4));
        var off = new Lyrics([plain], "test");
        new Romanizer(tomorrow).Fill(off);
        Assert.Equal("a su ka ra", off.Lines[0].Pronunciation!.Text);
        Assert.Equal(LyricLanguage.Japanese, off.Lines[0].Pronunciation!.Language);
        Assert.Equal(4, off.Lines[0].Pronunciation!.Words!.Count);
    }

    [Fact]
    public void TimedRomajiThatIsntWithinTheLineIsLinedUpByLetters()
    {
        // A syllable timed before the line starts: the timing can't place it, the letters still can.
        var reader = new FakeReader(("夢", "ゆめ"), ("に", "に"));
        var (line, _) = Timed("夢に", ("yu", 0.5), ("me", 1.4), ("ni", 2));
        var l = new Lyrics([line], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(l);
        Assert.Equal("yume ni", l.Lines[0].Pronunciation!.Text);
        Assert.Null(l.Lines[0].Pronunciation!.Words);
    }

    [Fact]
    public void MarksInTimedRomajiSitAgainstTheirWords()
    {
        var reader = new FakeReader(("何", "なに"), ("？", null), ("夢", "ゆめ"), ("だ", "だ"));
        var (line, _) = Timed("何？夢だ", ("na", 1), ("ni", 1.5), ("?", 2), ("yu", 3), ("me", 3.5), ("da", 4));
        var l = new Lyrics([line], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(l);
        Assert.Equal("nani? yume da", l.Lines[0].Pronunciation!.Text);
    }

    [Fact]
    public void RomajiTooDifferentToLineUpIsLeftAlone() =>
        Assert.Null(Romanizer.Regroup("la la la la", "ashita kara mukou made"));

    [Fact]
    public void ByWordAKanjisKanaEndingStaysWithIt()
    {
        // Windows' analyser gives some endings apart: 泣|く, 鳴|る, 遠|く, 飛|ぶ.
        var crying = new FakeReader(("|貴方", "あなた"), ("も", "も"), ("|泣", "な"), ("く", "く"), ("ん", "ん"), ("だ", "だ"), ("ね", "ね"));
        Assert.Equal("anata mo naku n da ne", new Romanizer(crying) { GroupJapaneseByWord = true }.Romanize("貴方も泣くんだね", LyricLanguage.Japanese));
        var waves = new FakeReader(("|人里", "ひとざと"), ("|離れ", "はなれ"), ("て", "て"), ("|鳴", "な"), ("る", "る"), ("|音", "おと"), ("は", "は"),
            ("|向かい", "むかい"), ("波", "なみ"));
        Assert.Equal("hitozato hanare te naru oto wa mukai nami", new Romanizer(waves) { GroupJapaneseByWord = true }.Romanize("人里離れて鳴る音は向かい波", LyricLanguage.Japanese));
        var birds = new FakeReader(("|飛", "と"), ("ぶ", "ぶ"), ("|鳥", "とり"), ("は", "は"), ("|遠", "とお"), ("く", "く"), ("へ", "へ"));
        Assert.Equal("tobu tori wa tooku e", new Romanizer(birds) { GroupJapaneseByWord = true }.Romanize("飛ぶ鳥は遠くへ", LyricLanguage.Japanese));
        // A small っ ending still goes with what follows.
        var waiting = new FakeReader(("|待", "ま"), ("っ", "っ"), ("て", "て"));
        Assert.Equal("matte", new Romanizer(waiting) { GroupJapaneseByWord = true }.Romanize("待って", LyricLanguage.Japanese));
        // A new phrase isn't an ending.
        var phrase = new FakeReader(("|空", "そら"), ("|い", "い"));
        Assert.Equal("sora i", new Romanizer(phrase) { GroupJapaneseByWord = true }.Romanize("空い", LyricLanguage.Japanese));
        // Off (the default), Windows' words stay as it gives them.
        Assert.Equal("anata mo na ku n da ne", new Romanizer(crying).Romanize("貴方も泣くんだね", LyricLanguage.Japanese));
    }
}
