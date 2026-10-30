namespace Lyricod.Core.Tests;

public class IslandGestureTests
{
    // The island: x 100–300, y 100–140.
    static bool Expands(IslandEdge edge, (double X, double Y) entry, params (double X, double Y)[] trail) =>
        IslandGesture.IsSidewaysThenIn(trail, entry, edge);

    [Fact]
    public void SidewaysThenDownThroughTheTopExpands() =>
        Assert.True(Expands(IslandEdge.Top, (160, 105), (50, 80), (100, 80), (150, 80), (160, 85), (160, 95)));

    [Fact]
    public void SidewaysThenUpThroughTheBottomExpands() =>
        Assert.True(Expands(IslandEdge.Bottom, (160, 138), (50, 160), (120, 160), (160, 158), (160, 150)));

    [Fact]
    public void ALongerDropAfterTheSidewaysLegStillCounts() =>
        Assert.True(Expands(IslandEdge.Top, (150, 105), (0, 0), (100, 0), (150, 0), (150, 30), (150, 60), (150, 95)));

    [Fact]
    public void OnlyTheLastTwoLegsMatter() =>
        Assert.True(Expands(IslandEdge.Bottom, (170, 138), (60, 250), (60, 170), (120, 170), (170, 168), (170, 150)));

    [Fact]
    public void StraightInHides() =>
        Assert.False(Expands(IslandEdge.Top, (160, 105), (160, 20), (160, 50), (160, 80), (160, 95)));

    [Fact]
    public void DiagonalHides() =>
        Assert.False(Expands(IslandEdge.Top, (145, 105), (60, 20), (90, 50), (120, 80), (135, 95)));

    [Fact]
    public void ThroughASideHides() =>
        Assert.False(Expands(IslandEdge.Side, (102, 120), (20, 120), (60, 120), (95, 120)));

    [Fact]
    public void ATooShortSidewaysLegHides() =>
        Assert.False(Expands(IslandEdge.Top, (160, 105), (140, 80), (160, 80), (160, 95)));

    [Fact]
    public void DistancesGrowWithTheDisplayScale()
    {
        (double, double)[] trail = [(100, 80), (160, 80), (160, 95)];
        Assert.True(IslandGesture.IsSidewaysThenIn(trail, (160, 105), IslandEdge.Top, scale: 1));
        Assert.False(IslandGesture.IsSidewaysThenIn(trail, (160, 105), IslandEdge.Top, scale: 2));
    }

    [Fact]
    public void NoTrailHides() =>
        Assert.False(IslandGesture.IsSidewaysThenIn([], (160, 105), IslandEdge.Top));
}
