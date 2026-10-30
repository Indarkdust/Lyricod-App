namespace Lyricod.Core;

/// <summary>
/// The deliberate move that expands an island set to hide on hover: the pointer travels sideways,
/// then turns and comes in through the island's top or bottom edge. Any other way in hides it, so
/// brushing past never opens the mini player.
/// </summary>
public static class IslandGesture
{
    /// <summary>How far the sideways leg has to go, in view pixels.</summary>
    public const double MinSideways = 40;

    /// <param name="trail">Where the pointer was before coming in, oldest first, from about the last second (screen pixels).</param>
    /// <param name="entry">Where it is now, just inside the island.</param>
    /// <param name="edge">The edge it came in through (see <see cref="IslandEntry.Edge"/>).</param>
    /// <param name="scale">The display's scale: pixel distances grow with it.</param>
    public static bool IsSidewaysThenIn(IReadOnlyList<(double X, double Y)> trail, (double X, double Y) entry, IslandEdge edge, double scale = 1)
    {
        if (edge == IslandEdge.Side || trail.Count < 2) return false;
        double sideways = MinSideways * scale, slack = 6 * scale;
        // The last leg, from where the pointer turned to where it came in, is up or down: down
        // through the top edge, up through the bottom one, with little drift to the side.
        for (int turn = trail.Count - 1; turn >= 1; turn--)
        {
            var corner = trail[turn];
            double inX = Math.Abs(entry.X - corner.X), inY = entry.Y - corner.Y;
            if (edge == IslandEdge.Top ? inY <= 0 : inY >= 0) continue;
            if (inX > Math.Max(slack, Math.Abs(inY) * 0.6)) continue;
            // The leg before it went sideways far enough, staying roughly level.
            for (int start = turn - 1; start >= 0; start--)
            {
                double acrossX = Math.Abs(corner.X - trail[start].X), acrossY = Math.Abs(corner.Y - trail[start].Y);
                if (acrossX >= sideways && acrossY <= Math.Max(slack, acrossX * 0.35)) return true;
            }
        }
        return false;
    }
}
