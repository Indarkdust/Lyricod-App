namespace Lyricod.Core.Fonts;

public enum LyricWeight { Regular, Medium, Semibold, Bold, Heavy }

/// <summary>Which kind of font the weights are for.</summary>
public enum LyricFont { Default, MiSans, Other }

public static class FontWeights
{
    public static ushort Get(LyricWeight weight, LyricFont font) => font switch
    {
        // MiSans numbers its named weights its own way; asking for exactly those picks them.
        LyricFont.MiSans => weight switch
        {
            LyricWeight.Regular => 330, LyricWeight.Medium => 380, LyricWeight.Bold => 630, LyricWeight.Heavy => 700, _ => 520,
        },
        // Microsoft YaHei has only Light, Regular and Bold, and Windows picks Regular for 500 and 600,
        // which made the default look thin: the lighter two map to Regular, the rest to Bold.
        LyricFont.Default => weight is LyricWeight.Regular or LyricWeight.Medium ? (ushort)400 : (ushort)700,
        _ => weight switch
        {
            LyricWeight.Regular => 400, LyricWeight.Medium => 500, LyricWeight.Bold => 700, LyricWeight.Heavy => 900, _ => 600,
        },
    };
}
