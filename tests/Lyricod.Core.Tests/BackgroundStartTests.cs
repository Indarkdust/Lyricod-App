using System.Runtime.CompilerServices;

namespace Lyricod.Core.Tests;

public class BackgroundStartTests
{
    [Theory]
    // Written before 0.6.0 (the launcher), since then (the app), and by hand.
    [InlineData("\"C:\\Users\\me\\AppData\\Local\\Programs\\Lyricod\\Lyricod.exe\"", "\"C:\\Users\\me\\AppData\\Local\\Programs\\Lyricod\\Lyricod.exe\" --background")]
    [InlineData("\"D:\\Lyricod\\app\\Lyricod.exe\"", "\"D:\\Lyricod\\app\\Lyricod.exe\" --background")]
    [InlineData("\"C:\\Program Files\\Lyricod\\app\\Lyricod.exe\"", "\"C:\\Program Files\\Lyricod\\app\\Lyricod.exe\" --background")]
    [InlineData("\"D:\\Lyricod\\app\\Lyricod.exe\"  ", "\"D:\\Lyricod\\app\\Lyricod.exe\" --background")]
    [InlineData("D:\\Lyricod\\Lyricod.exe --quiet", "D:\\Lyricod\\Lyricod.exe --quiet --background")]
    // Only the flag as a whole argument at the end counts.
    [InlineData("D:\\Lyricod\\Lyricod.exe --no-background", "D:\\Lyricod\\Lyricod.exe --no-background --background")]
    public void ARunEntryWithoutTheFlagGetsIt(string before, string after)
    {
        Assert.Equal(after, BackgroundStart.WithFlag(before));
        Assert.Null(BackgroundStart.WithFlag(after));
    }

    [Theory]
    [InlineData("\"D:\\Lyricod\\app\\Lyricod.exe\" --background")]
    [InlineData("\"D:\\Lyricod\\app\\Lyricod.exe\" --background ")]
    [InlineData("")]
    [InlineData(" ")]
    public void AnEntryWithTheFlagOrNoEntryIsLeftAlone(string command) => Assert.Null(BackgroundStart.WithFlag(command));

    [Fact]
    public void OnlyAStartWithoutTheFlagAndNotAfterAnUpdateIsTheUsers()
    {
        string[] plain = [@"D:\Lyricod\app\Lyricod.exe"], flagged = [@"D:\Lyricod\app\Lyricod.exe", "--background"];
        Assert.True(BackgroundStart.ByUser(plain, afterUpdate: false));
        Assert.False(BackgroundStart.ByUser(flagged, afterUpdate: false));
        Assert.False(BackgroundStart.ByUser(plain, afterUpdate: true));
        // A folder named like the flag isn't the flag.
        Assert.True(BackgroundStart.ByUser([@"D:\--background\Lyricod.exe"], afterUpdate: false));
    }

    [Fact]
    public void SetupReopensLyricodWithTheFlag()
    {
        // Setup's script can't share the constant.
        var relaunch = File.ReadLines(Installer()).Single(line => line.Contains("Check: RelaunchAfterUpdate"));
        Assert.Contains($"Parameters: \"{BackgroundStart.Flag}\"", relaunch);
    }

    static string Installer([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "../../installer/Lyricod.iss");
}
