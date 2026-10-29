using Lyricod.Core.Romanization;

namespace Lyricod.Core.Tests;

/// Pronunciation lines. Korean and Japanese expectations are the macOS app's; pinyin is
/// spaced by syllable here (the Mac joins words, using macOS's word tokenizer).
public class RomanizerTests
{
    /// A stand-in for Windows' JapanesePhoneticAnalyzer: fixed words and readings.
    sealed class FakeReader(params (string Surface, string? Reading)[] words) : IJapaneseReader
    {
        public IReadOnlyList<WordReading> Read(string text) => words.Select(w => new WordReading(w.Surface, w.Reading)).ToList();
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
        Assert.Equal(new Pronunciation("toukyou no sora", LyricLanguage.Japanese), l.Lines[1].Pronunciation);

        var k = new Lyrics([new LyricLine { Text = "사랑해요", Pronunciation = new("sa lang hae yo") }], "test");
        new Romanizer().Fill(k);
        Assert.Equal("saranghaeyo", k.Lines[0].Pronunciation?.Text);
    }
}
