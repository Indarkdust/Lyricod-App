namespace Lyricod.Core;

/// <summary>
/// Tells a start by the user from one made for them. Windows' Run entry, Lyricod restarting itself and
/// Setup reopening it after an update all pass <see cref="Flag"/>.
/// </summary>
public static class BackgroundStart
{
    public const string Flag = "--background";

    /// <summary>A Run entry's command line with the flag added; null when it ends with it already, or is empty (no entry).</summary>
    public static string? WithFlag(string command)
    {
        string trimmed = command.TrimEnd();
        return trimmed.Length == 0 || trimmed.EndsWith(" " + Flag, StringComparison.Ordinal) ? null : $"{trimmed} {Flag}";
    }

    /// <summary>Whether the user started this Lyricod: no flag, and not the launcher starting it to finish an update.</summary>
    public static bool ByUser(IEnumerable<string> arguments, bool afterUpdate) => !afterUpdate && !arguments.Contains(Flag);
}
