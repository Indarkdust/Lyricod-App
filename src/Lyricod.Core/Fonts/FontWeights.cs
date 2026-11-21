namespace Lyricod.Core.Fonts;

public enum LyricWeight { Regular, Medium, Semibold, Bold, Heavy }

public enum LyricFont { Default, MiSans, Other, WenKai }

public static class FontWeights
{
    public static ushort Get(LyricWeight weight, LyricFont font) => font switch
    {
        // MiSans numbers its named weights its own way; asking for exactly those picks them.
        LyricFont.MiSans => weight switch
        {
            LyricWeight.Regular => 330, LyricWeight.Medium => 380, LyricWeight.Bold => 630, LyricWeight.Heavy => 700, _ => 520,
        },
        // LXGW WenKai comes as one Medium file: asking for more would draw a fake bold over its thin strokes.
        LyricFont.WenKai => 500,
        // Microsoft YaHei has only Light, Regular and Bold, and Windows picks Regular for 500 and 600,
        // which made the default look thin: the lighter two map to Regular, the rest to Bold.
        LyricFont.Default => weight is LyricWeight.Regular or LyricWeight.Medium ? (ushort)400 : (ushort)700,
        _ => weight switch
        {
            LyricWeight.Regular => 400, LyricWeight.Medium => 500, LyricWeight.Bold => 700, LyricWeight.Heavy => 900, _ => 600,
        },
    };
}
