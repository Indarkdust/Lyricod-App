namespace Lyricod.Core;

/// <summary>
/// The deliberate move that expands an island set to hide on hover, made outside it on the way in:
/// the pointer travels sideways, then turns and moves up or down into the island through its top or
/// bottom edge. Any other way in hides it, so brushing past never opens the mini player.
/// </summary>
public static class IslandGesture
{
    /// <summary>How far the sideways leg has to go, in view pixels.</summary>
    public const double MinSideways = 30;

    /// <summary>How far the turn has to go up or down before coming in, in view pixels.</summary>
    public const double MinVertical = 24;

    /// <summary>
    /// Whether the pointer came in by moving sideways, then up or down into the island. The turn
    /// can curve, as hands do: the last leg just has to end up more vertical than sideways.
    /// </summary>
    /// <param name="trail">Where the pointer was outside the island, oldest first, from about the last second and a half (screen pixels).</param>
    /// <param name="entry">Where it is now, just inside the island.</param>
    /// <param name="edge">The edge it came in through (see <see cref="IslandEntry.Edge"/>).</param>
    /// <param name="scale">The display's scale: pixel distances grow with it.</param>
    public static bool IsSidewaysThenIn(IReadOnlyList<(double X, double Y)> trail, (double X, double Y) entry, IslandEdge edge, double scale = 1)
    {
        if (edge == IslandEdge.Side || trail.Count < 2) return false;
        double sideways = MinSideways * scale, vertical = MinVertical * scale, slack = 6 * scale;
        for (int corner = trail.Count - 1; corner >= 1; corner--)
        {
            var c = trail[corner];
            double inX = Math.Abs(entry.X - c.X), inY = entry.Y - c.Y;
            // Down through the top edge, or up through the bottom one, far enough and mostly vertical.
            if ((edge == IslandEdge.Top ? inY : -inY) < vertical || inX > Math.Max(slack, Math.Abs(inY))) continue;
            // Before that, sideways far enough while staying roughly level.
            for (int start = corner - 1; start >= 0; start--)
            {
                double acrossX = Math.Abs(c.X - trail[start].X), acrossY = Math.Abs(c.Y - trail[start].Y);
                if (acrossX >= sideways && acrossY <= Math.Max(slack, acrossX * 0.5)) return true;
            }
        }
        return false;
    }
}
