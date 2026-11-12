namespace Lyricod;

/// <summary>
/// What went wrong, kept in a small file beside the settings (<c>%LOCALAPPDATA%\Lyricod\lyricod.log</c>).
/// Lyricod carries on past most failures (a timer's callback must never throw: that ends the process),
/// and a released build has no debug output, so without this nothing says what happened on someone's
/// PC. The file stays on the PC: its lines can carry paths with the user's name in them.
/// </summary>
static class Log
{
    /// <summary>The file starts afresh past this size, keeping the last one beside it as <c>lyricod.log.old</c>.</summary>
    const long MaxBytes = 256 * 1024;
    /// <summary>The same line again within this long isn't written again: a timer failing on every tick would fill the file.</summary>
    const int RepeatSeconds = 30;

    static readonly Lock Gate = new();
    static string? _last;
    static long _lastAt;

    public static string FilePath { get; } = Path.Combine(Settings.DataDir, "lyricod.log");

    /// <summary>Writes a line: an exception (with where it came from) or a message. Never throws; any thread.</summary>
    public static void Write(object? what)
    {
        string message = what?.ToString() ?? "";
        System.Diagnostics.Debug.WriteLine(message);
        try
        {
            lock (Gate)
            {
                long now = Environment.TickCount64;
                if (message == _last && now - _lastAt < RepeatSeconds * 1000L) return;
                (_last, _lastAt) = (message, now);
                Directory.CreateDirectory(Settings.DataDir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
            }
        }
        catch (Exception) { /* nowhere left to say so */ }
    }
}
