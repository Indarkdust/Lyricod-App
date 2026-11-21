namespace Lyricod.Core;

/// <summary>Which of the players in Windows' media controls Lyricod follows.</summary>
public static class SessionPick
{
    /// <summary>
    /// The chosen player when it's open (<paramref name="choice"/> is its app id, or "auto"), else
    /// one that's playing, preferring the one Windows calls current. Some players show up twice
    /// under one app id (NetEase with a progress plugin keeps a session without the song's position
    /// next to one with it); the one that reports a position is followed, or Lyricod would time the
    /// song itself.
    /// </summary>
    public static T? Choose<T>(IReadOnlyList<T> sessions, T? current, string choice,
        Func<T, string?> appId, Func<T, bool> playing, Func<T, bool> hasTimeline) where T : class
    {
        var chosen = (choice != "auto" ? sessions.FirstOrDefault(s => appId(s) == choice) : null)
            ?? (current != null && playing(current) ? current : null)
            ?? sessions.FirstOrDefault(playing)
            ?? current ?? sessions.FirstOrDefault();
        if (chosen == null || hasTimeline(chosen)) return chosen;
        string? id = appId(chosen);
        var siblings = sessions.Where(s => !ReferenceEquals(s, chosen) && appId(s) == id && hasTimeline(s)).ToList();
        // A playing session is only swapped for one that's playing too, never for a paused leftover.
        return (playing(chosen) ? siblings.FirstOrDefault(playing) : siblings.OrderByDescending(playing).FirstOrDefault())
            ?? chosen;
    }
}
