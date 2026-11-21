namespace Lyricod.Core.Tests;

public class SecondLineTests
{
    [Theory]
    [InlineData(SecondLine.TranslationOrNext, true)]
    [InlineData(SecondLine.Translation, false)]
    [InlineData(SecondLine.PronunciationOrTranslation, false)]
    [InlineData(SecondLine.Pronunciation, false)]
    [InlineData(SecondLine.PronunciationAndTranslation, false)]
    [InlineData(SecondLine.Next, true)]
    [InlineData(SecondLine.None, false)]
    public void PlainSyncedSongOnlyHasTheNextLine(SecondLine mode, bool expected)
    {
        var lyrics = new Lyrics([new() { Text = "one" }, new() { Text = "two" }], "test");
        Assert.Equal(expected, mode.RowUnder(0, lyrics) != null);
        Assert.Null(mode.RowUnder(1, lyrics)); // nothing after the last line
    }

    [Theory]
    [InlineData(SecondLine.TranslationOrNext)]
    [InlineData(SecondLine.Translation)]
    [InlineData(SecondLine.PronunciationOrTranslation)]
    [InlineData(SecondLine.Pronunciation)]
    [InlineData(SecondLine.PronunciationAndTranslation)]
    [InlineData(SecondLine.Next)]
    [InlineData(SecondLine.None)]
    public void NoLineUnsyncedOrBlankHasNoRow(SecondLine mode)
    {
        var lines = new List<LyricLine> { new() { Text = "one", Translation = "译文", Pronunciation = new("one") }, new() { Text = "two" } };
        Assert.Null(mode.RowUnder(null, new Lyrics(lines, "test")));
        Assert.Null(mode.RowUnder(0, null));
        Assert.Null(mode.RowUnder(0, new Lyrics(lines, "test", false)));
        var blank = new List<LyricLine> { new() { Text = "one", Translation = " ", Pronunciation = new("\t") }, new() { Text = " " } };
        Assert.Null(mode.RowUnder(0, new Lyrics(blank, "test")));
    }

    [Fact]
    public void TheRowFollowsEachLine()
    {
        // A translated song with an untranslated line: that line gets no second row.
        var lyrics = new Lyrics([new() { Text = "one" }, new() { Text = "two", Translation = "译文" }], "test");
        Assert.Null(SecondLine.Translation.RowUnder(0, lyrics));
        Assert.Equal("译文", SecondLine.Translation.RowUnder(1, lyrics)?.Text);
        Assert.Equal("two", SecondLine.TranslationOrNext.RowUnder(0, lyrics)?.Text);
        Assert.Equal("译文", SecondLine.PronunciationOrTranslation.RowUnder(1, lyrics)?.Text);
        Assert.Null(SecondLine.Pronunciation.RowUnder(1, lyrics));
    }

    [Fact]
    public void PronunciationAndTranslationAreTwoRowsWhereThereAreBoth()
    {
        var lyrics = new Lyrics([
            new() { Text = "惹かれ合うのは", Pronunciation = new("hikare au no wa"), Translation = "为何彼此心生憧憬" },
            new() { Text = "なぜ", Pronunciation = new("naze") },
            new() { Text = "海", Translation = "海" },
            new() { Text = "x" },
        ], "test");
        var mode = SecondLine.PronunciationAndTranslation;
        Assert.Equal([("hikare au no wa", true), ("为何彼此心生憧憬", true)], mode.RowsUnder(0, lyrics));
        Assert.Equal([("naze", true)], mode.RowsUnder(1, lyrics));
        Assert.Equal([("海", true)], mode.RowsUnder(2, lyrics));
        Assert.Empty(mode.RowsUnder(3, lyrics));
        Assert.Equal(2, mode.MaxRows());
        // The island has room for one row: it shows the pronunciation, or the translation.
        Assert.Equal("hikare au no wa", mode.RowUnder(0, lyrics)?.Text);
        Assert.Equal([("two", false)], SecondLine.TranslationOrNext.RowsUnder(0, new Lyrics([new() { Text = "one" }, new() { Text = "two" }], "test")));
    }
}
