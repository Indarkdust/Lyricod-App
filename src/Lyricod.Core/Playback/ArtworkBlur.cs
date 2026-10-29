namespace Lyricod.Core;

/// <summary>
/// Softens a small copy of the cover once, for the lyrics window's background (the macOS app's
/// ArtworkDrift does the same with Core Image): blurred and a little more saturated, then shown
/// scaled up and slowly turning. Blurring once here costs nothing per frame later.
/// </summary>
public static class ArtworkBlur
{
    /// <summary>Blurs <paramref name="bgra"/> in place (three box passes, close to a Gaussian) and
    /// scales its saturation by <paramref name="saturation"/>.</summary>
    /// <param name="bgra">width × height pixels, 4 bytes each in B, G, R, A order.</param>
    public static void Soften(byte[] bgra, int width, int height, int radius, double saturation)
    {
        if (bgra.Length < width * height * 4 || width < 1 || height < 1) return;
        var scratch = new byte[bgra.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxPass(bgra, scratch, width, height, radius, horizontal: true);
            BoxPass(scratch, bgra, width, height, radius, horizontal: false);
        }
        if (saturation == 1) return;
        for (int i = 0; i < width * height * 4; i += 4)
        {
            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            bgra[i] = Clamp(luma + (b - luma) * saturation);
            bgra[i + 1] = Clamp(luma + (g - luma) * saturation);
            bgra[i + 2] = Clamp(luma + (r - luma) * saturation);
        }
    }

    /// <summary>A running-sum box blur along rows or columns, edges clamped.</summary>
    static void BoxPass(byte[] from, byte[] to, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width, length = horizontal ? width : height;
        int step = horizontal ? 4 : width * 4;
        double norm = 1.0 / (2 * radius + 1);
        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * width * 4 : line * 4;
            for (int c = 0; c < 4; c++)
            {
                int Pixel(int k) => from[start + Math.Clamp(k, 0, length - 1) * step + c];
                double sum = 0;
                for (int k = -radius; k <= radius; k++) sum += Pixel(k);
                for (int k = 0; k < length; k++)
                {
                    to[start + k * step + c] = Clamp(sum * norm);
                    sum += Pixel(k + radius + 1) - Pixel(k - radius);
                }
            }
        }
    }

    static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
