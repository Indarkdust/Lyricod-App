namespace Lyricod.Core;

/// <summary>Where the desktop strip goes when its height changes.</summary>
public static class StripPlacement
{
    /// <summary>
    /// The strip's new top edge. It grows and shrinks upward from its bottom edge, so it stays where it was put,
    /// but it doesn't grow above the top of its screen: from there it grows downward.
    /// </summary>
    public static int Top(int top, int grow, int screenTop) =>
        grow > 0 ? Math.Max(top - grow, Math.Min(top, screenTop)) : top - grow;
}
