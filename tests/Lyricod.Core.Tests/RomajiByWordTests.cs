using Lyricod.Core.Romanization;

namespace Lyricod.Core.Tests;

/// Romaji by word (experimental): the source's romaji spaced by Windows' words, and a kanji's kana
/// ending kept with it (Romanizer.ByWord.cs). Off, romaji is as RomanizerTests expects.
public class RomajiByWordTests
{
    static Lyrics Filled(FakeReader reader, LyricLine line, bool byWord = true)
    {
        var lyrics = new Lyrics([line], "test");
        new Romanizer(reader) { GroupJapaneseByWord = byWord }.Fill(lyrics);
        return lyrics;
    }

    /// Romaji by word on: the line's romaji as Windows' words spaced it.
    static string? ByWord(FakeReader reader, string text) =>
        new Romanizer(reader) { GroupJapaneseByWord = true }.Romanize(text, LyricLanguage.Japanese);

    [Fact]
    public void SourceRomajiReadingTheSameIsGroupedByWord()
    {
        // QQ Music's romaji, a syllable at a time, reads the same as Windows' words: shown by word.
        var reader = new FakeReader(("惹かれ", "ひかれ"), ("合う", "あう"), ("の", "の"), ("は", "は"), (" ", null), ("なぜ", "なぜ"));
        var l = Filled(reader, new LyricLine { Text = "惹かれ合うのは なぜ", Pronunciation = new("hi ka re a u no wa na ze") });
        Assert.Equal(new Pronunciation("hikare au no wa naze", LyricLanguage.Japanese, PronunciationOrigin.SourceByWord), l.Lines[0].Pronunciation);

        // A reading that differs here and there keeps the source's letters, spaced by our words.
        var other = Filled(reader, new LyricLine { Text = "惹かれ合うのは なぜ", Pronunciation = new("hi ka re a u no wa na n de") });
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
        var p = Filled(reader, line).Lines[0].Pronunciation!;
        Assert.Equal("dorehodo yokatta deshou", p.Text);
        Assert.Equal(PronunciationOrigin.SourceByWord, p.Origin);
        // The timing stays with the syllables, whose texts make up the line.
        Assert.Equal(["do", "re", "ho", "do", " yo", "ka", "t", "ta", " de", "sho", "u"], p.Words!.Select(w => w.Text));
        Assert.Equal(p.Text, string.Concat(p.Words!.Select(w => w.Text)));
        Assert.Equal(4.999, p.Words![4].Start, 6);

        // A kanji's syllables all start within it: 未だに "imadani", 夢に "yume ni".
        var kanji = new FakeReader(("未だに", "いまだに"), ("夢", "ゆめ"), ("に", "に"));
        var (dream, _) = Timed("未だに夢に", ("i", 1), ("ma", 1.5), ("da", 2), ("ni", 3), ("yu", 4), ("me", 4.4), ("ni", 5));
        Assert.Equal("imadani yume ni", Filled(kanji, dream).Lines[0].Pronunciation!.Text);

        // The readings needn't agree: 明日 sung "asu" where the analyser says "ashita" (lining the
        // letters up only gets this right while they're alike).
        var tomorrow = new FakeReader(("明日", "あした"), ("から", "から"));
        var (asu, _) = Timed("明日から", ("a", 1), ("su", 1.5), ("ka", 3), ("ra", 4));
        Assert.Equal("asu kara", Filled(tomorrow, asu).Lines[0].Pronunciation!.Text);

        // Off (the default), the romaji shows as it came, and keeps its timing.
        var (plain, _) = Timed("明日から", ("a", 1), ("su", 1.5), ("ka", 3), ("ra", 4));
        var off = Filled(tomorrow, plain, byWord: false);
        Assert.Equal("a su ka ra", off.Lines[0].Pronunciation!.Text);
        Assert.Equal(LyricLanguage.Japanese, off.Lines[0].Pronunciation!.Language);
        Assert.Equal(4, off.Lines[0].Pronunciation!.Words!.Count);
    }

    [Fact]
    public void TimedRomajiTakesTheSameWordsAsLyricodsOwn()
    {
        // Windows' analyser as it splits these (see ByWordAKanjisKanaEndingStaysWithIt): a kanji's
        // ending apart (鳴|る), て a word of its own, a small っ before the word it doubles.
        var waves = new FakeReader(("|人里", "ひとざと"), ("|離れ", "はなれ"), ("て", "て"), ("|鳴", "な"), ("る", "る"), ("|音", "おと"), ("は", "は"),
            ("|向かい", "むかい"), ("波", "なみ"));
        var (line, _) = Timed("人里離れて鳴る音は向かい波", ("hi", 1), ("to", 1.3), ("za", 2), ("to", 2.5), ("ha", 3), ("na", 3.5), ("re", 4), ("te", 5),
            ("na", 6), ("ru", 7), ("o", 8), ("to", 8.5), ("wa", 9), ("mu", 10), ("ka", 11), ("i", 12), ("na", 13), ("mi", 13.5));
        var l = Filled(waves, line);
        Assert.Equal("hitozato hanare te naru oto wa mukai nami", l.Lines[0].Pronunciation!.Text);
        Assert.NotNull(l.Lines[0].Pronunciation!.Words);

        var waiting = new FakeReader(("|待", "ま"), ("っ", "っ"), ("て", "て"), ("|泣", "な"), ("く", "く"), ("ん", "ん"), ("だ", "だ"), ("ね", "ね"));
        var (wait, _) = Timed("待って泣くんだね", ("ma", 1), ("'t", 2), ("te", 3), ("na", 4), ("ku", 5), ("n", 6), ("da", 7), ("ne", 8));
        var w = Filled(waiting, wait);
        Assert.Equal("matte naku n da ne", w.Lines[0].Pronunciation!.Text);
        Assert.NotNull(w.Lines[0].Pronunciation!.Words);
    }

    [Fact]
    public void TimedRomajiThatIsntWithinTheLineIsLinedUpByLetters()
    {
        // A syllable timed before the line starts: the timing can't place it, the letters still can.
        // Each word is then sung when the characters it reads are: 夢 from 1 to 2, に from 2 to 3.
        var reader = new FakeReader(("夢", "ゆめ"), ("に", "に"));
        var (line, _) = Timed("夢に", ("yu", 0.5), ("me", 1.4), ("ni", 2));
        var l = new Lyrics([line], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(l);
        Assert.Equal("yume ni", l.Lines[0].Pronunciation!.Text);
        Assert.Equal([new LyricWord(1, 1, "yume"), new LyricWord(2, 1, " ni")], l.Lines[0].Pronunciation!.Words);
    }

    /// A line timed a character at a time from 1 s, a second each, as a source without romaji has it.
    static LyricLine ByCharacter(string text, Pronunciation? romaji = null) => new()
    {
        Start = 1, End = 1 + text.Length, Text = text, Pronunciation = romaji,
        Words = [.. text.Select((c, i) => new LyricWord(1 + i, 1, c.ToString()))],
    };

    [Fact]
    public void RomajiWorkedOutHereIsSungWhenItsCharactersAre()
    {
        // No romaji from the source: each word of ours is lit while the characters it reads are sung.
        var reader = new FakeReader(("|人里", "ひとざと"), ("|離れ", "はなれ"), ("て", "て"), ("|鳴", "な"), ("る", "る"), ("|音", "おと"), ("は", "は"));
        var l = new Lyrics([ByCharacter("人里離れて鳴る音は")], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(l);
        var p = l.Lines[0].Pronunciation!;
        Assert.Equal("hitozato hanare te naru oto wa", p.Text);
        Assert.Equal(PronunciationOrigin.Lyricod, p.Origin);
        Assert.Equal(p.Text, string.Concat(p.Words!.Select(w => w.Text)));
        Assert.Equal([new LyricWord(1, 2, "hitozato"), new LyricWord(3, 2, " hanare"), new LyricWord(5, 1, " te"), new LyricWord(6, 2, " naru"),
            new LyricWord(8, 1, " oto"), new LyricWord(9, 1, " wa")], p.Words);
        // It's a line of its own to light, over the line's own time.
        var sung = p.Sung(l.Lines[0])!;
        Assert.Equal((1, 10, p.Text), (sung.Start, sung.End, sung.Text));

        // The same with romaji by word off: Windows' words as it gives them.
        var plain = new Lyrics([ByCharacter("人里離れて鳴る音は")], "test");
        new Romanizer(reader).Fill(plain);
        Assert.Equal(["hitozato", " hanare", " te", " na", " ru", " oto", " wa"], plain.Lines[0].Pronunciation!.Words!.Select(w => w.Text));
        Assert.Equal(new LyricWord(7, 1, " ru"), plain.Lines[0].Pronunciation!.Words![4]);

        // A line the source didn't time by word has nothing to time its romaji with.
        var untimed = new Lyrics([new LyricLine { Start = 1, End = 10, Text = "人里離れて鳴る音は" }], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(untimed);
        Assert.Equal("hitozato hanare te naru oto wa", untimed.Lines[0].Pronunciation!.Text);
        Assert.Null(untimed.Lines[0].Pronunciation!.Words);
    }

    [Fact]
    public void ASourcesUntimedRomajiSpacedByWordIsSungWithOurs()
    {
        // NetEase's romaji has no timing; its line does. 明日 is read "asu" there, "ashita" here:
        // the source's letters, our words, the line's timing.
        var reader = new FakeReader(("明日", "あした"), ("から", "から"), ("、", null), ("向こう", "むこう"), ("まで", "まで"));
        var l = new Lyrics([ByCharacter("明日から、向こうまで", new("a su ka ra, mu ko u ma de"))], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(l);
        var p = l.Lines[0].Pronunciation!;
        Assert.Equal("asu kara, mukou made", p.Text);
        Assert.Equal(PronunciationOrigin.SourceByWord, p.Origin);
        // The comma goes with the word before it, and is lit with it.
        Assert.Equal([new LyricWord(1, 2, "asu"), new LyricWord(3, 2, " kara,"), new LyricWord(6, 3, " mukou"), new LyricWord(9, 2, " made")], p.Words);

        // Off, the source's romaji shows as it came, and plain: it isn't lined up with anything.
        var plain = new Lyrics([ByCharacter("明日から、向こうまで", new("a su ka ra, mu ko u ma de"))], "test");
        new Romanizer(reader).Fill(plain);
        Assert.Equal("a su ka ra, mu ko u ma de", plain.Lines[0].Pronunciation!.Text);
        Assert.Null(plain.Lines[0].Pronunciation!.Words);
    }

    [Fact]
    public void AWordTimedAsOneSharesItsTimeAmongItsCharacters()
    {
        // The source timed 明日から as one word (1 to 5 s) and まで as another (5 to 7 s): 明日 has
        // the first half of the first, から the second.
        var reader = new FakeReader(("明日", "あした"), ("から", "から"), (" ", null), ("まで", "まで"));
        var line = new LyricLine { Start = 1, End = 7, Text = "明日から まで", Words = [new(1, 4, "明日から "), new(5, 2, "まで")] };
        var l = new Lyrics([line], "test");
        new Romanizer(reader) { GroupJapaneseByWord = true }.Fill(l);
        Assert.Equal([new LyricWord(1, 2, "ashita"), new LyricWord(3, 2, " kara"), new LyricWord(5, 2, " made")], l.Lines[0].Pronunciation!.Words);
    }

    [Fact]
    public void MarksInTimedRomajiSitAgainstTheirWords()
    {
        var reader = new FakeReader(("何", "なに"), ("？", null), ("夢", "ゆめ"), ("だ", "だ"));
        var (line, _) = Timed("何？夢だ", ("na", 1), ("ni", 1.5), ("?", 2), ("yu", 3), ("me", 3.5), ("da", 4));
        Assert.Equal("nani? yume da", Filled(reader, line).Lines[0].Pronunciation!.Text);
    }

    [Fact]
    public void RomajiTooDifferentToLineUpIsLeftAlone() =>
        Assert.Null(Romanizer.Regroup("la la la la", "ashita kara mukou made"));

    [Fact]
    public void ByWordAKanjisKanaEndingStaysWithIt()
    {
        // Windows' analyser gives some endings apart: 泣|く, 鳴|る, 遠|く, 飛|ぶ.
        var crying = new FakeReader(("|貴方", "あなた"), ("も", "も"), ("|泣", "な"), ("く", "く"), ("ん", "ん"), ("だ", "だ"), ("ね", "ね"));
        Assert.Equal("anata mo naku n da ne", ByWord(crying, "貴方も泣くんだね"));
        var waves = new FakeReader(("|人里", "ひとざと"), ("|離れ", "はなれ"), ("て", "て"), ("|鳴", "な"), ("る", "る"), ("|音", "おと"), ("は", "は"),
            ("|向かい", "むかい"), ("波", "なみ"));
        Assert.Equal("hitozato hanare te naru oto wa mukai nami", ByWord(waves, "人里離れて鳴る音は向かい波"));
        var birds = new FakeReader(("|飛", "と"), ("ぶ", "ぶ"), ("|鳥", "とり"), ("は", "は"), ("|遠", "とお"), ("く", "く"), ("へ", "へ"));
        Assert.Equal("tobu tori wa tooku e", ByWord(birds, "飛ぶ鳥は遠くへ"));
        // A small っ ending still goes with what follows.
        var waiting = new FakeReader(("|待", "ま"), ("っ", "っ"), ("て", "て"));
        Assert.Equal("matte", ByWord(waiting, "待って"));
        // A new phrase isn't an ending.
        var phrase = new FakeReader(("|空", "そら"), ("|い", "い"));
        Assert.Equal("sora i", ByWord(phrase, "空い"));
        // Off (the default), Windows' words stay as it gives them.
        Assert.Equal("anata mo na ku n da ne", new Romanizer(crying).Romanize("貴方も泣くんだね", LyricLanguage.Japanese));
    }
}
