namespace Lyricod.Core.Tests;

public class IslandEntryTests
{
    [Theory]
    [InlineData(150, 180, 150, 139, true)]
    [InlineData(50, 120, 101, 120, false)]
    [InlineData(250, 120, 199, 120, false)]
    [InlineData(150, 50, 150, 101, false)]
    // Fast movement can land nearer another edge: the crossed edge wins.
    [InlineData(150, 300, 102, 105, true)]
    [InlineData(0, 120, 150, 139, false)]
    // Diagonal approaches from below a corner can cross either edge.
    [InlineData(50, 180, 150, 139, true)]
    [InlineData(50, 180, 101, 110, false)]
    [InlineData(250, 180, 150, 139, true)]
    [InlineData(250, 180, 199, 110, false)]
    [InlineData(90, 150, 110, 130, false)] // exact corner tie
    public void UsesTheCrossedEdge(double outsideX, double outsideY, double x, double y, bool below)
    {
        Assert.Equal(below, IslandEntry.IsFromBelow(x, y, 100, 100, 100, 40, (outsideX, outsideY)));
    }

    [Theory]
    [InlineData(150, 139, true)]
    [InlineData(101, 120, false)]
    [InlineData(199, 120, false)]
    [InlineData(150, 101, false)]
    [InlineData(104, 139, true)]
    [InlineData(101, 136, false)]
    [InlineData(101, 139, false)]
    [InlineData(150, 140, true)]
    [InlineData(100, 140, false)]
    public void MissingOrNowInsideSampleUsesNearestEdge(double x, double y, bool below)
    {
        Assert.Equal(below, IslandEntry.IsFromBelow(x, y, 100, 100, 100, 40));
        Assert.Equal(below, IslandEntry.IsFromBelow(x, y, 100, 100, 100, 40, (150, 120)));
    }

    [Fact]
    public void WorksWithScreenOffsetsAndScaling()
    {
        Assert.True(IslandEntry.IsFromBelow(-500, -241, -600, -300, 200, 60, (-500, -200)));
        Assert.False(IslandEntry.IsFromBelow(-599, -270, -600, -300, 200, 60, (-700, -270)));
        Assert.False(IslandEntry.IsFromBelow(-500, -299, -600, -300, 200, 60, (-500, -400)));
    }
}
