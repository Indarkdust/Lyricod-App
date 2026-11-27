namespace Lyricod.Core.Tests;

public class LiquidLensTests
{
    const int Width = 300, Height = 60;

    static byte[] Map(bool squareTop = false) => LiquidLens.Map(Width, Height, 12, squareTop);
    static byte[] Light(bool squareTop = false, ShineStyle style = ShineStyle.Soft) => LiquidLens.Light(Width, Height, 12, squareTop, rim: 1.5, style);

    static (int Across, int Down) At(byte[] map, int x, int y) =>
        (map[(y * Width + x) * 4 + 2] - 128, map[(y * Width + x) * 4 + 1] - 128);

    [Fact]
    public void TheMiddleIsLeftAlone()
    {
        var map = Map();
        Assert.Equal((0, 0), At(map, 150, 30));
        Assert.Equal(Width * Height * 4, map.Length);
        Assert.All(Enumerable.Range(0, Width * Height), i => Assert.Equal(255, map[i * 4 + 3]));
    }

    [Fact]
    public void EdgesBendInward()
    {
        var map = Map();
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
        var map = Map();
        // Just inside the rounded corner: in and down.
        var (across, down) = At(map, 5, 5);
        Assert.True(across > 30 && down > 30);
        // Cut off by the rounding: outside the glass, left alone.
        Assert.Equal((0, 0), At(map, 1, 1));
    }

    [Fact]
    public void BendingFadesAwayFromTheEdge()
    {
        var map = Map();
        int edge = At(map, 1, 30).Across, near = At(map, 6, 30).Across, inside = At(map, 25, 30).Across;
        Assert.True(edge > near && near > inside);
        Assert.Equal(0, inside);
    }

    [Fact]
    public void ASquareTopIsLeftStraight()
    {
        var map = Map(squareTop: true);
        Assert.Equal(0, At(map, 150, 1).Down);
        Assert.True(At(map, 150, 58).Down < -60);
        Assert.True(At(map, 1, 5).Across > 60);
    }

    static byte Lit(byte[] light, int x, int y) => light[(y * Width + x) * 4];

    /// <summary>How much black is under the white: the alpha the light alone doesn't account for.</summary>
    static int Shade(byte[] light, int x, int y) => light[(y * Width + x) * 4 + 3] - Lit(light, x, y);

    [Theory]
    [InlineData(ShineStyle.Bright)]
    [InlineData(ShineStyle.Soft)]
    public void LightCatchesTheEdgeNotTheMiddle(ShineStyle style)
    {
        var light = Light(style: style);
        Assert.Equal(Width * Height * 4, light.Length);
        Assert.Equal(0, Lit(light, 150, 30));
        Assert.True(Lit(light, 150, 0) > 30);
        Assert.Equal(0, Lit(light, 0, 0)); // cut off by the rounding
        // White over black, premultiplied.
        Assert.All(Enumerable.Range(0, Width * Height), i =>
            Assert.True(light[i * 4] <= light[i * 4 + 3] && light[i * 4 + 1] == light[i * 4] && light[i * 4 + 2] == light[i * 4]));
    }

    [Fact]
    public void TheSideAwayFromTheLightIsShaded()
    {
        var light = Light();
        // Just inside the line along the edge.
        Assert.True(Shade(light, 150, 56) > 8);
        // None on the side that faces it, or the island gets a dark ring.
        Assert.Equal(0, Shade(light, 150, 3));
        Assert.True(Shade(light, 296, 30) > Shade(light, 3, 30));
        Assert.Equal(0, Shade(light, 150, 30));
        // The line itself stays bright.
        Assert.True(Shade(light, 150, 59) < Shade(light, 150, 56));
    }

    [Fact]
    public void BrightHasNoShadeAndABrighterEdge()
    {
        var bright = Light(style: ShineStyle.Bright);
        var soft = Light();
        Assert.All(Enumerable.Range(0, Width * Height), i => Assert.Equal(bright[i * 4], bright[i * 4 + 3]));
        Assert.True(Lit(bright, 150, 0) > 2 * Lit(soft, 150, 0));
        Assert.True(Lit(bright, 150, 59) > Lit(soft, 150, 59));
    }

    [Theory]
    [InlineData(ShineStyle.Bright)]
    [InlineData(ShineStyle.Soft)]
    public void TheTopFacesTheLightAndTheFarSideGlints(ShineStyle style)
    {
        var light = Light(style: style);
        Assert.True(Lit(light, 150, 0) > Lit(light, 150, 59));
        Assert.True(Lit(light, 150, 59) > Lit(light, 299, 30));
    }

    [Theory]
    [InlineData(ShineStyle.Bright)]
    [InlineData(ShineStyle.Soft)]
    public void TheLineAlongTheEdgeIsThin(ShineStyle style)
    {
        var light = Light(style: style);
        // A few pixels in, only the faint glow is left.
        Assert.True(Lit(light, 150, 4) < Lit(light, 150, 0) / 4);
    }

    [Theory]
    [InlineData(ShineStyle.Bright)]
    [InlineData(ShineStyle.Soft)]
    public void ASquareTopCatchesNoLightAlongTheScreensEdge(ShineStyle style)
    {
        var light = Light(squareTop: true, style);
        Assert.Equal(0, Lit(light, 150, 0));
        Assert.True(Lit(light, 150, 59) > 20);
    }

    [Fact]
    public void TinyOrOddSizesWork()
    {
        Assert.Equal(4, LiquidLens.Map(1, 1, 12, squareTop: false).Length);
        Assert.Equal(7 * 3 * 4, LiquidLens.Map(7, 3, 40, squareTop: true).Length);
        Assert.Equal(4, LiquidLens.Light(1, 1, 12, squareTop: false, rim: 1.5, ShineStyle.Soft).Length);
        Assert.Equal(7 * 3 * 4, LiquidLens.Light(7, 3, 40, squareTop: true, rim: 0, ShineStyle.Bright).Length);
    }

    [Theory]
    [InlineData(300, 60, 12)]
    [InlineData(1121, 102, 24)]   // the island at 200%
    [InlineData(1380, 408, 44)]   // its mini player
    [InlineData(559, 51, 25.5)]   // a floating island: round ends
    [InlineData(40, 90, 10)]
    [InlineData(31, 30, 40)]
    [InlineData(7, 3, 40)]
    [InlineData(2, 1, 0)]
    public void CopyingAlongStraightEdgesChangesNoPixel(int width, int height, double radius)
    {
        foreach (bool squareTop in new[] { false, true })
        {
            int count = width * height * 4;
            byte[] depths = new byte[count], ways = new byte[count], wantDepths = new byte[count], wantWays = new byte[count];
            LiquidLens.Measure(depths, width, height, radius, squareTop, (i, depth, _, _) => BitConverter.TryWriteBytes(depths.AsSpan(i), (float)depth));
            LiquidLens.Measure(ways, width, height, radius, squareTop, (i, _, dx, dy) => Way(ways, i, dx, dy));
            // Every pixel worked out by itself.
            double r = Math.Clamp(radius, 0, Math.Min(width, height) / 2.0), top = squareTop ? -height : 0;
            double centreX = width / 2.0, centreY = (top + height) / 2.0, halfX = width / 2.0, halfY = (height - top) / 2.0;
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double px = x + 0.5 - centreX, py = y + 0.5 - centreY;
                double qx = Math.Abs(px) - (halfX - r), qy = Math.Abs(py) - (halfY - r);
                double outX = Math.Max(qx, 0), outY = Math.Max(qy, 0), corner = Math.Sqrt(outX * outX + outY * outY);
                double depth = r - corner - Math.Min(Math.Max(qx, qy), 0);
                (double dx, double dy) = corner > 0 ? (outX / corner * Math.Sign(px), outY / corner * Math.Sign(py))
                    : qx > qy ? (Math.Sign(px), 0.0) : (0.0, Math.Sign(py));
                BitConverter.TryWriteBytes(wantDepths.AsSpan((y * width + x) * 4), (float)depth);
                Way(wantWays, (y * width + x) * 4, dx, dy);
            }
            Assert.Equal(wantDepths, depths);
            Assert.Equal(wantWays, ways);
        }
    }

    static void Way(byte[] pixels, int i, double dx, double dy) =>
        (pixels[i], pixels[i + 1]) = ((byte)(sbyte)Math.Round(127 * dx), (byte)(sbyte)Math.Round(127 * dy));
}
