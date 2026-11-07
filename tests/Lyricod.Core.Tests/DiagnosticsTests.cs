using System.Globalization;

namespace Lyricod.Core.Tests;

public class DiagnosticsTests
{
    [Theory]
    [InlineData(false, "blocked")]
    public void ReportsAutomaticLookupPermission(bool allowed, string label)
    {
        string text = Diagnostics.Text("dev", "Windows", "x64", null, null, false, null, null, false, false, 0, 0,
            automaticLookupAllowed: allowed);
        Assert.Contains($"Automatic lyric lookup: {label}\n", text);
    }

    [Fact]
    public void ReportsRecognitionAndAnInvariantScore()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string text = Diagnostics.Text("dev", "Windows", "x64", null, null, false, null, null, false, false, 0, 0,
                musicPlayer: false, treatedAsSong: false, bestMatchScore: 4.5);
            Assert.Contains("\n\nMusic player: no\n\nTreated as a song: no\n\nBest match score: 4.5\nTiming:", text);
            string pinned = Diagnostics.Text("dev", "Windows", "x64", null, null, false, null, null, false, true, 0, 0,
                musicPlayer: false, treatedAsSong: true);
            Assert.Contains("Treated as a song: yes\n\nBest match score: unverified\n", pinned);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

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
                Lyricod 0.4.5 (312) on Windows 10.0.26100, arm64

                Player: QQ Music (QQMusic.exe), playing

                Song: 晴天 — 周杰伦 (4:29, QQ 0039MnYb0qxYhV)

                Lyrics: QQ Music, word by word, 1 line (qq:0039MnYb0qxYhV)
                Picked by hand: no
                Timing: this song +0.30 s, all songs −0.20 s
                """.Replace("\r\n", "\n"), text);
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void GroupsLyricodThePlayerTheSongAndItsLyrics()
    {
        var track = new Track { Title = "啄木鳥", Artist = "ヨルシカ", Duration = 216 };
        var lyrics = new Lyrics([new LyricLine { Text = "a", Words = [new LyricWord(0, 1, "a")] }], "QQ音乐")
        { Ref = new LyricsRef(LyricsSource.Qq, "000E1FVU0aq6GB") };
        string text = Diagnostics.Text("0.5.7 (0)", "Windows 10.0.26300.0", "x64", "NetEase Cloud Music", "cloudmusic.exe", true,
            track, lyrics, false, false, 0, 0, "Player unavailable", playerTimeline: true, cover: (800, 800),
            font: "MiSans (downloaded, hash ok)", installKind: InstallKind.Portable, musicPlayer: true, treatedAsSong: true,
            bestMatchScore: 8.5, automaticLookupAllowed: true, coverBytes: 38_912, coverWhole: true, glass: "global exclusion fallback");
        Assert.Equal("""
            Lyricod 0.5.7 (0) on Windows 10.0.26300.0, x64
            Install: portable
            Font: MiSans (downloaded, hash ok)
            Glass capture: global exclusion fallback

            Player: NetEase Cloud Music (cloudmusic.exe), playing, position from the player
            Music player: yes
            Automatic lyric lookup: allowed

            Song: 啄木鳥 — ヨルシカ (3:36)
            Treated as a song: yes
            Cover: 800×800 px, 38 KB, whole

            Lyrics: QQ Music, word by word, 1 line (qq:000E1FVU0aq6GB)
            Picked by hand: no
            Best match score: 8.5
            Timing: this song +0.00 s, all songs +0.00 s

            Last error: Player unavailable
            """.Replace("\r\n", "\n"), text);
    }

    [Fact]
    public void CountsTranslationsAndWherePronunciationsCameFrom()
    {
        var lyrics = new Lyrics([
            new LyricLine { Text = "a", Translation = "甲", Pronunciation = new("a", LyricLanguage.Japanese) },
            new LyricLine { Text = "b", Translation = "乙", Pronunciation = new("b", LyricLanguage.Japanese, PronunciationOrigin.SourceByWord) },
            new LyricLine { Text = "c", Pronunciation = new("c", LyricLanguage.Japanese, PronunciationOrigin.SourceByWord) },
            new LyricLine { Text = "d", Pronunciation = new("d", LyricLanguage.Japanese, PronunciationOrigin.Lyricod) },
            new LyricLine { Text = "e" },
        ], "QQ Music");
        string text = Diagnostics.Text("dev", "Windows", "x64", null, null, false, new Track { Title = "t", Artist = "a" }, lyrics, false, false, 0, 0);
        Assert.Contains("\nTranslation: 2 of 5 lines\nPronunciation: 4 of 5 lines (source's as given 1, source's by word 2, Lyricod's 1)\n", text);
        // The player's platform's translation over another platform's words says where it's from.
        lyrics.Ref = new LyricsRef(LyricsSource.Qq, "mid");
        lyrics.TranslationRef = LyricsRef.Netease(7);
        text = Diagnostics.Text("dev", "Windows", "x64", null, null, false, new Track { Title = "t", Artist = "a" }, lyrics, false, false, 0, 0);
        Assert.Contains("\nTranslation: 2 of 5 lines, from NetEase (netease:7)\n", text);
    }

    [Fact]
    public void OmitsUnavailableDetails()
    {
        string text = Diagnostics.Text("dev", "Windows", "x64", null, null, false, null, null, false, false, 0, 0);
        Assert.Equal("Lyricod dev on Windows, x64\n\nTiming: all songs +0.00 s", text);
    }

    [Fact]
    public void PlainLyricsAndErrors()
    {
        string text = Diagnostics.Text("dev", "Windows", "x64", "Player", "player.exe", false,
            new Track { Title = "Song", Artist = "Artist" }, new Lyrics([], "LRCLIB", false), false, true, -1.25, 0.5, "Player unavailable");
        Assert.Contains("Song: Song — Artist\n", text);
        Assert.Contains("Lyrics: LRCLIB, unsynced, 0 lines\nPicked by hand: yes\n", text);
        Assert.EndsWith("Last error: Player unavailable", text);
    }

    [Fact]
    public void SaysWhetherThePlayerGivesItsPosition()
    {
        string with = Diagnostics.Text("dev", "Windows", "x64", "NetEase", "cloudmusic.exe", true, null, null, false, false, 0, 0, playerTimeline: true);
        string without = Diagnostics.Text("dev", "Windows", "x64", "NetEase", "cloudmusic.exe", true, null, null, false, false, 0, 0, playerTimeline: false);
        Assert.Contains("Player: NetEase (cloudmusic.exe), playing, position from the player\n", with);
        Assert.Contains("Player: NetEase (cloudmusic.exe), playing, no position from the player\n", without);
    }

    [Fact]
    public void GivesTheCoverSize()
    {
        string text = Diagnostics.Text("dev", "Windows", "x64", "NetEase", "cloudmusic.exe", true,
            new Track { Title = "Song", Artist = "Artist" }, null, false, false, 0, 0, cover: (1200, 1200));
        Assert.Contains("Song: Song — Artist\nCover: 1200×1200 px\n", text);
    }

    [Fact]
    public void SaysHowBigTheCoverIsAndWhetherItCameWhole()
    {
        string Cover(long bytes, bool whole) => Diagnostics.Text("dev", "Windows", "x64", "NetEase", "cloudmusic.exe", true,
            new Track { Title = "Song", Artist = "Artist" }, null, false, false, 0, 0, cover: (3000, 3000), coverBytes: bytes, coverWhole: whole);
        Assert.Contains("Cover: 3000×3000 px, 2.5 MB, whole\n", Cover(2_621_440, true));
        Assert.Contains("Cover: 3000×3000 px, 60 KB, cut short\n", Cover(61_440, false));
        Assert.Contains("Cover: 3000×3000 px, 1 KB, whole\n", Cover(10, true));
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
