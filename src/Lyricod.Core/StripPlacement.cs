namespace Lyricod.Core;

/// <summary>Where the desktop strip goes when its height changes.</summary>
public static class StripPlacement
{
    /// <summary>
    /// The strip's new top edge. It grows and shrinks upward from its bottom edge, so it stays where it was put,
    /// but it doesn't grow above the top of its screen, where the line would be cut off: at the top, it grows and
    /// shrinks from its top edge, so a bigger font and back leaves it where it was.
    /// </summary>
    public static int Top(int top, int grow, int screenTop) =>
        top <= screenTop ? top : Math.Max(top - grow, screenTop);
}
