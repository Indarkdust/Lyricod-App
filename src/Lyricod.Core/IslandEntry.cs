namespace Lyricod.Core;

/// <summary>The island edge the pointer crossed coming in.</summary>
public enum IslandEdge { Side, Top, Bottom }

/// <summary>Decides which edge the pointer crossed when entering the island.</summary>
public static class IslandEntry
{
    public static bool IsFromBelow(double x, double y, double left, double top, double width, double height,
        (double X, double Y)? lastOutside = null) =>
        Edge(x, y, left, top, width, height, lastOutside) == IslandEdge.Bottom;

    public static IslandEdge Edge(double x, double y, double left, double top, double width, double height,
        (double X, double Y)? lastOutside = null)
    {
        double right = left + width, bottom = top + height;
        if (lastOutside is { } p)
        {
            // The last edge crossed along the sampled movement is the entry edge.
            double below = p.Y > bottom && y <= bottom ? (bottom - p.Y) / (y - p.Y) : -1;
            double side = Math.Max(
                p.X < left && x >= left ? (left - p.X) / (x - p.X) : -1,
                p.X > right && x <= right ? (right - p.X) / (x - p.X) : -1);
            double above = p.Y < top && y >= top ? (top - p.Y) / (y - p.Y) : -1;
            if (Math.Max(below, Math.Max(side, above)) >= 0)
                return below > Math.Max(side, above) ? IslandEdge.Bottom
                    : above > Math.Max(side, below) ? IslandEdge.Top : IslandEdge.Side;
        }

        // No outside sample (or the pill grew over it): use the nearest edge.
        // Exact corner ties count as a side.
        double sides = Math.Min(x - left, right - x);
        return bottom - y < Math.Min(sides, y - top) ? IslandEdge.Bottom
            : y - top < Math.Min(sides, bottom - y) ? IslandEdge.Top : IslandEdge.Side;
    }
}
