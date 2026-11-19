using Microsoft.Win32;

namespace Lyricod.Platform;

/// <summary>
/// Lyricod's entry in Windows' list of installed apps. Setup writes it, with the version; an update
/// the launcher swapped in ran no Setup, so Lyricod corrects the version itself (winget reads it too).
/// </summary>
static class InstalledEntry
{
    // Setup's AppId (installer/Lyricod.iss), as Inno Setup names its key.
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{A757848B-CCB2-45AC-BD19-9F2235B470B6}_is1";

    public static void Refresh(string root, string version)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key, writable: true);
            // Only the entry of this very copy.
            if (key?.GetValue("Inno Setup: App Path") is not string installed || !SameFolder(installed, root)) return;
            if (key.GetValue("DisplayVersion") as string == version) return;
            key.SetValue("DisplayVersion", version);
            key.SetValue("DisplayName", "Lyricod " + version);
        }
        catch (Exception) { }
    }

    static bool SameFolder(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
}
