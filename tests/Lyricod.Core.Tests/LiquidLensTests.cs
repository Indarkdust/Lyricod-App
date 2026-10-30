namespace Lyricod.Core.Tests;

public class LiquidLensTests
{
    const int Width = 300, Height = 60;

    static (int Across, int Down) At(byte[] map, int x, int y) =>
        (map[(y * Width + x) * 4 + 2] - 128, map[(y * Width + x) * 4 + 1] - 128);

    [Fact]
    public void TheMiddleIsLeftAlone()
    {
        var map = LiquidLens.Map(Width, Height, 12, squareTop: false);
        Assert.Equal((0, 0), At(map, 150, 30));
        Assert.Equal(Width * Height * 4, map.Length);
        Assert.All(Enumerable.Range(0, Width * Height), i => Assert.Equal(255, map[i * 4 + 3]));
    }

    [Fact]
    public void EdgesBendInward()
    {
        var map = LiquidLens.Map(Width, Height, 12, squareTop: false);
        Assert.True(At(map, 1, 30).Across > 60);    // left edge: look further right
        Assert.True(At(map, 298, 30).Across < -60); // right edge: look further left
        Assert.True(At(map, 150, 1).Down > 60);     // top: look further down
        Assert.True(At(map, 150, 58).Down < -60);   // bottom: look further up
        Assert.Equal(0, At(map, 1, 30).Down);
        Assert.Equal(0, At(map, 150, 1).Across);
    }

    [Fact]
    public void CornersBendTowardTheMiddle()
    {
        var map = LiquidLens.Map(Width, Height, 12, squareTop: false);
        // Just inside the rounded corner: in and down.
        var (across, down) = At(map, 5, 5);
        Assert.True(across > 30 && down > 30);
        // Cut off by the rounding: outside the glass, left alone.
        Assert.Equal((0, 0), At(map, 1, 1));
    }

    [Fact]
    public void BendingFadesAwayFromTheEdge()
    {
        var map = LiquidLens.Map(Width, Height, 12, squareTop: false);
        int edge = At(map, 1, 30).Across, near = At(map, 6, 30).Across, inside = At(map, 25, 30).Across;
        Assert.True(edge > near && near > inside);
        Assert.Equal(0, inside);
    }

    [Fact]
    public void ASquareTopIsLeftStraight()
    {
        var map = LiquidLens.Map(Width, Height, 12, squareTop: true);
        Assert.Equal(0, At(map, 150, 1).Down);
        Assert.True(At(map, 150, 58).Down < -60);
        Assert.True(At(map, 1, 5).Across > 60);
    }

    static byte Lit(byte[] light, int x, int y) => light[(y * Width + x) * 4 + 3];

    [Fact]
    public void LightCatchesTheEdgeNotTheMiddle()
    {
        var light = LiquidLens.Light(Width, Height, 12, squareTop: false, rim: 1.5);
        Assert.Equal(Width * Height * 4, light.Length);
        Assert.Equal(0, Lit(light, 150, 30));
        Assert.True(Lit(light, 150, 0) > 100);
        Assert.Equal(0, Lit(light, 0, 0)); // cut off by the rounding
        // White, premultiplied.
        Assert.All(Enumerable.Range(0, Width * Height), i =>
            Assert.True(light[i * 4] == light[i * 4 + 3] && light[i * 4 + 1] == light[i * 4 + 3] && light[i * 4 + 2] == light[i * 4 + 3]));
    }

    [Fact]
    public void TheTopFacesTheLightAndTheFarSideGlints()
    {
        var light = LiquidLens.Light(Width, Height, 12, squareTop: false, rim: 1.5);
        Assert.True(Lit(light, 150, 0) > Lit(light, 150, 59));
        Assert.True(Lit(light, 150, 59) > Lit(light, 299, 30));
    }

    [Fact]
    public void TheLineAlongTheEdgeIsThin()
    {
        var light = LiquidLens.Light(Width, Height, 12, squareTop: false, rim: 1.5);
        // A few pixels in, only the faint glow is left.
        Assert.True(Lit(light, 150, 4) < Lit(light, 150, 0) / 4);
    }

    [Fact]
    public void ASquareTopCatchesNoLightAlongTheScreensEdge()
    {
        var light = LiquidLens.Light(Width, Height, 12, squareTop: true, rim: 1.5);
        Assert.Equal(0, Lit(light, 150, 0));
        Assert.True(Lit(light, 150, 59) > 50);
    }

    [Fact]
    public void TinyOrOddSizesWork()
    {
        Assert.Equal(4, LiquidLens.Map(1, 1, 12, squareTop: false).Length);
        Assert.Equal(7 * 3 * 4, LiquidLens.Map(7, 3, 40, squareTop: true).Length);
        Assert.Equal(4, LiquidLens.Light(1, 1, 12, squareTop: false, rim: 1.5).Length);
        Assert.Equal(7 * 3 * 4, LiquidLens.Light(7, 3, 40, squareTop: true, rim: 0).Length);
    }
}
