namespace Lyricod.Core;

/// <summary>
/// The lens of Lyricod Glass's liquid effect: a displacement map for a rounded rectangle that bends
/// what's behind the glass inward near its edges, strongest at the edge and gone a short way in, as
/// light bends through the rounded edge of a thick piece of glass.
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
        // Where the bending fades out: about the corner's size, never past the middle.
        double band = Math.Max(2, Math.Min(Math.Min(width, height) / 2.0, Math.Max(radius * 1.5, 12)));
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2.0);
        // A square top is handled as a shape extending far above, so its top never comes near.
        double top = squareTop ? -height : 0;
        double centreX = width / 2.0, centreY = (top + height) / 2.0, halfX = width / 2.0, halfY = (height - top) / 2.0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            double px = x + 0.5 - centreX, py = y + 0.5 - centreY;
            // Distance inside the rounded rectangle, and the way out of it.
            double qx = Math.Abs(px) - (halfX - radius), qy = Math.Abs(py) - (halfY - radius);
            double outX = Math.Max(qx, 0), outY = Math.Max(qy, 0), corner = Math.Sqrt(outX * outX + outY * outY);
            double depth = radius - corner - Math.Min(Math.Max(qx, qy), 0);
            (double dx, double dy) = corner > 0 ? (outX / corner * Math.Sign(px), outY / corner * Math.Sign(py))
                : qx > qy ? (Math.Sign(px), 0.0) : (0.0, Math.Sign(py));
            double strength = depth >= band || depth < 0 ? 0 : Math.Pow(1 - depth / band, 2);
            int i = (y * width + x) * 4;
            // Inward: against the way out.
            pixels[i + 2] = (byte)Math.Round(128 - 127 * dx * strength);
            pixels[i + 1] = (byte)Math.Round(128 - 127 * dy * strength);
            pixels[i + 3] = 255;
        }
        return pixels;
    }
}
