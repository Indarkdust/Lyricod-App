using Lyricod.Core;
using Microsoft.Win32;

namespace Lyricod.Platform;

/// <summary>Opens Lyricod when Windows starts: an entry under the user's Run key (no admin rights; it also shows in Task Manager's Startup apps).</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Lyricod";
    static string Command => $"\"{Environment.ProcessPath}\" {BackgroundStart.Flag}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string value && value.Length > 0;
            }
            catch (Exception) { return false; }
        }
    }

    public static bool Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(Name, Command);
            else key.DeleteValue(Name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>An entry without the flag gets it, whatever program it names: by the flag Lyricod knows Windows started it.</summary>
    public static void AddFlag()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(Name) is string value && BackgroundStart.WithFlag(value) is { } flagged) key.SetValue(Name, flagged);
        }
        catch (Exception) { /* it stays as it was, and that start counts as the user's */ }
    }
}
