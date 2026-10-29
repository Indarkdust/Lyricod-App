namespace Lyricod.Core;

/// <summary>Decides which edge the pointer crossed when entering the island.</summary>
public static class IslandEntry
{
    public static bool IsFromBelow(double x, double y, double left, double top, double width, double height,
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
            if (Math.Max(below, Math.Max(side, above)) >= 0) return below > Math.Max(side, above);
        }

        // No outside sample (or the pill grew over it): use the nearest edge.
        // Exact corner ties count as a side, as does the top edge.
        return bottom - y < Math.Min(Math.Min(x - left, right - x), y - top);
    }
}
