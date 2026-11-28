namespace Lyricod.Core;

/// <summary>A rectangle on screen, in physical pixels.</summary>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>Which side of a floating island stays put while it grows.</summary>
public enum IslandAnchorX { Centre, Left, Right }

public enum IslandAnchorY { Top, Bottom }

/// <summary>
/// Where the floating island sits. It's saved as one point and grows away from the screen's edges:
/// from its centre if its widest layout fits around the point, else from the side nearest the edge;
/// down from its top if its tallest layout (the mini player) fits below, else up from its bottom.
/// </summary>
public static class IslandPlacement
{
    /// <summary>
    /// The point to save for a pill dropped at <paramref name="pill"/>: its centre, left or right
    /// edge across and its top or bottom edge down, kept inside <paramref name="area"/>. Whole
    /// pixels, rounded so that <see cref="Anchors"/> reads the same edges back.
    /// </summary>
    public static (int X, int Y) Spot(ScreenRect pill, ScreenRect area, double maxWidth, double maxHeight)
    {
        double centre = pill.Left + pill.Width / 2;
        // Snapped to the centre line, its window still sits on a whole pixel, so the pill is within one of
        // the line: the line is what's saved, not the pill's own centre, which rounds to the next pixel as often.
        if (Math.Abs(centre - CentreLine(area)) < 1) centre = CentreLine(area);
        double x = AnchorX(centre, area, maxWidth) switch
        {
            IslandAnchorX.Centre => Math.Min(Math.Max(Math.Round(centre), Math.Ceiling(area.Left + maxWidth / 2)), Math.Floor(area.Right - maxWidth / 2)),
            IslandAnchorX.Left => Math.Round(Math.Max(pill.Left, area.Left)),
            _ => Math.Round(Math.Min(pill.Right, area.Right)),
        };
        double y = pill.Top + maxHeight <= area.Bottom
            ? Math.Floor(Math.Max(pill.Top, area.Top))
            : Math.Ceiling(Math.Min(pill.Bottom, area.Bottom));
        return ((int)x, (int)y);
    }

    /// <summary>A point inside the owning display, separate from the saved edge used for placement.</summary>
    public static (int X, int Y) DisplayPoint(ScreenRect pill, ScreenRect display) =>
        ((int)Math.Clamp(Math.Floor(pill.Left + pill.Width / 2), display.Left, display.Right - 1),
         (int)Math.Clamp(Math.Floor(pill.Top + pill.Height / 2), display.Top, display.Bottom - 1));

    public static (IslandAnchorX X, IslandAnchorY Y) Anchors(double x, double y, ScreenRect area, double maxWidth, double maxHeight) =>
        (AnchorX(x, area, maxWidth), y + maxHeight <= area.Bottom ? IslandAnchorY.Top : IslandAnchorY.Bottom);

    static IslandAnchorX AnchorX(double x, ScreenRect area, double maxWidth) =>
        x - maxWidth / 2 >= area.Left && x + maxWidth / 2 <= area.Right ? IslandAnchorX.Centre
        : x - maxWidth / 2 < area.Left ? IslandAnchorX.Left : IslandAnchorX.Right;

    /// <summary>A screen's centre line, where a reset island sits: between two pixels when the width is odd.</summary>
    public static double CentreLine(ScreenRect area) => area.Left + area.Width / 2;

    /// <summary>
    /// A saved point across, to place by. One saved on its screen's centre line is that line, which a whole
    /// pixel can't always say, so that the island sits exactly where a reset one does.
    /// </summary>
    public static double Across(int x, ScreenRect area) => Math.Abs(x - CentreLine(area)) < 1 ? CentreLine(area) : x;

    /// <summary>How near a dragged island's centre comes to its screen's centre line before it sits on it, in view pixels.</summary>
    public const double SnapRange = 12;

    /// <summary>
    /// Where a dragged island's centre goes across: onto the centre line of <paramref name="area"/>, where a
    /// reset island sits, while within <paramref name="range"/> of it; otherwise where the pointer has it.
    /// </summary>
    public static double SnapCentre(double centre, ScreenRect area, double range)
    {
        double middle = CentreLine(area);
        return Math.Abs(centre - middle) <= range ? middle : centre;
    }

    /// <summary>
    /// The left edge of the island's move handle, along the pill: a gap past the pill's trailing end; a gap
    /// before its leading end when there's no room after it; over its trailing end, <paramref name="inset"/>
    /// in from it, when there's none either side. The room runs from <paramref name="from"/> to <paramref name="to"/>.
    /// </summary>
    public static double HandleLeft(double start, double end, double size, double gap, double from, double to, double inset) =>
        end + gap + size <= to ? end + gap
        : start - gap - size >= from ? start - gap - size
        : end - inset - size;

    /// <summary>Top-left of a box (the window, or the pill itself) that holds the pill against its anchored edges at the saved point.</summary>
    public static (double Left, double Top) Origin(double x, double y, (IslandAnchorX X, IslandAnchorY Y) anchors, double width, double height) =>
        (anchors.X switch { IslandAnchorX.Left => x, IslandAnchorX.Right => x - width, _ => x - width / 2 },
         anchors.Y == IslandAnchorY.Top ? y : y - height);
}
