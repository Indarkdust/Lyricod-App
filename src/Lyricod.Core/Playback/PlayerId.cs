namespace Lyricod.Core;

/// <summary>Where Windows keeps programs and itself, as paths.</summary>
public sealed record ProgramFolders(string ProgramFiles, string ProgramFilesX86, string System, string SystemX86, string Windows);

/// <summary>
/// What a player's app id (the one its media session reports) says about its program: "cloudmusic.exe" is
/// a file name, "Family!App" a packaged app, and anything else ("MSEdge") a name the app chose.
/// </summary>
public static class PlayerId
{
    /// <summary>The program's file name when the id is one (with or without its folder), else null.</summary>
    public static string? ExeName(string id)
    {
        string name = FileName(id);
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    // Windows paths also work when Core runs on another OS.
    static string FileName(string path) => path.Replace('/', '\\').Split('\\')[^1];

    /// <summary>
    /// The ids Windows may list a program under among its apps (shell:AppsFolder), likeliest first: the path
    /// below a known folder after that folder's id, then the path as it is. The first one Windows accepts is its id.
    /// </summary>
    public static List<string> AppsFolderIds(string path, ProgramFolders folders)
    {
        (string Id, string Folder)[] known =
        [
            ("{6D809377-6AF0-444B-8957-A3773F02200E}", folders.ProgramFiles),
            ("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", folders.ProgramFilesX86),
            ("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", folders.System),
            ("{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}", folders.SystemX86),
            ("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", folders.Windows),
        ];
        var ids = new List<string>();
        foreach (var (id, folder) in known)
        {
            string root = folder.TrimEnd('\\') + "\\";
            if (path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) ids.Add($"{id}\\{path[root.Length..]}");
        }
        ids.Add(path);
        return ids;
    }

    /// <summary>
    /// Whether the id is a web browser's: one of the app ids Windows has for its browsers, or the file name of
    /// one of their programs. <paramref name="commands"/> are the browsers' command lines, quoted or not.
    /// </summary>
    public static bool IsBrowser(string id, IEnumerable<string> browserIds, IEnumerable<string> commands)
    {
        if (browserIds.Contains(id, StringComparer.OrdinalIgnoreCase)) return true;
        return ExeName(id) is { } name && commands.Any(command =>
            FileName(command.StartsWith('"') ? command.Split('"')[1] : command.Trim()).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the player is open: it has a media session, or a process goes by the id's file name or by that
    /// of <paramref name="path"/> (where it last ran from, or ""), or, for a packaged app, a process has its id.
    /// </summary>
    /// <param name="packagedProcesses">The app ids of running packaged apps; only read for a packaged id.</param>
    public static bool IsRunning(string id, string path, IEnumerable<string> sessionIds, IEnumerable<string> processNames,
        IEnumerable<string> packagedProcesses)
    {
        if (sessionIds.Contains(id)) return true;
        string? byId = ExeName(id), byPath = path.Length > 0 ? FileName(path) : null;
        if (processNames.Any(name => name.Equals(byId, StringComparison.OrdinalIgnoreCase) || name.Equals(byPath, StringComparison.OrdinalIgnoreCase)))
            return true;
        return id.Contains('!') && packagedProcesses.Contains(id, StringComparer.OrdinalIgnoreCase);
    }
}
