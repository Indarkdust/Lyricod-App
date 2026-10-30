namespace Lyricod.Core;

/// <summary>
/// The deliberate move that expands an island set to hide on hover: the pointer travels sideways,
/// then turns up or down. Either it comes in that way through the island's top or bottom edge, or,
/// once over the hidden island, it makes the turn there (sliding in along the top of the screen,
/// then down). Any other way in hides it, so brushing past never opens the mini player.
/// </summary>
public static class IslandGesture
{
    /// <summary>How far the sideways leg has to go, in view pixels.</summary>
    public const double MinSideways = 30;

    /// <summary>How far the turn has to go when it's made over the island, in view pixels.</summary>
    public const double MinTurn = 10;

    /// <param name="trail">Where the pointer was before coming in, oldest first, from about the last second (screen pixels).</param>
    /// <param name="entry">Where it is now, just inside the island.</param>
    /// <param name="edge">The edge it came in through (see <see cref="IslandEntry.Edge"/>).</param>
    /// <param name="scale">The display's scale: pixel distances grow with it.</param>
    public static bool IsSidewaysThenIn(IReadOnlyList<(double X, double Y)> trail, (double X, double Y) entry, IslandEdge edge, double scale = 1) =>
        edge != IslandEdge.Side && Turned(trail, entry, scale, inY => edge == IslandEdge.Top ? inY > 0 : inY < 0);

    /// <summary>Whether the pointer, over the island now at <paramref name="now"/>, has just moved sideways and then turned up or down.</summary>
    public static bool IsSidewaysThenTurn(IReadOnlyList<(double X, double Y)> trail, (double X, double Y) now, double scale = 1) =>
        Turned(trail, now, scale, inY => Math.Abs(inY) >= MinTurn * scale);

    /// <summary>
    /// Whether the path ends with a leg that's more up or down than sideways (and passes
    /// <paramref name="turn"/>, given how far it went down), after a leg that went sideways far
    /// enough while staying roughly level. Hands curve, so neither leg has to be straight.
    /// </summary>
    static bool Turned(IReadOnlyList<(double X, double Y)> trail, (double X, double Y) end, double scale, Func<double, bool> turn)
    {
        if (trail.Count < 2) return false;
        double sideways = MinSideways * scale, slack = 6 * scale;
        for (int corner = trail.Count - 1; corner >= 1; corner--)
        {
            var c = trail[corner];
            double inX = Math.Abs(end.X - c.X), inY = end.Y - c.Y;
            if (!turn(inY) || inX > Math.Max(slack, Math.Abs(inY))) continue;
            for (int start = corner - 1; start >= 0; start--)
            {
                double acrossX = Math.Abs(c.X - trail[start].X), acrossY = Math.Abs(c.Y - trail[start].Y);
                if (acrossX >= sideways && acrossY <= Math.Max(slack, acrossX * 0.5)) return true;
            }
        }
        return false;
    }
}
