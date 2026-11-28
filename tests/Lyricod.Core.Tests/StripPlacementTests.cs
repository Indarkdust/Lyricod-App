namespace Lyricod.Core.Tests;

public class StripPlacementTests
{
    [Fact]
    public void TheStripGrowsUpwardButNotAboveItsScreen()
    {
        // With room above, the bottom edge stays put.
        Assert.Equal(540, StripPlacement.Top(600, 60, 0));
        Assert.Equal(660, StripPlacement.Top(600, -60, 0));
        // Parked near the top, it stops at the top and grows downward from there.
        Assert.Equal(0, StripPlacement.Top(6, 60, 0));
        Assert.Equal(0, StripPlacement.Top(0, 60, 0));
        Assert.Equal(1080, StripPlacement.Top(1100, 60, 1080)); // a screen below the main one
        // Already above the top (as 0.6.1 could leave it), it goes no higher.
        Assert.Equal(-54, StripPlacement.Top(-54, 30, 0));
        // At the top it shrinks from its top edge too, so growing and shrinking back leaves it where it was.
        Assert.Equal(0, StripPlacement.Top(0, -60, 0));
        Assert.Equal(1080, StripPlacement.Top(1080, -60, 1080));
        Assert.Equal(-54, StripPlacement.Top(-54, -30, 0));
        int y = 0;
        foreach (int step in new[] { 10, 10, 10, -10, -10, -10 }) y = StripPlacement.Top(y, step, 0); // a slider dragged up and back
        Assert.Equal(0, y);
    }

    // A 900 strip with a 24 handle, 6 past the line's end.
    static double Handle(double end, double from, double to) => StripPlacement.HandleLeft(end, 24, 6, 900, from, to);

    [Fact]
    public void TheHandleSitsPastTheLinesEndOnAStripThatIsAllOnScreen()
    {
        Assert.Equal(656, Handle(650, -510, 1410));
        // A line as wide as the strip takes: the handle stays on the strip, over the line's end.
        Assert.Equal(876, Handle(876, -510, 1410));
        Assert.Equal(876, Handle(900, 0, 900));
    }

    [Fact]
    public void TheHandleStaysOnScreenWhenTheStripHangsPastItsEdge()
    {
        // The screen ends 700 into the strip: room after the line, then none, then a line that runs off it.
        Assert.Equal(656, Handle(650, -1220, 700));
        Assert.Equal(676, Handle(690, -1220, 700));
        Assert.Equal(676, Handle(760, -1220, 700));
        // The screen starts 600 into the strip, past the main line's end: a wider row under it still shows.
        Assert.Equal(600, Handle(520, 600, 2520));
        Assert.Equal(656, Handle(650, 600, 2520));
    }
}
