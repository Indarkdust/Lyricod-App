using System.Globalization;

namespace Lyricod.Core.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void SongAndLyricsDetailsStayEnglish()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var track = new Track { Title = "晴天", Artist = "周杰伦", Duration = 269, QqSongMid = "0039MnYb0qxYhV" };
            var lyrics = new Lyrics([new LyricLine { Text = "晴天", Words = [new LyricWord(0, 1, "晴天")] }], "QQ音乐")
            { Ref = new LyricsRef(LyricsSource.Qq, track.QqSongMid) };
            string text = Diagnostics.Text("0.4.5 (312)", "Windows 10.0.26100", "arm64", "QQ Music", "QQMusic.exe",
                true, track, lyrics, false, false, 0.3, -0.2);
            Assert.Equal("""
                Lyricod 0.4.5 (312) · Windows 10.0.26100 · arm64
                Player: QQ Music (QQMusic.exe) · playing
                Song: 晴天 — 周杰伦 · 4:29 · QQ 0039MnYb0qxYhV
                Lyrics: QQ Music · word by word · 1 lines · qq:0039MnYb0qxYhV · picked by hand: no
                Timing: this song +0.30 s · all songs −0.20 s
                """.Replace("\r\n", "\n"), text);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void OmitsUnavailableDetails()
    {
        string text = Diagnostics.Text("dev", "Windows", "x64", null, null, false, null, null, false, false, 0, 0);
        Assert.Equal("Lyricod dev · Windows · x64\nTiming: all songs +0.00 s", text);
    }

    [Fact]
    public void PlainLyricsAndErrors()
    {
        string text = Diagnostics.Text("dev", "Windows", "x64", "Player", "player.exe", false,
            new Track { Title = "Song", Artist = "Artist" }, new Lyrics([], "LRCLIB", false), false, true, -1.25, 0.5, "Player unavailable");
        Assert.Contains("Song: Song — Artist\n", text);
        Assert.Contains("Lyrics: LRCLIB · unsynced · 0 lines · picked by hand: yes", text);
        Assert.EndsWith("Last error: Player unavailable", text);
    }

    [Fact]
    public void SaysWhetherThePlayerGivesItsPosition()
    {
        string with = Diagnostics.Text("dev", "Windows", "x64", "NetEase", "cloudmusic.exe", true, null, null, false, false, 0, 0, playerTimeline: true);
        string without = Diagnostics.Text("dev", "Windows", "x64", "NetEase", "cloudmusic.exe", true, null, null, false, false, 0, 0, playerTimeline: false);
        Assert.Contains("Player: NetEase (cloudmusic.exe) · playing · position from the player\n", with);
        Assert.Contains("Player: NetEase (cloudmusic.exe) · playing · no position from the player\n", without);
    }

    [Fact]
    public void GivesTheCoverSize()
    {
        string text = Diagnostics.Text("dev", "Windows", "x64", "NetEase", "cloudmusic.exe", true,
            new Track { Title = "Song", Artist = "Artist" }, null, false, false, 0, 0, cover: (1200, 1200));
        Assert.Contains("Song: Song — Artist\nCover: 1200×1200 px\n", text);
    }

    [Fact]
    public void LeavesTheUserNameOutOfPaths()
    {
        string text = Diagnostics.Text("dev", "Windows", "x64", "Player", @"C:\Users\Ann\Apps\player.exe", true, null, null, false, false, 0, 0,
            @"lyrics: Could not find file 'c:\users\ann\AppData\Local\Lyricod\lyrics\x.json'.", @"C:\Users\Ann\");
        Assert.DoesNotContain("Ann", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"(%USERPROFILE%\Apps\player.exe)", text);
        Assert.Contains(@"'%USERPROFILE%\AppData\Local\Lyricod\lyrics\x.json'", text);
    }
}
