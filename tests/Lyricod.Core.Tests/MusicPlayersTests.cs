namespace Lyricod.Core.Tests;

public class MusicPlayersTests
{
    [Theory]
    [InlineData("cloudmusic.exe")]
    [InlineData("QQMusic.exe")]
    [InlineData("SPOTIFY.EXE")]
    [InlineData(@"C:\Program Files\MusicBee\MusicBee.exe")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")]
    [InlineData("microsoft.zunemusic_8wekyb3d8bbwe!Microsoft.ZuneMusic")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")]
    [InlineData("wmplayer.exe")]
    [InlineData("KuGou.exe")]
    [InlineData("Kuwo.exe")]
    [InlineData("KwMusic.exe")]
    [InlineData("foobar2000.exe")]
    [InlineData("AIMP.exe")]
    [InlineData("TIDAL.exe")]
    [InlineData("Amazon Music.exe")]
    [InlineData("Deezer.exe")]
    [InlineData("lx-music-desktop.exe")]
    [InlineData("LX Music.exe")]
    [InlineData("YesPlayMusic.exe")]
    [InlineData("Cider.exe")]
    public void RecognizesMusicPlayers(string appId) => Assert.True(MusicPlayers.IsMusicPlayer(appId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MSEdge")]
    [InlineData("Chrome")]
    [InlineData("308046B0AF4A39CB")]
    [InlineData("firefox.exe")]
    [InlineData("vlc.exe")]
    [InlineData("mpv.exe")]
    [InlineData("Microsoft.ZuneVideo_8wekyb3d8bbwe!App")]
    [InlineData("Unknown.exe")]
    [InlineData("NotSpotify.exe")]
    [InlineData("Spotify.exe.backup")]
    [InlineData("AppleInc.AppleMusicWinFake_123!App")]
    [InlineData(@"C:\Spotify.exe\video.exe")]
    public void EverythingElseNeedsEvidence(string? appId) => Assert.False(MusicPlayers.IsMusicPlayer(appId));
}

public class LyricsSearchTests
{
    [Theory]
    [InlineData("Hello", "Singer", 0, 120, 6, true)]
    [InlineData("Hello", "", 120, 120.5, 5.5, true)]
    [InlineData("Hello", "", 120, 122, 4.5, false)]
    [InlineData("Hello", "", 0, 120, 3, false)]
    [InlineData("Singer - Hello (Official Video)", "Singer", 120, 120, 8.5, true)]
    [InlineData("Singer - Hello (Official Video)", "Singer", 0, 120, 6, true)]
    [InlineData("Minecraft but every minute the world changes", "Gamer", 600, 120, -10, false)]
    [InlineData("Hello", "My daily vlog", 600, 120, -4, false)]
    [InlineData("Hello", "Singer", 2400, 120, 2, false)]
    [InlineData("Unrelated title", "Singer", 120, 120, 5.5, false)]
    public void CalibratesAgainstTheMatcher(string title, string artist, double playingLength, double length, double score, bool accepted)
    {
        var search = new LyricsSearch(false);
        var track = search.Prepare(new Track { Title = title, Artist = artist, Duration = playingLength });
        var actual = TrackMatcher.Score(["Hello"], ["Singer"], length, track);
        Assert.Equal(score, actual);
        Assert.Equal(accepted, search.Accepts(actual, TrackMatcher.TitleMatches(["Hello"], track)));
    }

    [Fact]
    public void PrefixNeedsTheArtistsNameAndDoesNotChangeMusicPlayers()
    {
        var song = new Track { Title = "Singer - Hello (Official Video)", Artist = "Singer" };
        Assert.Same(song, new LyricsSearch(true).Prepare(song));
        Assert.Equal("Hello (Official Video)", new LyricsSearch(false).Prepare(song).Title);
        var vlog = song with { Artist = "Someone else" };
        Assert.Same(vlog, new LyricsSearch(false).Prepare(vlog));
    }

    [Fact]
    public void VersionPenaltiesStillApply()
    {
        var song = new Track { Title = "Hello", Artist = "Singer", Duration = 120 };
        var score = TrackMatcher.Score(["Hello (instrumental)"], ["Singer"], 120, song);
        Assert.True(score < LyricsSearch.ConfidentScore);
        Assert.False(new LyricsSearch(false).Accepts(score, true));
    }
}
