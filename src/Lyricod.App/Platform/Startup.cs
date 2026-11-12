using Microsoft.Win32;

namespace Lyricod.Platform;

/// <summary>Opens Lyricod when Windows starts: an entry under the user's Run key (no admin rights; it also shows in Task Manager's Startup apps).</summary>
static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Lyricod";
    static string Command => $"\"{Environment.ProcessPath}\"";

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
}
