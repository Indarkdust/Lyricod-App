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
}
