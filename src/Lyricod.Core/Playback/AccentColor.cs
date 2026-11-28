namespace Lyricod.Core;

/// <summary>
/// The island's accent colour as text: any colour, a deep blue or a dark one picked by hand too,
/// lightened until it reads on the island.
/// </summary>
public static class AccentColor
{
    /// <summary>
    /// How light the island's text is at least (relative luminance): about 13 : 1 against black at
    /// full strength, whatever the colour, and still 3.7 : 1 at half.
    /// </summary>
    public const double TextLuminance = 0.6;

    /// <summary>
    /// <paramref name="colour"/> mixed toward white (in sRGB, which keeps its hue) just far enough
    /// to reach <paramref name="luminance"/>. One already that light comes back as it is.
    /// </summary>
    public static (byte R, byte G, byte B) Lifted((byte R, byte G, byte B) colour, double luminance)
    {
        if (Luminance(colour) >= luminance) return colour;
        // The mix only gets lighter toward white, so halving finds where it's first light enough.
        double low = 0, high = 1;
        for (int i = 0; i < 16; i++)
        {
            double mid = (low + high) / 2;
            if (Luminance(Mix(colour, mid)) >= luminance) high = mid; else low = mid;
        }
        return Mix(colour, high);
    }

    /// <summary>Relative luminance as WCAG defines it: 0 for black, 1 for white.</summary>
    public static double Luminance((byte R, byte G, byte B) colour) =>
        0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);

    static double Linear(byte channel)
    {
        double c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    static (byte R, byte G, byte B) Mix((byte R, byte G, byte B) colour, double white)
    {
        byte Channel(byte c) => (byte)Math.Round(c + (255 - c) * white);
        return (Channel(colour.R), Channel(colour.G), Channel(colour.B));
    }
}
