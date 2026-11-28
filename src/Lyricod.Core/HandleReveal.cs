namespace Lyricod.Core;

/// <summary>
/// When an overlay's move handle shows: once the pointer has rested on the hover area, for as long as it
/// stays there or on the handle, and a moment longer, so the way from one to the other doesn't lose it.
/// </summary>
public sealed class HandleReveal
{
    /// <summary>How long the pointer rests on the area first: passing over it mustn't bring the handle up.</summary>
    public const double RestSeconds = 0.4;

    /// <summary>How long the handle stays once the pointer has left the area and the handle.</summary>
    public const double LingerSeconds = 0.3;

    double? _restingSince;
    double _lastOver;

    public bool Shown { get; private set; }

    /// <param name="overArea">The pointer is over the hover area.</param>
    /// <param name="overHandle">The pointer is over the handle's place (it counts only while the handle shows).</param>
    /// <param name="now">Steady-clock seconds.</param>
    /// <returns>Whether the handle shows now.</returns>
    public bool Update(bool overArea, bool overHandle, double now)
    {
        if (Shown)
        {
            if (overArea || overHandle) _lastOver = now;
            else if (now - _lastOver >= LingerSeconds) Reset();
        }
        else if (!overArea) _restingSince = null;
        else if (now - (_restingSince ??= now) >= RestSeconds)
        {
            Shown = true;
            _lastOver = now;
        }
        return Shown;
    }

    /// <summary>The window went away, or the handle no longer applies: the next rest starts afresh.</summary>
    public void Reset()
    {
        Shown = false;
        _restingSince = null;
    }
}
