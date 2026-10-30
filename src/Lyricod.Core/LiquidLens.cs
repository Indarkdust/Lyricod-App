namespace Lyricod.Core;

/// <summary>
/// The lens of Lyricod Glass's liquid effect: a displacement map for a rounded rectangle that bends
/// what's behind the glass inward near its edges, strongest at the edge and gone a short way in, as
/// light bends through the rounded edge of a thick piece of glass; and the light the glass catches
/// along that same edge, for its 3D effect.
/// </summary>
public static class LiquidLens
{
    /// <summary>
    /// The map, as BGRA pixels: red and green hold how far to look across and down (128 means not at
    /// all, 255 the most inward from the left or top, 0 from the right or bottom); blue is 0 and alpha 255.
    /// </summary>
    /// <param name="radius">The corner radius, in pixels.</param>
    /// <param name="squareTop">The top edge is the screen's edge (the default island): no bending there.</param>
    public static byte[] Map(int width, int height, double radius, bool squareTop)
    {
        var pixels = new byte[width * height * 4];
        double band = Band(width, height, radius);
        Measure(width, height, radius, squareTop, (i, depth, dx, dy) =>
        {
            double strength = depth >= band || depth < 0 ? 0 : Math.Pow(1 - depth / band, 2);
            // Inward: against the way out.
            pixels[i + 2] = (byte)Math.Round(128 - 127 * dx * strength);
            pixels[i + 1] = (byte)Math.Round(128 - 127 * dy * strength);
            pixels[i + 3] = 255;
        });
        return pixels;
    }

    /// <summary>
    /// The light on the glass, as premultiplied BGRA pixels of white: a thin bright line along the
    /// edge, strongest where the edge faces the light (above, a little to the left) and glinting
    /// more faintly on the far side, where light comes back through the glass; and a soft glow over
    /// the band where the glass bends, as its rounded edge gathers light.
    /// </summary>
    /// <param name="rim">How wide the bright line is, in pixels.</param>
    public static byte[] Light(int width, int height, double radius, bool squareTop, double rim)
    {
        var pixels = new byte[width * height * 4];
        double band = Band(width, height, radius);
        rim = Math.Max(rim, 0.5);
        // Toward the light: mostly up, a little to the left.
        double lightX = -0.35 / Math.Sqrt(1 + 0.35 * 0.35), lightY = -1 / Math.Sqrt(1 + 0.35 * 0.35);
        Measure(width, height, radius, squareTop, (i, depth, dx, dy) =>
        {
            // The pixels the edge only partly covers get the full line: the island's clip smooths them.
            if (depth < -1 || depth >= band) return;
            double facing = dx * lightX + dy * lightY;
            double lit = 0.15 + 0.85 * Math.Max(Math.Pow(Math.Max(facing, 0), 2), 0.55 * Math.Pow(Math.Max(-facing, 0), 2));
            double t = Math.Clamp(1 - depth / rim, 0, 1);
            double line = 0.6 * lit * t * t * (3 - 2 * t);
            double glow = 0.1 * Math.Pow(1 - Math.Max(depth, 0) / band, 3) * (0.5 + 0.5 * lit);
            byte alpha = (byte)Math.Round(255 * (1 - (1 - line) * (1 - glow)));
            pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = alpha;
        });
        return pixels;
    }

    /// <summary>How far in from the edge the glass bends: about the corner's size, never past the middle.</summary>
    static double Band(int width, int height, double radius) =>
        Math.Max(2, Math.Min(Math.Min(width, height) / 2.0, Math.Max(radius * 1.5, 12)));

    /// <summary>
    /// Visits each pixel (by its offset in a BGRA buffer) with how far inside the rounded rectangle it
    /// is (negative: outside) and the way out from there, one pixel long.
    /// </summary>
    static void Measure(int width, int height, double radius, bool squareTop, Action<int, double, double, double> visit)
    {
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2.0);
        // A square top is handled as a shape extending far above, so its top never comes near.
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
            visit((y * width + x) * 4, depth, dx, dy);
        }
    }
}
