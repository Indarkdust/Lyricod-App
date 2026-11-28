namespace Lyricod.Core;

/// <summary>Where the desktop strip goes when its height changes, and where its move handle sits.</summary>
public static class StripPlacement
{
    /// <summary>
    /// The strip's new top edge. It grows and shrinks upward from its bottom edge, so it stays where it was put,
    /// but it doesn't grow above the top of its screen, where the line would be cut off: at the top, it grows and
    /// shrinks from its top edge, so a bigger font and back leaves it where it was.
    /// </summary>
    public static int Top(int top, int grow, int screenTop) =>
        top <= screenTop ? top : Math.Max(top - grow, screenTop);

    /// <summary>
    /// The left edge of the strip's move handle, measured from the strip's left edge: a gap past the line's
    /// <paramref name="end"/>, or as near that as keeps it on the strip, <paramref name="strip"/> wide, and on
    /// its screen, which runs from <paramref name="from"/> to <paramref name="to"/> and which the strip may
    /// hang past. With no room after the line it sits over the line's end.
    /// </summary>
    public static double HandleLeft(double end, double size, double gap, double strip, double from, double to) =>
        Math.Max(Math.Min(end + gap, Math.Min(strip, to) - size), Math.Max(from, 0));
}
