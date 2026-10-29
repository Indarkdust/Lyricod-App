using System.Diagnostics;
using System.IO.Compression;

namespace Lyricod.Core;

public static class PortableUpdate
{
    static readonly string[] StagedNames = ["app.new", "Lyricod.exe.new", "README.txt.new"];

    public static void CheckWritable(string root)
    {
        string probe = Path.Combine(root, ".lyricod-update-" + Guid.NewGuid().ToString("N"));
        using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    public static bool MatchesVersion(string? fileVersion, Version version) =>
        Version.TryParse(fileVersion, out var found) && found.Major == version.Major
            && found.Minor == version.Minor && found.Build == version.Build;

    public static async Task StageAsync(string zip, string root, Version version, CancellationToken stop)
    {
        CheckWritable(root);
        foreach (string name in StagedNames) Delete(Path.Combine(root, name));
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                stop.ThrowIfCancellationRequested();
                if (UpdateFiles.StagedPath(entry.FullName) is not { } relative) continue;
                string target = Path.Combine(root, relative);
                // ZIP links are not payload files, even when their names look safe.
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("The update contains a link.");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }
                if (!seen.Add(relative)) throw new InvalidDataException("The update contains duplicate files.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var source = entry.Open();
                await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
                await source.CopyToAsync(destination, stop).ConfigureAwait(false);
            }
            string app = Path.Combine(root, "app.new", "Lyricod.exe");
            if (!File.Exists(app) || !File.Exists(Path.Combine(root, "Lyricod.exe.new")) || !File.Exists(Path.Combine(root, "README.txt.new"))
                || !MatchesVersion(FileVersionInfo.GetVersionInfo(app).FileVersion, version))
                throw new InvalidDataException("The update has missing files or the wrong version.");
            stop.ThrowIfCancellationRequested();
        }
        catch
        {
            foreach (string name in StagedNames) TryDelete(Path.Combine(root, name));
            throw;
        }
    }

    public static void Cleanup(string? portableRoot, string updatesDirectory, Version runningVersion)
    {
        // Startup cleanup must never prevent the app from opening.
        try
        {
            if (portableRoot != null)
            {
                foreach (string name in StagedNames.Concat(["app.old", "Lyricod.exe.old"])) TryDelete(Path.Combine(portableRoot, name));
                // The launcher renames an app.old it couldn't delete to app.old-<number>.
                foreach (string directory in Directory.EnumerateDirectories(portableRoot, "app.old-*")) TryDelete(directory);
            }
            if (Directory.Exists(updatesDirectory))
                foreach (string directory in Directory.EnumerateDirectories(updatesDirectory))
                    if (UpdateCheck.ParseVersion(Path.GetFileName(directory)) is { } version && version <= runningVersion) TryDelete(directory);
        }
        catch (Exception) { }
    }

    static void TryDelete(string path)
    {
        try { Delete(path); }
        catch (Exception) { }
    }

    static void Delete(string path)
    {
        if (!Path.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        // Never follow a leftover junction into another folder.
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                foreach (string child in Directory.EnumerateFileSystemEntries(path)) Delete(child);
            Directory.Delete(path);
        }
        else File.Delete(path);
    }
}
