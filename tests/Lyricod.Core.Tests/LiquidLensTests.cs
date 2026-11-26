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

    /// <summary>The maps as first written, every pixel worked out by itself: what the quicker way has to match exactly.</summary>
    static (byte[] Map, byte[] Light) PixelByPixel(int width, int height, double radius, bool squareTop, double rim, ShineStyle style)
    {
        var (lineMost, glowMost, shadeMost) = style == ShineStyle.Soft ? (0.24, 0.03, 0.1) : (0.6, 0.1, 0.0);
        byte[] map = new byte[width * height * 4], light = new byte[width * height * 4];
        double band = Math.Max(2, Math.Min(Math.Min(width, height) / 2.0, Math.Max(radius * 1.5, 12)));
        rim = Math.Max(rim, 0.5);
        double lightX = -0.35 / Math.Sqrt(1 + 0.35 * 0.35), lightY = -1 / Math.Sqrt(1 + 0.35 * 0.35);
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2.0);
        double top = squareTop ? -height : 0;
        double centreX = width / 2.0, centreY = (top + height) / 2.0, halfX = width / 2.0, halfY = (height - top) / 2.0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double px = x + 0.5 - centreX, py = y + 0.5 - centreY;
            double qx = Math.Abs(px) - (halfX - radius), qy = Math.Abs(py) - (halfY - radius);
            double outX = Math.Max(qx, 0), outY = Math.Max(qy, 0), corner = Math.Sqrt(outX * outX + outY * outY);
            double depth = radius - corner - Math.Min(Math.Max(qx, qy), 0);
            (double dx, double dy) = corner > 0 ? (outX / corner * Math.Sign(px), outY / corner * Math.Sign(py))
                : qx > qy ? (Math.Sign(px), 0.0) : (0.0, Math.Sign(py));
            int i = (y * width + x) * 4;
            double strength = depth >= band || depth < 0 ? 0 : Math.Pow(1 - depth / band, 2);
            map[i + 2] = (byte)Math.Round(128 - 127 * dx * strength);
            map[i + 1] = (byte)Math.Round(128 - 127 * dy * strength);
            map[i + 3] = 255;
            if (depth < -1 || depth >= band) continue;
            double facing = dx * lightX + dy * lightY;
            double lit = 0.15 + 0.85 * Math.Max(Math.Pow(Math.Max(facing, 0), 2), 0.55 * Math.Pow(Math.Max(-facing, 0), 2));
            double t = Math.Clamp(1 - depth / rim, 0, 1);
            double line = lineMost * lit * t * t * (3 - 2 * t);
            double near = 1 - Math.Max(depth, 0) / band;
            double glow = glowMost * Math.Pow(near, 3) * (0.5 + 0.5 * lit);
            double white = 1 - (1 - line) * (1 - glow);
            double shade = shadeMost * Math.Max(-facing, 0) * near * near * (1 - t);
            light[i] = light[i + 1] = light[i + 2] = (byte)Math.Round(255 * white);
            light[i + 3] = (byte)Math.Round(255 * (1 - (1 - white) * (1 - shade)));
        }
        return (map, light);
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
        foreach (var style in new[] { ShineStyle.Bright, ShineStyle.Soft })
        foreach (double rim in new[] { 0, 1.5, 3 })
        {
            var (map, light) = PixelByPixel(width, height, radius, squareTop, rim, style);
            Assert.Equal(map, LiquidLens.Map(width, height, radius, squareTop));
            Assert.Equal(light, LiquidLens.Light(width, height, radius, squareTop, rim, style));
        }
    }
}
