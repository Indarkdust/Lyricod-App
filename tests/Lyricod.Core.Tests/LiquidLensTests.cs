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

    static byte[] LifelikeMap(bool squareTop = false) => LiquidLens.LifelikeMap(Width, Height, 12, squareTop, band: 20);
    static byte[] LifelikeLight(bool squareTop = false, double scale = 2) => LiquidLens.LifelikeLight(Width, Height, 12, squareTop, scale);

    /// <summary>How far Lifelike's map looks across and down at a pixel, as a part of its most.</summary>
    static (double Across, double Down) Bend(byte[] map, int x, int y) =>
        ((BitConverter.ToUInt16(map, (y * Width + x) * 8) - 32768) / 32767.0, (BitConverter.ToUInt16(map, (y * Width + x) * 8 + 2) - 32768) / 32767.0);

    static void Bends(byte[] map, int x, int y, double across, double down)
    {
        Assert.Equal(across, Bend(map, x, y).Across, 0.0005);
        Assert.Equal(down, Bend(map, x, y).Down, 0.0005);
    }

    static byte Glint(byte[] light, int x, int y) => light[(y * Width + x) * 4 + 2];
    static byte Dark(byte[] light, int x, int y) => light[(y * Width + x) * 4];

    [Fact]
    public void LifelikeBendsAsAQuarterCircleOverItsBand()
    {
        var map = LifelikeMap();
        Assert.Equal(Width * Height * 8, map.Length);
        // Nearly all the way at the edge, falling off fast: the rim shows what's further in, mirrored.
        foreach (var (inset, bend) in new[] { (0, 0.778), (1, 0.620), (6, 0.262), (12, 0.073) })
        {
            Bends(map, 150, inset, 0, bend);
            Bends(map, 150, Height - 1 - inset, 0, -bend);
            Bends(map, inset, 30, bend, 0);
            Bends(map, Width - 1 - inset, 30, -bend, 0);
        }
        Assert.Equal((0.0, 0.0), Bend(map, 150, 20)); // past the band
        Assert.Equal((0.0, 0.0), Bend(map, 150, 30));
        Assert.All(Enumerable.Range(0, Width * Height), i =>
            Assert.True(BitConverter.ToUInt16(map, i * 8 + 4) == 0 && BitConverter.ToUInt16(map, i * 8 + 6) == ushort.MaxValue));
    }

    [Fact]
    public void LifelikesBendTurnsCornersNoTighterThanTheBand()
    {
        var map = LifelikeMap();
        // Where the 12 pixel corner's nearest edge changes there's no crease: either side of its diagonal bends the same way.
        var (across, down) = Bend(map, 14, 13);
        Assert.InRange(across, 0.03, down);
        Assert.Equal((down, across), Bend(map, 13, 14));
        // It's at its most at the corner's own edge, and less just inside it, as along a straight edge.
        Bends(map, 1, 1, 0.707, 0.707);
        Bends(map, Width - 2, Height - 2, -0.707, -0.707);
        Bends(map, 4, 4, 0.448, 0.448);
    }

    [Fact]
    public void LifelikeLightsTheTopAndBottomAlikeAndNotTheSides()
    {
        var light = LifelikeLight();
        Assert.Equal(Width * Height * 4, light.Length);
        Assert.All(Enumerable.Range(0, Height / 2), y => Assert.Equal(Glint(light, 150, y), Glint(light, 150, Height - 1 - y)));
        Assert.Equal(255, Glint(light, 150, 0));
        Assert.Equal(0, Glint(light, 0, 30));
        Assert.Equal(0, Glint(light, Width - 1, 30));
        // Round the corner it fades.
        Assert.InRange(Glint(light, 4, 4), 1, Glint(light, 150, 1) / 2);
        // A pixel the corner only partly covers is lit as its outermost: the clip smooths it.
        Assert.True(Glint(light, 3, 3) > Glint(light, 4, 4));
        Assert.Equal(0, Glint(light, 0, 0)); // cut off by the rounding
        Assert.All(Enumerable.Range(0, Width * Height), i => Assert.True(light[i * 4 + 1] == 0 && light[i * 4 + 3] == 255));
    }

    [Fact]
    public void LifelikeLightIsAThinLineWithAFaintGlowInside()
    {
        // At 2 pixels per view pixel: the pixels 0.5, 1.5 and 3.5 deep.
        var light = LifelikeLight();
        Assert.Equal(1, Glint(light, 150, 0) / 255.0, 0.005);
        Assert.Equal(0.54, Glint(light, 150, 1) / 255.0, 0.005);
        Assert.Equal(0.14, Glint(light, 150, 3) / 255.0, 0.005);
        Assert.Equal(0, Glint(light, 150, 14));
        Assert.Equal(0, Glint(light, 150, 30));
        // The line keeps its width in view pixels.
        Assert.Equal(Glint(LifelikeLight(scale: 1), 150, 0), Glint(LifelikeLight(scale: 3), 150, 1));
    }

    [Fact]
    public void LifelikeDarkensOnlyTheOutermostPixelsOfTheSides()
    {
        var light = LifelikeLight();
        Assert.Equal(0.43, Dark(light, 0, 30) / 255.0, 0.01);
        Assert.Equal(Dark(light, 0, 30), Dark(light, Width - 1, 30));
        Assert.Equal(0, Dark(light, 1, 30));
        Assert.Equal(0, Dark(light, 150, 0));
        Assert.Equal(0, Dark(light, 150, Height - 1));
        Assert.Equal(0, Dark(light, 150, 30));
        // Three quarters of a view pixel wide.
        var larger = LifelikeLight(scale: 3);
        Assert.True(Dark(larger, 0, 30) > Dark(larger, 1, 30) && Dark(larger, 1, 30) > 0);
        Assert.Equal(0, Dark(larger, 2, 30));
    }

    [Fact]
    public void ASquareTopGetsNothingOfLifelikeAlongTheScreensEdge()
    {
        var light = LifelikeLight(squareTop: true);
        Assert.All(Enumerable.Range(0, Width), x => Assert.Equal(0, Glint(light, x, 0)));
        Assert.Equal(0, Dark(light, 150, 0));
        Assert.Equal(255, Glint(light, 150, Height - 1));
        // The sides' line runs up to the screen's edge.
        Assert.Equal(Dark(light, 0, 30), Dark(light, 0, 0));
        var map = LifelikeMap(squareTop: true);
        Assert.Equal(0, Bend(map, 150, 1).Down);
        Assert.True(Bend(map, 150, 58).Down < -0.5);
        Assert.True(Bend(map, 1, 0).Across > 0.5);
    }

    [Theory]
    [InlineData(560, 51, 18.36, 33.15)]  // a floating island
    [InlineData(690, 204, 20, 60)]       // its mini player
    [InlineData(560, 30, 10.8, 19.5)]
    [InlineData(30, 560, 10.8, 19.5)]
    public void LifelikesLensIsSizedByTheIslandsShorterSide(double width, double height, double band, double reach)
    {
        Assert.Equal(band, LiquidLens.LifelikeBand(width, height), 1e-9);
        Assert.Equal(reach, LiquidLens.LifelikeReach(width, height), 1e-9);
    }

    [Fact]
    public void TinyOrOddSizesWork()
    {
        Assert.Equal(4, LiquidLens.Map(1, 1, 12, squareTop: false).Length);
        Assert.Equal(7 * 3 * 4, LiquidLens.Map(7, 3, 40, squareTop: true).Length);
        Assert.Equal(4, LiquidLens.Light(1, 1, 12, squareTop: false, rim: 1.5, ShineStyle.Soft).Length);
        Assert.Equal(7 * 3 * 4, LiquidLens.Light(7, 3, 40, squareTop: true, rim: 0, ShineStyle.Bright).Length);
        Assert.Equal(8, LiquidLens.LifelikeMap(1, 1, 12, squareTop: false, band: 0.5).Length);
        Assert.Equal(7 * 3 * 8, LiquidLens.LifelikeMap(7, 3, 40, squareTop: true, band: 1.5).Length);
        Assert.Equal(4, LiquidLens.LifelikeLight(1, 1, 12, squareTop: false, scale: 1).Length);
        Assert.Equal(7 * 3 * 4, LiquidLens.LifelikeLight(7, 3, 40, squareTop: true, scale: 2.5).Length);
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
            byte[] wide = new byte[count * 2], wantWide = new byte[count * 2];
            LiquidLens.Measure(depths, width, height, radius, squareTop, (i, depth, _, _) => BitConverter.TryWriteBytes(depths.AsSpan(i), (float)depth));
            LiquidLens.Measure(ways, width, height, radius, squareTop, (i, _, dx, dy) => Way(ways, i, dx, dy));
            LiquidLens.Measure(wide, width, height, radius, squareTop, (i, depth, _, _) => BitConverter.TryWriteBytes(wide.AsSpan(i), depth), size: 8);
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
                BitConverter.TryWriteBytes(wantWide.AsSpan((y * width + x) * 8), depth);
            }
            Assert.Equal(wantDepths, depths);
            Assert.Equal(wantWays, ways);
            Assert.Equal(wantWide, wide);
        }
    }

    static void Way(byte[] pixels, int i, double dx, double dy) =>
        (pixels[i], pixels[i + 1]) = ((byte)(sbyte)Math.Round(127 * dx), (byte)(sbyte)Math.Round(127 * dy));
}
