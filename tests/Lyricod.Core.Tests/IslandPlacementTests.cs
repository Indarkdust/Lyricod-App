namespace Lyricod.Core.Tests;

public class IslandPlacementTests
{
    static readonly ScreenRect Screen = new(0, 0, 1920, 1040);
    const double MaxWidth = 560, MaxHeight = 150;

    static (IslandAnchorX, IslandAnchorY) Drop(double left, double top, double width = 300, double height = 34)
    {
        var (x, y) = IslandPlacement.Spot(new(left, top, width, height), Screen, MaxWidth, MaxHeight);
        return IslandPlacement.Anchors(x, y, Screen, MaxWidth, MaxHeight);
    }

    [Fact]
    public void GrowsFromItsCentreWhenThereIsRoom() =>
        Assert.Equal((IslandAnchorX.Centre, IslandAnchorY.Top), Drop(810, 16));

    [Fact]
    public void GrowsAwayFromTheEdgeItIsNear()
    {
        Assert.Equal((IslandAnchorX.Left, IslandAnchorY.Top), Drop(10, 16));
        Assert.Equal((IslandAnchorX.Right, IslandAnchorY.Top), Drop(1600, 16));
        Assert.Equal((IslandAnchorX.Centre, IslandAnchorY.Bottom), Drop(810, 990));
        Assert.Equal((IslandAnchorX.Left, IslandAnchorY.Bottom), Drop(10, 990));
    }

    [Fact]
    public void ReloadingPutsThePillWhereItWasDropped()
    {
        // Within the pixel the saved spot rounds to: a misread edge would move it half its width.
        var random = new Random(7);
        for (int i = 0; i < 20000; i++)
        {
            double width = 120 + random.NextDouble() * (MaxWidth - 120), height = 34 + random.NextDouble() * 26;
            var pill = new ScreenRect(random.NextDouble() * (Screen.Width - width), random.NextDouble() * (Screen.Height - height), width, height);
            var (x, y) = IslandPlacement.Spot(pill, Screen, MaxWidth, MaxHeight);
            var anchors = IslandPlacement.Anchors(x, y, Screen, MaxWidth, MaxHeight);
            var (left, top) = IslandPlacement.Origin(x, y, anchors, width, height);
            Assert.InRange(left - pill.Left, -1, 1);
            Assert.InRange(top - pill.Top, -1, 1);
        }
    }

    [Fact]
    public void TheWidestAndTallestLayoutStaysOnScreen()
    {
        var random = new Random(11);
        for (int i = 0; i < 5000; i++)
        {
            // Anywhere, even partly off the screen (dragged past an edge).
            var pill = new ScreenRect(random.Next(-200, 1900), random.Next(-30, 1030), 300, 34);
            var (x, y) = IslandPlacement.Spot(pill, Screen, MaxWidth, MaxHeight);
            var anchors = IslandPlacement.Anchors(x, y, Screen, MaxWidth, MaxHeight);
            var (left, top) = IslandPlacement.Origin(x, y, anchors, MaxWidth, MaxHeight);
            Assert.InRange(left, Screen.Left, Screen.Right - MaxWidth);
            Assert.InRange(top, Screen.Top, Screen.Bottom - MaxHeight);
        }
    }

    [Theory]
    [InlineData(0, 0, 1620, 100, 1920, 100)]
    [InlineData(0, 0, 100, 1006, 100, 1040)]
    [InlineData(0, 0, 1620, 1006, 1920, 1040)]
    [InlineData(-1920, -1040, -300, -34, 0, 0)]
    public void FlushEdgesKeepTheirDisplayAndPlacement(int displayX, int displayY, int left, int top, int edgeX, int edgeY)
    {
        var display = new ScreenRect(displayX, displayY, 1920, 1040);
        var pill = new ScreenRect(left, top, 300, 34);
        var spot = IslandPlacement.Spot(pill, display, MaxWidth, MaxHeight);
        Assert.Equal((edgeX, edgeY), spot);
        var lookup = IslandPlacement.DisplayPoint(pill, display);
        // Adjacent displays start at the right or bottom edge.
        Assert.InRange(lookup.X, display.Left, display.Right - 1);
        Assert.InRange(lookup.Y, display.Top, display.Bottom - 1);
        var anchors = IslandPlacement.Anchors(spot.X, spot.Y, display, MaxWidth, MaxHeight);
        Assert.Equal((pill.Left, pill.Top), IslandPlacement.Origin(spot.X, spot.Y, anchors, pill.Width, pill.Height));
    }

    [Fact]
    public void DisplayLookupStaysInsideEvenWhenDraggedPastAnEdge() =>
        Assert.Equal((1919, 1039), IslandPlacement.DisplayPoint(new(1900, 1030, 300, 34), Screen));

    [Fact]
    public void WindowOriginKeepsTheAnchoredEdgeInPlace()
    {
        Assert.Equal((100.0, 50.0), IslandPlacement.Origin(100, 50, (IslandAnchorX.Left, IslandAnchorY.Top), 600, 160));
        Assert.Equal((-500.0, 50.0), IslandPlacement.Origin(100, 50, (IslandAnchorX.Right, IslandAnchorY.Top), 600, 160));
        Assert.Equal((-200.0, -110.0), IslandPlacement.Origin(100, 50, (IslandAnchorX.Centre, IslandAnchorY.Bottom), 600, 160));
    }

    [Theory]
    [InlineData(950, 960)]   // coming from the left
    [InlineData(968.5, 960)] // coming from the right
    [InlineData(960, 960)]
    [InlineData(948, 960)]   // at the edge of the range it still sits on the centre
    [InlineData(972, 960)]
    [InlineData(947.9, 947.9)] // past it, the island is where the pointer has it
    [InlineData(972.1, 972.1)]
    [InlineData(300, 300)]
    public void ADraggedIslandSitsOnItsScreensCentreLineWhileNearIt(double centre, double expected) =>
        Assert.Equal(expected, IslandPlacement.SnapCentre(centre, Screen, IslandPlacement.SnapRange));

    [Fact]
    public void TheCentreLineIsThatOfTheScreenTheIslandIsOn()
    {
        // A second screen to the right of the main one, and one to its left.
        var right = new ScreenRect(1920, 0, 2560, 1400);
        Assert.Equal(3200, IslandPlacement.SnapCentre(3190, right, 12));
        Assert.Equal(3200, IslandPlacement.SnapCentre(3212, right, 12));
        Assert.Equal(3213, IslandPlacement.SnapCentre(3213, right, 12));
        var left = new ScreenRect(-1280, 0, 1280, 984);
        Assert.Equal(-640, IslandPlacement.SnapCentre(-630, left, 12));
        // The main screen's centre line means nothing to an island on another screen.
        Assert.Equal(960, IslandPlacement.SnapCentre(960, right, 12));
        // An odd width's centre line falls between two pixels; the island's own centre can too.
        Assert.Equal(683.5, IslandPlacement.SnapCentre(690, new ScreenRect(0, 0, 1367, 768), 12));
    }

    [Fact]
    public void TheRangeGrowsWithTheDisplayScale()
    {
        // 12 view pixels are 18 at 150 %.
        Assert.Equal(960, IslandPlacement.SnapCentre(945, Screen, IslandPlacement.SnapRange * 1.5));
        Assert.Equal(945, IslandPlacement.SnapCentre(945, Screen, IslandPlacement.SnapRange));
    }

    [Theory]
    // The pill's centre is half a pixel from its window's edge, so half a pixel off the line, either side.
    [InlineData(0, 1366, 301, 150.5, 683)]
    [InlineData(0, 1366, 300, 347.5, 683)]
    // An odd width's line is between two pixels: neither is the line, and either is saved as it.
    [InlineData(0, 1367, 301, 278.5, 684)]
    [InlineData(0, 1367, 300, 278, 684)]
    [InlineData(-1367, 1367, 300, 278, -684)]
    public void AnIslandDroppedOnTheCentreLineComesBackWhereAResetOneSits(double areaLeft, double areaWidth, double pillWidth, double centreInWindow, int saved)
    {
        var area = new ScreenRect(areaLeft, 0, areaWidth, 728);
        double line = IslandPlacement.CentreLine(area);
        // Carried onto the line, the window sits on a whole pixel.
        double centre = Math.Round(line - centreInWindow) + centreInWindow;
        var (x, _) = IslandPlacement.Spot(new(centre - pillWidth / 2, 16, pillWidth, 34), area, MaxWidth, MaxHeight);
        Assert.Equal(saved, x);
        // Placed by the line itself, as a reset island is.
        Assert.Equal(line, IslandPlacement.Across(x, area));
    }

    [Fact]
    public void OnlyAPointOnTheCentreLineIsReadAsTheLine()
    {
        var even = new ScreenRect(0, 0, 1366, 728);
        Assert.Equal(682, IslandPlacement.Across(682, even));
        Assert.Equal(684, IslandPlacement.Across(684, even));
        var odd = new ScreenRect(0, 0, 1367, 728);
        Assert.Equal(683.5, IslandPlacement.Across(683, odd));
        Assert.Equal(682, IslandPlacement.Across(682, odd));
        Assert.Equal(685, IslandPlacement.Across(685, odd));
        // Dropped past the snap range, the pill's own centre is saved.
        Assert.Equal(696, IslandPlacement.Spot(new(545.6, 16, 300, 34), even, MaxWidth, MaxHeight).X);
    }

    [Fact]
    public void TheHandleSitsPastThePillsEndWhereThereIsRoom()
    {
        // A 300 pill in the middle of 536 of room: a 24 handle, 6 past its end.
        Assert.Equal(424, IslandPlacement.HandleLeft(118, 418, 24, 6, 0, 536, 5));
        // Just enough room after it, to the pixel.
        Assert.Equal(512, IslandPlacement.HandleLeft(206, 506, 24, 6, 0, 536, 5));
    }

    [Fact]
    public void TheHandleGoesBeforeThePillWhenThereIsNoRoomAfterIt()
    {
        // Held by its right edge, the pill ends where the room does.
        Assert.Equal(206, IslandPlacement.HandleLeft(236, 536, 24, 6, 0, 536, 5));
        Assert.Equal(1, IslandPlacement.HandleLeft(31, 507, 24, 6, 1, 536, 5));
    }

    [Fact]
    public void TheHandleSitsOverThePillsEndWhenThereIsNoRoomEitherSide()
    {
        // A long line: the pill nearly fills the room.
        Assert.Equal(489, IslandPlacement.HandleLeft(18, 518, 24, 6, 0, 536, 5));
        // A window cut to the pill's shape has no room outside the pill at all.
        Assert.Equal(389, IslandPlacement.HandleLeft(118, 418, 24, 6, 118, 418, 5));
    }
}
