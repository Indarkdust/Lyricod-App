using Lyricod.Core.Romanization;

namespace Lyricod.Core.Tests;

/// Pronunciation lines. Korean and Japanese expectations are the macOS app's; pinyin is
/// spaced by syllable here (the Mac joins words, using macOS's word tokenizer).
public class RomanizerTests
{
    /// A stand-in for Windows' JapanesePhoneticAnalyzer: fixed words and readings. A surface starting
    /// with "|" starts a phrase.
    sealed class FakeReader(params (string Surface, string? Reading)[] words) : IJapaneseReader
    {
        public IReadOnlyList<WordReading> Read(string text) =>
            words.Select(w => new WordReading(w.Surface.TrimStart('|'), w.Reading, w.Surface.StartsWith('|'))).ToList();
    }

    [Fact]
    public void PinyinReadsPhrases()
    {
        var r = new Romanizer();
        // Characters with two readings take the one their phrase uses.
        Assert.Equal("chóng xīn lái guò", r.Romanize("重新来过", LyricLanguage.Chinese));
        Assert.Equal("wǒ liǎo jiě nǐ", r.Romanize("我了解你", LyricLanguage.Chinese));
        Assert.Equal("yín háng", r.Romanize("银行", LyricLanguage.Chinese));
        // 还 as "still" is hái; as "give back" it stays huán.
        Assert.Equal("hái yào duō jiǔ cái néng zhǎng dà", r.Romanize("還要多久才能長大", LyricLanguage.Chinese));
        Assert.Equal("huán gěi wǒ", r.Romanize("还给我", LyricLanguage.Chinese));
        // Punctuation and English words.
        Assert.Equal("wǒ men de ài, guò le jiù bù zài huí lái", r.Romanize("我們的愛，過了就不再回來", LyricLanguage.Chinese));
        Assert.Equal("Baby wǒ zài děng nǐ say goodbye", r.Romanize("Baby 我在等你 say goodbye", LyricLanguage.Chinese));
    }

    [Fact]
    public void KanaToRomaji()
    {
        Assert.Equal("toukyou", KanaRomaji.Convert("とうきょう"));
        Assert.Equal("koohii", KanaRomaji.Convert("コーヒー"));
        Assert.Equal("matte", KanaRomaji.Convert("まって"));
        Assert.Equal("chotto", KanaRomaji.Convert("ちょっと"));
        Assert.Equal("issho", KanaRomaji.Convert("いっしょ"));
        Assert.Equal("kotchi", KanaRomaji.Convert("こっち")); // Hepburn, like the Mac app
        Assert.Equal("shashin", KanaRomaji.Convert("しゃしん"));
        Assert.Equal("fan", KanaRomaji.Convert("ファン"));
        Assert.Equal("a", KanaRomaji.Convert("あっ"));
        Assert.Equal("Baby", KanaRomaji.Convert("Baby"));
    }

    [Fact]
    public void JapaneseFromReadings()
    {
        // 夢ならばどれほどよかったでしょう, split the way the analyser does.
        var reader = new FakeReader(("夢", "ゆめ"), ("なら", "なら"), ("ば", "ば"), ("どれ", "どれ"), ("ほど", "ほど"),
            ("よかっ", "よかっ"), ("た", "た"), ("でしょう", "でしょう"));
        Assert.Equal("yume nara ba dore hodo yokatta deshou", new Romanizer(reader).Romanize("夢ならばどれほどよかったでしょう", LyricLanguage.Japanese));

        var particles = new FakeReader(("今日", "きょう"), ("は", "は"), ("君", "きみ"), ("に", "に"), ("会い", "あい"), ("たい", "たい"));
        Assert.Equal("kyou wa kimi ni ai tai", new Romanizer(particles).Romanize("今日は君に会いたい", LyricLanguage.Japanese));

        var mixed = new FakeReader(("待っ", "まっ"), ("て", "て"), ("、", null), ("Baby", "Baby"), (" ", null), ("I", "I"));
        Assert.Equal("matte, Baby I", new Romanizer(mixed).Romanize("待って、Baby I", LyricLanguage.Japanese));
    }

    [Fact]
    public void JapaneseNeedsAReaderWithoutSourceRomaji() =>
        Assert.Null(new Romanizer().Romanize("夢ならば", LyricLanguage.Japanese));

    [Fact]
    public void KoreanFollowsHowItIsSaid()
    {
        var r = new Romanizer();
        Assert.Equal("saranghaeyo geudaereul", r.Romanize("사랑해요 그대를", LyricLanguage.Korean));
        Assert.Equal("neoui ireumeun", r.Romanize("너의 이름은", LyricLanguage.Korean));
        Assert.Equal("bogo sipda", r.Romanize("보고 싶다", LyricLanguage.Korean));
        Assert.Equal("gamsahamnida", r.Romanize("감사합니다", LyricLanguage.Korean));
        Assert.Equal("gachi isseo", r.Romanize("같이 있어", LyricLanguage.Korean));
        Assert.Equal("joa jota mana", r.Romanize("좋아 좋다 많아", LyricLanguage.Korean));
        Assert.Equal("eopseo ilgeo bakke", r.Romanize("없어 읽어 밖에", LyricLanguage.Korean));
        Assert.Equal("silla seollal dongnip", r.Romanize("신라 설날 독립", LyricLanguage.Korean));
        Assert.Equal("ireoke malhanikka", r.Romanize("이렇게 말하니까", LyricLanguage.Korean));
    }

    [Fact]
    public void LatinLinesGetNoPronunciation()
    {
        var r = new Romanizer();
        Assert.Null(r.Romanize("I love you", LyricLanguage.Chinese));
        Assert.Null(r.Romanize("Yeah, yeah", LyricLanguage.Korean));
    }

    [Fact]
    public void LanguageComesFromTheScript()
    {
        static Lyrics L(params string[] texts) => new(texts.Select((t, i) => new LyricLine { Id = i, Text = t }).ToList(), "test");
        Assert.Equal(LyricLanguage.Japanese, Romanizer.SongLanguage(L("東京の空", "永遠に君を", "永遠", "I miss you")));
        // Kanji alone in a Japanese song is read as Japanese, not Chinese.
        Assert.Equal(LyricLanguage.Japanese, Romanizer.LineLanguage("永遠", LyricLanguage.Japanese));
        Assert.Equal(LyricLanguage.Chinese, Romanizer.LineLanguage("永遠", LyricLanguage.Chinese));
        Assert.Null(Romanizer.LineLanguage("I miss you", LyricLanguage.Japanese));
        Assert.Equal(LyricLanguage.Korean, Romanizer.SongLanguage(L("사랑해요", "baby")));
        Assert.Equal(LyricLanguage.Chinese, Romanizer.SongLanguage(L("从前从前", "有个人爱你很久")));
    }

    [Fact]
    public void SourceRomajiIsKeptForJapaneseOnly()
    {
        var l = new Lyrics([
            new LyricLine { Id = 0, Text = "夢ならば", Pronunciation = new("yu me na ra ba") },
            new LyricLine { Id = 1, Text = "東京の空" },
        ], "test");
        new Romanizer(new FakeReader(("東京", "とうきょう"), ("の", "の"), ("空", "そら"))).Fill(l);
        Assert.Equal(new Pronunciation("yu me na ra ba", LyricLanguage.Japanese), l.Lines[0].Pronunciation);
        Assert.Equal(new Pronunciation("toukyou no sora", LyricLanguage.Japanese, PronunciationOrigin.Lyricod), l.Lines[1].Pronunciation);

        var k = new Lyrics([new LyricLine { Text = "사랑해요", Pronunciation = new("sa lang hae yo") }], "test");
        new Romanizer().Fill(k);
        Assert.Equal("saranghaeyo", k.Lines[0].Pronunciation?.Text);
    }

    [Fact]
    public void SourceRomajiReadingTheSameIsGroupedByWord()
    {
        // QQ Music's romaji, a syllable at a time, reads the same as Windows' words: shown by word.
        var reader = new FakeReader(("惹かれ", "ひかれ"), ("合う", "あう"), ("の", "の"), ("は", "は"), (" ", null), ("なぜ", "なぜ"));
        var l = new Lyrics([new LyricLine { Text = "惹かれ合うのは なぜ", Pronunciation = new("hi ka re a u no wa na ze") }], "test");
        new Romanizer(reader).Fill(l);
        Assert.Equal(new Pronunciation("hikare au no wa naze", LyricLanguage.Japanese, PronunciationOrigin.SourceByWord), l.Lines[0].Pronunciation);

        // A reading that differs here and there keeps the source's letters, spaced by our words.
        var other = new Lyrics([new LyricLine { Text = "惹かれ合うのは なぜ", Pronunciation = new("hi ka re a u no wa na n de") }], "test");
        new Romanizer(reader).Fill(other);
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

    [Fact]
    public void RomajiTooDifferentToLineUpIsLeftAlone() =>
        Assert.Null(Romanizer.Regroup("la la la la", "ashita kara mukou made"));

    [Fact]
    public void AKanjisKanaEndingStaysWithIt()
    {
        // Windows' analyser gives some endings apart: 泣|く, 鳴|る, 遠|く, 飛|ぶ.
        var crying = new FakeReader(("|貴方", "あなた"), ("も", "も"), ("|泣", "な"), ("く", "く"), ("ん", "ん"), ("だ", "だ"), ("ね", "ね"));
        Assert.Equal("anata mo naku n da ne", new Romanizer(crying).Romanize("貴方も泣くんだね", LyricLanguage.Japanese));
        var waves = new FakeReader(("|人里", "ひとざと"), ("|離れ", "はなれ"), ("て", "て"), ("|鳴", "な"), ("る", "る"), ("|音", "おと"), ("は", "は"),
            ("|向かい", "むかい"), ("波", "なみ"));
        Assert.Equal("hitozato hanare te naru oto wa mukai nami", new Romanizer(waves).Romanize("人里離れて鳴る音は向かい波", LyricLanguage.Japanese));
        var birds = new FakeReader(("|飛", "と"), ("ぶ", "ぶ"), ("|鳥", "とり"), ("は", "は"), ("|遠", "とお"), ("く", "く"), ("へ", "へ"));
        Assert.Equal("tobu tori wa tooku e", new Romanizer(birds).Romanize("飛ぶ鳥は遠くへ", LyricLanguage.Japanese));
        // A small っ ending still goes with what follows.
        var waiting = new FakeReader(("|待", "ま"), ("っ", "っ"), ("て", "て"));
        Assert.Equal("matte", new Romanizer(waiting).Romanize("待って", LyricLanguage.Japanese));
        // A new phrase isn't an ending.
        var phrase = new FakeReader(("|空", "そら"), ("|い", "い"));
        Assert.Equal("sora i", new Romanizer(phrase).Romanize("空い", LyricLanguage.Japanese));
    }
}
