namespace Lyricod.Core.Tests;

public class SessionPickTests
{
    sealed record S(string Id, bool Playing, bool Timeline);

    static S? Pick(IReadOnlyList<S> sessions, S? current, string choice = "auto") =>
        SessionPick.Choose(sessions, current, choice, s => s.Id, s => s.Playing, s => s.Timeline);

    [Fact]
    public void NetEaseTwiceFollowsTheSessionWithAPosition()
    {
        // NetEase on Windows with its progress option: Windows' current session has no position.
        var bare = new S("cloudmusic.exe", true, false);
        var timed = new S("cloudmusic.exe", true, true);
        var edge = new S("MSEdge", false, true);
        Assert.Same(timed, Pick([timed, bare, edge], bare));
        Assert.Same(timed, Pick([bare, timed, edge], bare));
        Assert.Same(timed, Pick([bare, timed], null));
        Assert.Same(timed, Pick([bare, timed, edge], edge, "cloudmusic.exe"));
    }

    [Fact]
    public void OtherAppsAndPausedLeftoversDontTakeOver()
    {
        var bare = new S("cloudmusic.exe", true, false);
        // Another app with a position doesn't replace the current one.
        Assert.Same(bare, Pick([bare, new S("Spotify.exe", true, true)], bare));
        // A paused session of the same app doesn't replace a playing one.
        Assert.Same(bare, Pick([bare, new S("cloudmusic.exe", false, true)], bare));
        // When nothing plays, the same app's session with a position is still the better one.
        var pausedBare = new S("cloudmusic.exe", false, false);
        var pausedTimed = new S("cloudmusic.exe", false, true);
        Assert.Same(pausedTimed, Pick([pausedBare, pausedTimed], pausedBare));
    }

    [Fact]
    public void UsualChoicesAreUnchanged()
    {
        var qq = new S("QQMusic.exe", false, true);
        var edge = new S("MSEdge", true, true);
        Assert.Same(edge, Pick([qq, edge], qq));            // one that's playing over the paused current one
        Assert.Same(qq, Pick([qq, edge], qq, "QQMusic.exe")); // the chosen player, playing or not
        Assert.Same(qq, Pick([qq], qq));
        Assert.Null(Pick([], null));
    }
}
