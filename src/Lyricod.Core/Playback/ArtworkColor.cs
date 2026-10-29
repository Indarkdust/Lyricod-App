namespace Lyricod.Core;

/// <summary>
/// Picks an album cover's dominant colour, tuned to stay readable on black (the macOS app's
/// ArtworkColor): pixels are bucketed by hue, vivid ones weigh more, and greys, near-blacks and
/// near-whites are ignored, so a dark cover with a neon sign picks the sign.
/// </summary>
public static class ArtworkColor
{
    /// <param name="bgra">A small thumbnail (32×32 is plenty), 4 bytes a pixel in B, G, R, A order.</param>
    /// <returns>(r, g, b) in 0–255, or null for a cover without a clear colour.</returns>
    public static (byte R, byte G, byte B)? Dominant(ReadOnlySpan<byte> bgra)
    {
        const int buckets = 12;
        Span<double> weight = stackalloc double[buckets];
        Span<double> sumR = stackalloc double[buckets], sumG = stackalloc double[buckets], sumB = stackalloc double[buckets];
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
            var (h, s, v) = ToHsv(r, g, b);
            if (s <= 0.2 || v <= 0.2) continue;
            double w = s * v;
            int k = Math.Min((int)(h * buckets), buckets - 1);
            weight[k] += w;
            sumR[k] += r * w; sumG[k] += g * w; sumB[k] += b * w;
        }
        int best = 0;
        for (int k = 1; k < buckets; k++) if (weight[k] > weight[best]) best = k;
        if (weight[best] <= 2) return null;
        var (hue, sat, val) = ToHsv(sumR[best] / weight[best], sumG[best] / weight[best], sumB[best] / weight[best]);
        // Bright and moderately saturated, so it reads like the cover but never muddy on black.
        var (tr, tg, tb) = FromHsv(hue, Math.Clamp(sat, 0.35, 0.7), Math.Max(val, 0.95));
        return ((byte)Math.Round(tr * 255), (byte)Math.Round(tg * 255), (byte)Math.Round(tb * 255));
    }

    /// <summary>Hue, saturation and value, each 0–1.</summary>
    static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = (g - b) / d % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }
        return (h, max == 0 ? 0 : d / max, max);
    }

    static (double R, double G, double B) FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h * 6 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h * 6) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
