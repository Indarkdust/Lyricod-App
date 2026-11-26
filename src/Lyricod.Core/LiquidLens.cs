namespace Lyricod.Core;

/// <summary>Lyricod Glass's 3D effect: a bright edge, which reads as thickness over a busy background, or a soft one
/// with shade on the side away from the light, which keeps its shape over a plain one.</summary>
public enum ShineStyle { Bright, Soft }

/// <summary>
/// The lens of Lyricod Glass's liquid effect: a displacement map that bends what's behind a rounded
/// rectangle inward near its edges, like a thick piece of glass, and the light the glass catches
/// along them.
/// </summary>
public static class LiquidLens
{
    /// <summary>
    /// The map, as BGRA pixels: red and green hold how far to look across and down (128 not at all,
    /// 255 the most inward from the left or top, 0 from the right or bottom); blue is 0 and alpha
    /// 255.
    /// </summary>
    /// <param name="radius">The corner radius, in pixels.</param>
    /// <param name="squareTop">The top edge is the screen's edge (the default island): no bending there.</param>
    public static byte[] Map(int width, int height, double radius, bool squareTop)
    {
        var pixels = new byte[width * height * 4];
        double band = Band(width, height, radius);
        Measure(pixels, width, height, radius, squareTop, (i, depth, dx, dy) =>
        {
            double strength = depth >= band || depth < 0 ? 0 : Math.Pow(1 - depth / band, 2);
            pixels[i + 2] = (byte)Math.Round(128 - 127 * dx * strength);
            pixels[i + 1] = (byte)Math.Round(128 - 127 * dy * strength);
            pixels[i + 3] = 255;
        });
        return pixels;
    }

    /// <summary>
    /// The light and shade on the glass, as premultiplied BGRA pixels of white over black: a thin
    /// bright line along the edge, strongest where it faces the light (above, a little to the left)
    /// and fainter on the far side, plus a soft glow over the band where the glass bends. Soft keeps
    /// all of it faint and shades that band on the side away from the light, just inside the line:
    /// white light doesn't show over a light background, and the shade gives the glass depth there.
    /// </summary>
    /// <param name="rim">How wide the bright line is, in pixels.</param>
    public static byte[] Light(int width, int height, double radius, bool squareTop, double rim, ShineStyle style)
    {
        var (lineMost, glowMost, shadeMost) = style == ShineStyle.Soft ? (0.24, 0.03, 0.1) : (0.6, 0.1, 0.0);
        var pixels = new byte[width * height * 4];
        double band = Band(width, height, radius);
        rim = Math.Max(rim, 0.5);
        // Toward the light: mostly up, a little to the left.
        double lightX = -0.35 / Math.Sqrt(1 + 0.35 * 0.35), lightY = -1 / Math.Sqrt(1 + 0.35 * 0.35);
        Measure(pixels, width, height, radius, squareTop, (i, depth, dx, dy) =>
        {
            // The pixels the edge only partly covers get the full line: the island's clip smooths them.
            if (depth < -1 || depth >= band) return;
            double facing = dx * lightX + dy * lightY;
            double lit = 0.15 + 0.85 * Math.Max(Math.Pow(Math.Max(facing, 0), 2), 0.55 * Math.Pow(Math.Max(-facing, 0), 2));
            double t = Math.Clamp(1 - depth / rim, 0, 1);
            double line = lineMost * lit * t * t * (3 - 2 * t);
            double near = 1 - Math.Max(depth, 0) / band;
            double glow = glowMost * Math.Pow(near, 3) * (0.5 + 0.5 * lit);
            double white = 1 - (1 - line) * (1 - glow);
            double shade = shadeMost * Math.Max(-facing, 0) * near * near * (1 - t);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)Math.Round(255 * white);
            pixels[i + 3] = (byte)Math.Round(255 * (1 - (1 - white) * (1 - shade)));
        });
        return pixels;
    }

    /// <summary>How far in from the edge the glass bends: about the corner's size, never past the middle.</summary>
    static double Band(int width, int height, double radius) =>
        Math.Max(2, Math.Min(Math.Min(width, height) / 2.0, Math.Max(radius * 1.5, 12)));

    /// <summary>
    /// Has <paramref name="visit"/> fill in each pixel of a BGRA buffer (given its offset there) from its
    /// depth inside the rounded rectangle (negative: outside) and the unit vector pointing out.
    /// </summary>
    static void Measure(byte[] pixels, int width, int height, double radius, bool squareTop, Action<int, double, double, double> visit)
    {
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2.0);
        // A square top is handled as a shape extending far above, so its top never comes near.
        double top = squareTop ? -height : 0;
        double centreX = width / 2.0, centreY = (top + height) / 2.0, halfX = width / 2.0, halfY = (height - top) / 2.0;
        for (int y = 0; y < height; y++)
        {
            double py = y + 0.5 - centreY, qy = Math.Abs(py) - (halfY - radius);
            // Along the straight part of a row every pixel is as far from the same edge, so one is worked
            // out and copied across: most of a wide island, whose maps are made again for each frame of
            // a change of size.
            double reach = halfX - radius + Math.Min(qy, 0);
            bool Straight(int x) => x >= 0 && x < width && Math.Abs(x + 0.5 - centreX) - (halfX - radius) <= Math.Min(qy, 0);
            int first = (int)Math.Ceiling(centreX - 0.5 - reach), last = (int)Math.Floor(centreX - 0.5 + reach);
            while (first <= last && !Straight(first)) first++;
            while (last >= first && !Straight(last)) last--;
            for (int x = 0; x < width; x++)
            {
                if (x > first && x <= last)
                {
                    var row = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(y * width * 4, width * 4));
                    row[(first + 1)..(last + 1)].Fill(row[first]);
                    x = last;
                    continue;
                }
                double px = x + 0.5 - centreX;
                double qx = Math.Abs(px) - (halfX - radius);
                double outX = Math.Max(qx, 0), outY = Math.Max(qy, 0), corner = Math.Sqrt(outX * outX + outY * outY);
                double depth = radius - corner - Math.Min(Math.Max(qx, qy), 0);
                (double dx, double dy) = corner > 0 ? (outX / corner * Math.Sign(px), outY / corner * Math.Sign(py))
                    : qx > qy ? (Math.Sign(px), 0.0) : (0.0, Math.Sign(py));
                visit((y * width + x) * 4, depth, dx, dy);
            }
        }
    }
}
