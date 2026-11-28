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
    public void AFileFromWhenTheViewsHadLocksStillLoads()
    {
        // Their unlock switches are gone; what was saved beside them is read as before.
        var settings = Lyricod.Settings.Read("""
            {"ShowDesktopLyrics":true,"DesktopLocked":false,"DesktopX":120,"DesktopY":900,
             "IslandStyle":"Floating","IslandLocked":false,"IslandX":640,"IslandY":48,"IslandHover":"Nothing"}
            """)!;
        Assert.Equal(120, settings.DesktopX);
        Assert.Equal(900, settings.DesktopY);
        Assert.Equal(Lyricod.IslandStyle.Floating, settings.IslandStyle);
        Assert.Equal(640, settings.IslandX);
        Assert.Equal(48, settings.IslandY);
        Assert.Equal(Lyricod.IslandHover.Nothing, settings.IslandHover);
        Assert.DoesNotContain("Locked", JsonSerializer.Serialize(settings));
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

    [Fact]
    public void TheLastPlayerRoundTripsAndOpeningItAtStartIsOffUntilChosen()
    {
        var settings = new Lyricod.Settings();
        Assert.Equal("", settings.LastPlayerId);
        Assert.Equal("", settings.LastPlayerPath);
        Assert.False(settings.OpenPlayerAtStart);
        // A file without them reads as the same.
        Assert.False(JsonSerializer.Deserialize<Lyricod.Settings>("""{"PlayerChoice":"cloudmusic.exe"}""")!.OpenPlayerAtStart);
        Assert.Equal("", JsonSerializer.Deserialize<Lyricod.Settings>("{}")!.LastPlayerId);

        var properties = new List<string?>();
        settings.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        settings.LastPlayerPath = @"C:\Program Files\NetEase\CloudMusic\cloudmusic.exe";
        settings.LastPlayerId = "cloudmusic.exe";
        settings.LastPlayerId = "cloudmusic.exe";
        settings.OpenPlayerAtStart = true;
        Assert.Equal([nameof(Lyricod.Settings.LastPlayerPath), nameof(Lyricod.Settings.LastPlayerId), nameof(Lyricod.Settings.OpenPlayerAtStart)], properties);

        var saved = JsonSerializer.Deserialize<Lyricod.Settings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal("cloudmusic.exe", saved.LastPlayerId);
        Assert.Equal(@"C:\Program Files\NetEase\CloudMusic\cloudmusic.exe", saved.LastPlayerPath);
        Assert.True(saved.OpenPlayerAtStart);
    }
}
