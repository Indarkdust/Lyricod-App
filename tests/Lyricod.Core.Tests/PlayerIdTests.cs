namespace Lyricod.Core.Tests;

public class PlayerIdTests
{
    static readonly ProgramFolders Folders = new(@"C:\Program Files", @"C:\Program Files (x86)", @"C:\Windows\System32", @"C:\Windows\SysWOW64", @"C:\Windows");

    [Theory]
    [InlineData("cloudmusic.exe", "cloudmusic.exe")]
    [InlineData("QQMusic.EXE", "QQMusic.EXE")]
    [InlineData(@"C:\Program Files\MusicBee\MusicBee.exe", "MusicBee.exe")]
    [InlineData("MSEdge", null)]
    [InlineData("Tencent.QQMusic", null)]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", null)]
    [InlineData("", null)]
    public void AnIdThatIsAFileNameSaysWhichProgram(string id, string? name) => Assert.Equal(name, PlayerId.ExeName(id));

    [Theory]
    // As Windows 11 lists NetEase Cloud Music, QQ Music and its own programs.
    [InlineData(@"C:\Program Files\NetEase\CloudMusic\cloudmusic.exe", @"{6D809377-6AF0-444B-8957-A3773F02200E}\NetEase\CloudMusic\cloudmusic.exe")]
    [InlineData(@"C:\Program Files (x86)\Tencent\QQMusic\QQMusic.exe", @"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\Tencent\QQMusic\QQMusic.exe")]
    [InlineData(@"c:\program files\foobar2000\foobar2000.exe", @"{6D809377-6AF0-444B-8957-A3773F02200E}\foobar2000\foobar2000.exe")]
    [InlineData(@"C:\Windows\SysWOW64\player.exe", @"{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}\player.exe")]
    [InlineData(@"C:\Windows\explorer.exe", @"{F38BF404-1D43-42F2-9305-67DE0B28FC23}\explorer.exe")]
    public void AProgramInAKnownFolderIsListedUnderThatFoldersId(string path, string id)
    {
        var ids = PlayerId.AppsFolderIds(path, Folders);
        Assert.Equal(id, ids[0]);
        Assert.Equal(path, ids[^1]);
    }

    [Fact]
    public void TheNearestKnownFolderComesFirstAndThePathItselfLast()
    {
        Assert.Equal([@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\wmplayer.exe", @"{F38BF404-1D43-42F2-9305-67DE0B28FC23}\System32\wmplayer.exe", @"C:\Windows\System32\wmplayer.exe"],
            PlayerId.AppsFolderIds(@"C:\Windows\System32\wmplayer.exe", Folders));
        // Installed for one user, or on another drive: only the path.
        Assert.Equal([@"C:\Users\me\AppData\Roaming\Spotify\Spotify.exe"], PlayerId.AppsFolderIds(@"C:\Users\me\AppData\Roaming\Spotify\Spotify.exe", Folders));
        Assert.Equal([@"D:\Program Files\Player\player.exe"], PlayerId.AppsFolderIds(@"D:\Program Files\Player\player.exe", Folders));
        // "Program Files" isn't the start of "Program Files (x86)", and a folder is not a program inside itself.
        Assert.Single(PlayerId.AppsFolderIds(@"C:\Program Files (x86)", Folders));
    }

    static bool Running(string id, string path, string[]? sessions = null, string[]? processes = null, string[]? packaged = null) =>
        PlayerId.IsRunning(id, path, sessions ?? [], processes ?? [], packaged ?? []);

    [Fact]
    public void APlayerWithASessionIsRunning()
    {
        Assert.True(Running("Tencent.QQMusic", "", sessions: ["MSEdge", "Tencent.QQMusic"]));
        Assert.False(Running("Tencent.QQMusic", "", sessions: ["MSEdge"], processes: ["QQMusic.exe"]));
    }

    [Fact]
    public void AProcessByTheIdsOrTheRecordedPathsFileNameIsThePlayer()
    {
        string[] processes = ["explorer.exe", "CloudMusic.exe", "msedge.exe"];
        Assert.True(Running("cloudmusic.exe", "", processes: processes));
        Assert.True(Running("MSEdge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", processes: processes));
        Assert.False(Running("MSEdge", "", processes: processes));
        Assert.False(Running("QQMusic.exe", @"C:\Program Files (x86)\Tencent\QQMusic\QQMusic.exe", processes: processes));
        // Only the whole file name counts.
        Assert.False(Running("music.exe", "", processes: processes));
    }

    [Fact]
    public void APackagedAppIsFoundByItsIdWhateverTheCase()
    {
        const string id = "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic";
        Assert.True(Running(id, "", processes: ["Microsoft.Media.Player.exe"], packaged: ["microsoft.zunemusic_8wekyb3d8bbwe!MICROSOFT.ZUNEMUSIC"]));
        Assert.False(Running(id, "", processes: ["Microsoft.Media.Player.exe"], packaged: ["Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo"]));
    }

    [Fact]
    public void PackagedAppsAreOnlyLookedUpForAPackagedId()
    {
        // Reading them opens every process.
        var unread = Enumerable.Range(0, 1).Select<int, string>(_ => throw new InvalidOperationException());
        Assert.False(PlayerId.IsRunning("cloudmusic.exe", "", [], ["explorer.exe"], unread));
        Assert.False(PlayerId.IsRunning("MSEdge", "", [], ["explorer.exe"], unread));
        Assert.Throws<InvalidOperationException>(() => PlayerId.IsRunning("Family_1!App", "", [], ["explorer.exe"], unread));
    }

    static readonly string[] BrowserIds = ["MSEdge", "Chrome"];
    static readonly string[] BrowserCommands =
        [@"""C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe""", @"C:\Program Files\Mozilla Firefox\firefox.exe", @"""C:\Program Files\Some Browser\browse.exe"" --from-menu"];

    [Theory]
    [InlineData("MSEdge", true)]
    [InlineData("chrome", true)]
    [InlineData("msedge.exe", true)]
    [InlineData("Firefox.exe", true)]
    [InlineData("browse.exe", true)]
    // A site installed as an app is that app, not the browser.
    [InlineData("MSEdge._crx_abc", false)]
    [InlineData("cloudmusic.exe", false)]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", false)]
    public void AWebBrowserIsToldByItsIdOrItsProgram(string id, bool browser) =>
        Assert.Equal(browser, PlayerId.IsBrowser(id, BrowserIds, BrowserCommands));
}
