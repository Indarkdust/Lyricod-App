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
