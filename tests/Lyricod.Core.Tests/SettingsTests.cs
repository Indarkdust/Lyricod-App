using System.Text.Json;

namespace Lyricod.Core.Tests;

public class SettingsTests
{
    [Fact]
    public void AValueThisVersionCantReadKeepsItsDefaultAndTheRestIsRead()
    {
        // A test build's or a newer version's choice mustn't make the whole file unreadable.
        var settings = Lyricod.Settings.Read("""
            {"LyricsBackground":"Glass","IslandHover":"Peek","FrameRate":"Turbo","IslandSecondLine":"Lyrics","IslandSize":7.5,
             "PronunciationLanguages":["Korean","Klingon"],"DesktopFontSize":40,"LyricsEffect":"Glow","GlassShineStyle":"Soft"}
            """)!;
        Assert.Equal(Lyricod.LyricsBackground.Artwork, settings.LyricsBackground);
        Assert.Equal(Lyricod.IslandHover.ByDirection, settings.IslandHover);
        Assert.Equal(Lyricod.FrameRate.Balanced, settings.FrameRate);
        Assert.Equal(SecondLine.None, settings.IslandSecondLine);
        Assert.Equal(Lyricod.IslandSize.Medium, settings.IslandSize);
        Assert.Equal([LyricLanguage.Japanese, LyricLanguage.Korean], settings.PronunciationLanguages);
        Assert.Equal(40, settings.DesktopFontSize);
        Assert.Equal(Lyricod.SungEffect.Glow, settings.LyricsEffect);
        Assert.Equal(ShineStyle.Soft, settings.GlassShineStyle);

        // A file that isn't JSON at all is still refused, and Load starts afresh.
        Assert.ThrowsAny<JsonException>(() => Lyricod.Settings.Read("""{"DesktopFontSize":"""));
        Assert.Null(Lyricod.Settings.Read("[]"));
    }

    [Fact]
    public void OldSettingsKeepLookupEnabled()
    {
        Assert.True(JsonSerializer.Deserialize<Lyricod.Settings>("{}")!.LookUpOtherPlayers);
    }

    [Fact]
    public void LookupChoiceRoundTripsAndNotifies()
    {
        var settings = new Lyricod.Settings();
        var properties = new List<string?>();
        settings.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        settings.LookUpOtherPlayers = false;
        Assert.False(JsonSerializer.Deserialize<Lyricod.Settings>(JsonSerializer.Serialize(settings))!.LookUpOtherPlayers);
        Assert.Equal([nameof(Lyricod.Settings.LookUpOtherPlayers)], properties);
    }
}
