namespace Lyricod.Core.Tests;

public class MediaClockTests
{
    static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    static MediaClockReport Report(double position, double stamp, bool playing) =>
        new(position, Epoch.AddSeconds(stamp), playing, true);

    [Fact]
    public void ResumeWithThePauseTimeTimelineDoesNotCountThePause()
    {
        var playhead = new Playhead();
        var clock = new MediaClock(playhead);
        clock.Apply(Report(10, 0, true), true, Epoch, 100);
        clock.Apply(Report(12, 2, false), false, Epoch.AddSeconds(2), 102);
        Assert.Equal(12, playhead.RawPosition(112), 9);

        // Ten seconds later it plays again, still reporting the timeline it stamped when it paused.
        Assert.True(clock.Apply(Report(12, 2, true), false, Epoch.AddSeconds(12), 112));
        Assert.True(playhead.IsPlaying);
        Assert.Equal(12, playhead.RawPosition(112), 9);
        clock.Apply(Report(14, 13, true), false, Epoch.AddSeconds(13), 113);
        Assert.Equal(14, playhead.RawPosition(113), 9);
    }

    [Theory]
    [InlineData(5)]       // stamped in the future
    [InlineData(-200000)] // more than a day old, or never set
    public void AStaleOrEmptyStampGivesThePositionAsOfNow(double stamp)
    {
        var playhead = new Playhead();
        var clock = new MediaClock(playhead);
        clock.Apply(Report(30, stamp, true), true, Epoch, 100);
        Assert.Equal(30, playhead.RawPosition(100), 9);
    }

    [Fact]
    public void ANewSongStartsFromItsReportEvenWithTheSameTimeline()
    {
        var playhead = new Playhead();
        var clock = new MediaClock(playhead);
        clock.Apply(Report(0, 0, true), true, Epoch, 100);
        clock.Apply(Report(0, 0, false), false, Epoch.AddSeconds(10), 110);
        Assert.Equal(10, playhead.RawPosition(110), 9);

        Assert.True(clock.Apply(Report(0, 0, false), true, Epoch.AddSeconds(11), 111));
        Assert.Equal(0, playhead.RawPosition(111), 9);
    }

    [Fact]
    public void ARepeatedTimelineKeepsALocalSeek()
    {
        var playhead = new Playhead();
        var clock = new MediaClock(playhead);
        clock.Apply(Report(10, 0, true), true, Epoch, 100);
        Assert.False(clock.Apply(Report(10, 0, true), false, Epoch.AddSeconds(2), 102));
        Assert.Equal(12, playhead.RawPosition(102), 9);

        playhead.Anchor(60, 102, true);
        Assert.False(clock.Apply(Report(10, 0, true), false, Epoch.AddSeconds(3), 103));
        Assert.Equal(61, playhead.RawPosition(103), 9);
    }

    [Fact]
    public void WithoutATimelineASongIsTimedFromItsStart()
    {
        var playhead = new Playhead();
        var clock = new MediaClock(playhead);
        var none = new MediaClockReport(0, default, true, false);
        Assert.True(clock.Apply(none, true, Epoch, 100));
        Assert.True(clock.Apply(none with { IsPlaying = false }, false, Epoch, 105));
        Assert.Equal(5, playhead.RawPosition(110), 9);
    }
}
