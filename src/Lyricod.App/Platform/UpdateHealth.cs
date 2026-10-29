namespace Lyricod.Platform;

/// <summary>
/// Right after a portable update the launcher keeps the old copy until this Lyricod says it has started
/// (it waits on the event named in LYRICOD_UPDATE_SIGNAL); if it doesn't, the launcher puts the old one back.
/// </summary>
static class UpdateHealth
{
    const string Variable = "LYRICOD_UPDATE_SIGNAL";

    /// <summary>The launcher is waiting to hear that this Lyricod has started.</summary>
    public static bool Pending { get; } = Environment.GetEnvironmentVariable(Variable) is { Length: > 0 };

    public static void SignalStarted()
    {
        try
        {
            if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } name) return;
            Environment.SetEnvironmentVariable(Variable, null); // not for anything this Lyricod starts later
            nint started = Win32.OpenEvent(Win32.EVENT_MODIFY_STATE, false, name);
            if (started == 0) return;
            Win32.SetEvent(started);
            Win32.CloseHandle(started);
        }
        catch (Exception) { } // the launcher then puts the old copy back after its wait: safe, if unneeded
    }
}
