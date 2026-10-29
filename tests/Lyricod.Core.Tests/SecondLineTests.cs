namespace Lyricod.Core.Tests;

public class SecondLineTests
{
    [Theory]
    [InlineData(SecondLine.TranslationOrNext, true)]
    [InlineData(SecondLine.Translation, false)]
    [InlineData(SecondLine.PronunciationOrTranslation, false)]
    [InlineData(SecondLine.Pronunciation, false)]
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
}
