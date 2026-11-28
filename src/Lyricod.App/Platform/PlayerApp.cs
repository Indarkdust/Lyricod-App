using System.Diagnostics;
using System.Runtime.InteropServices;
using Lyricod.Core;
using Microsoft.Win32;

namespace Lyricod.Platform;

/// <summary>
/// A music player as a program on this PC, from the app id its media session reports and the file it was
/// seen running from: its name, whether it's open, and opening it. Windows lists a program among its apps
/// under the id the program gave itself, a packaged app's id, or its path (see <see cref="PlayerId.AppsFolderIds"/>).
/// </summary>
static class PlayerApp
{
    const string AppsFolder = @"shell:AppsFolder\";

    static readonly ProgramFolders Folders = new(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.System), Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>The name of each id asked about, with the path it was worked out from. Used on the UI thread only.</summary>
    static readonly Dictionary<string, (string Path, string Name)> Names = [];

    /// <summary>The web browsers registered with Windows: the app ids their link types carry, and their command lines.</summary>
    static readonly Lazy<(List<string> Ids, List<string> Commands)> Browsers = new(() =>
    {
        List<string> ids = [], commands = [];
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var clients = root.OpenSubKey(@"Software\Clients\StartMenuInternet");
                foreach (string name in clients?.GetSubKeyNames() ?? [])
                {
                    using var command = clients!.OpenSubKey($@"{name}\shell\open\command");
                    if (command?.GetValue(null) is string line) commands.Add(line);
                    using var links = clients.OpenSubKey($@"{name}\Capabilities\URLAssociations");
                    if (links?.GetValue("https") is not string type) continue;
                    using var kind = Registry.ClassesRoot.OpenSubKey(type);
                    if (kind?.GetValue("AppUserModelId") is string id) ids.Add(id);
                }
            }
            catch (Exception) { /* a key that can't be read lists no browser */ }
        }
        return (ids, commands);
    });

    /// <summary>Whether the id is a web browser's: opening one again wouldn't bring the music back.</summary>
    public static bool IsBrowser(string id) => PlayerId.IsBrowser(id, Browsers.Value.Ids, Browsers.Value.Commands);

    /// <summary>
    /// What <see cref="Open"/> would start for this player: the app Windows lists under its id, else the file
    /// it ran from if that's still there; null when there's neither. Nothing is opened.
    /// </summary>
    public static string? Target(string id, string path) =>
        ListedName(id) != null ? AppsFolder + id : File.Exists(path) ? path : null;

    /// <summary>Opens a <see cref="Target"/>, off the calling thread: Windows can take a while over it, or ask the user first.</summary>
    public static void Open(string target) => Task.Run(() =>
    {
        try
        {
            var start = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                // Never left to be Lyricod's own folder, which an update moves aside: a folder a process works in can't be moved.
                WorkingDirectory = target.StartsWith(AppsFolder, StringComparison.Ordinal)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Path.GetDirectoryName(target) ?? "",
            };
            // No process comes back for a packaged app, which Windows starts itself.
            Process.Start(start)?.Dispose();
        }
        catch (Exception e) { Log.Write($"open {target}: {e.Message}"); }
    });

    /// <summary>Whether the player is open, with or without a song (see <see cref="PlayerId.IsRunning"/>).</summary>
    public static bool IsRunning(string id, string path, IReadOnlyList<string> sessionIds)
    {
        var processes = AudioMeter.Processes();
        return PlayerId.IsRunning(id, path, sessionIds, processes.Select(p => p.Name), processes.Select(p => PackagedId(p.Id)).OfType<string>());
    }

    /// <summary>The file a running player was started from, when its id leads to its process ("cloudmusic.exe"); else null.</summary>
    public static unsafe string? RunningPath(string id)
    {
        if (AudioMeter.FindRootProcess(id) is not { } pid) return null;
        nint process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            const int Size = 1024;
            char* path = stackalloc char[Size];
            uint length = Size;
            return Win32.QueryFullProcessImageName(process, 0, path, ref length) ? new string(path, 0, (int)length) : null;
        }
        finally { Win32.CloseHandle(process); }
    }

    static unsafe string? PackagedId(uint pid)
    {
        nint process = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            const int Size = 256; // an id is at most 130 characters
            char* id = stackalloc char[Size];
            uint length = Size;
            return Win32.GetApplicationUserModelId(process, ref length, id) == 0 ? new string(id) : null;
        }
        finally { Win32.CloseHandle(process); }
    }

    /// <summary>
    /// A player's name as Windows shows it ("网易云音乐", "Microsoft Edge"), for Settings and the tray panel.
    /// <paramref name="path"/> is the file it ran from when the caller knows it; with "", a running
    /// player's is looked up. Kept per id, and worked out again when a different path is given.
    /// </summary>
    public static string Name(string id, string path)
    {
        if (Names.TryGetValue(id, out var known) && (path.Length == 0 || path == known.Path)) return known.Name;
        if (path.Length == 0) path = RunningPath(id) ?? "";
        string name = FindName(id, path);
        Names[id] = (path, name);
        return name;
    }

    static string FindName(string id, string path)
    {
        if (ListedName(id) is { Length: > 0 } listed) return listed;
        if (path.Length > 0 && PlayerId.AppsFolderIds(path, Folders).Select(ListedName).FirstOrDefault(found => found != null) is { Length: > 0 } byPath)
            return byPath;
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                && Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(id)?.DisplayInfo?.DisplayName is { Length: > 0 } name)
                return name;
        }
        catch (Exception) { /* desktop apps often have no app info */ }
        if (id is "MSEdge") return "Microsoft Edge";
        if (id is "Chrome") return "Google Chrome";
        try
        {
            if (path.Length > 0 && FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() is { Length: > 0 } description) return description;
        }
        catch (Exception) { /* the file is gone, or can't be read */ }
        return Path.GetFileNameWithoutExtension(id.Split('!')[^1]);
    }

    /// <summary>The name Windows lists an app under, by its id; null when it lists no such app.</summary>
    static string? ListedName(string id)
    {
        // With no id the name is the apps folder's own.
        if (id.Length == 0 || Win32.SHParseDisplayName(AppsFolder + id, 0, out nint item, 0, out _) != 0) return null;
        try
        {
            if (Win32.SHGetNameFromIDList(item, 0, out nint name) != 0) return ""; // 0: the name as shown
            try { return Marshal.PtrToStringUni(name) ?? ""; }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        finally { Marshal.FreeCoTaskMem(item); }
    }
}
