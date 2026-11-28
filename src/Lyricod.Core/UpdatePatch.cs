using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lyricod.Core;

/// <summary>One file of the new version, by its place in the portable folder (<c>app/Lyricod.dll</c>): its
/// size and SHA-256, and whether the patch carries it. A file it doesn't carry is the installed one, unchanged.</summary>
public sealed record PatchFile(string Path, long Size, string Sha256, bool New);

/// <summary>What a patch makes, file by file: every file of version <see cref="To"/>, for a copy that is
/// exactly version <see cref="From"/>. <see cref="Full"/> is the SHA-256 of the same release's whole zip,
/// which ties the patch to that build: replace the zip, and its old patches no longer apply.</summary>
public sealed record PatchManifest(Version From, Version To, string Arch, string Full, IReadOnlyList<PatchFile> Files);

/// <summary>A zip of the files changed since one version and a list of every file of the new one. Each staged
/// file is checked against the list, and anything amiss throws: the whole download is used instead.</summary>
public static class UpdatePatch
{
    /// <summary>The list inside every patch.</summary>
    public const string ManifestName = "patch.json";
    const string Carried = "files/";
    static readonly string[] Needed = ["Lyricod.exe", "README.txt", "app/Lyricod.exe"];

    /// <summary>Named to sort after a release's whole downloads, and not to be taken for one.</summary>
    public static string AssetName(Version to, string arch, Version from) =>
        $"patch-{from.ToString(3)}-to-{to.ToString(3)}-windows-{arch}.zip";

    public static PatchManifest Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("format").GetInt32() != 1) throw new InvalidDataException("An unknown kind of patch.");
            var from = UpdateCheck.ParseVersion(root.GetProperty("from").GetString() ?? "");
            var to = UpdateCheck.ParseVersion(root.GetProperty("to").GetString() ?? "");
            string arch = root.GetProperty("arch").GetString() ?? "", full = root.GetProperty("full").GetString() ?? "";
            if (from == null || to == null)
                throw new InvalidDataException("The patch doesn't say what it is for.");
            var files = new List<PatchFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in root.GetProperty("files").EnumerateArray())
            {
                string path = item.GetProperty("path").GetString() ?? "", hash = item.GetProperty("sha256").GetString() ?? "";
                long size = item.GetProperty("size").GetInt64();
                bool carried = item.TryGetProperty("new", out var flag) && flag.GetBoolean();
                // The same rules as for the whole zip's entries: inside the portable folder, and nowhere else.
                if (path.Contains('\\') || path.EndsWith('/') || UpdateFiles.StagedPath("Lyricod/" + path) is not { } staged || !seen.Add(staged)
                    || size < 0 || !UpdateFiles.ValidHash(hash))
                    throw new InvalidDataException("The patch lists a file it shouldn't.");
                files.Add(new(path, size, hash, carried));
            }
            if (Needed.Any(name => files.All(f => f.Path != name))) throw new InvalidDataException("The patch's list of files is incomplete.");
            return new(from, to, arch, full, files);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("The patch's list of files can't be read.", e);
        }
    }

    public static string Write(PatchManifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("format", 1);
            json.WriteString("from", manifest.From.ToString(3));
            json.WriteString("to", manifest.To.ToString(3));
            json.WriteString("arch", manifest.Arch);
            json.WriteString("full", manifest.Full);
            json.WriteStartArray("files");
            foreach (var file in manifest.Files)
            {
                json.WriteStartObject();
                json.WriteString("path", file.Path);
                json.WriteNumber("size", file.Size);
                json.WriteString("sha256", file.Sha256);
                if (file.New) json.WriteBoolean("new", true);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Stages the new version in <paramref name="root"/> (app.new, Lyricod.exe.new, README.txt.new, as
    /// <see cref="PortableUpdate.StageAsync"/> does from the whole zip) out of the copy installed there and
    /// the patch. The patch must be for exactly this step, on this build of the release.
    /// </summary>
    /// <param name="full">The SHA-256 of the release's whole zip, as GitHub gives it.</param>
    public static async Task StageAsync(string patch, string root, Version from, Version to, string arch, string full, CancellationToken stop)
    {
        PortableUpdate.CheckWritable(root);
        PortableUpdate.ClearStage(root);
        try
        {
            using var archive = ZipFile.OpenRead(patch);
            var manifest = Read(await ManifestTextAsync(archive, stop).ConfigureAwait(false));
            if (manifest.From != from || manifest.To != to || manifest.Arch != arch || !manifest.Full.Equals(full, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The patch is for another version, or another build of this one.");
            // Nothing in it but its list and the files the list says it carries.
            if (archive.Entries.Count != manifest.Files.Count(f => f.New) + 1) throw new InvalidDataException("The patch holds something its list doesn't mention.");
            foreach (var file in manifest.Files)
            {
                stop.ThrowIfCancellationRequested();
                string target = Path.Combine(root, UpdateFiles.StagedPath("Lyricod/" + file.Path)!);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Stream source;
                if (file.New)
                {
                    var entry = archive.GetEntry(Carried + file.Path) ?? throw new InvalidDataException($"The patch lacks {file.Path}.");
                    if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("The patch contains a link.");
                    source = entry.Open();
                }
                // The running Lyricod has these open; it only reads them, and so does this.
                else source = new FileStream(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (source) await CopyAsync(source, target, file, stop).ConfigureAwait(false);
            }
            stop.ThrowIfCancellationRequested();
        }
        catch
        {
            PortableUpdate.ClearStage(root, quietly: true);
            throw;
        }
    }

    static async Task<string> ManifestTextAsync(ZipArchive archive, CancellationToken stop)
    {
        var entry = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("The patch has no list of files.");
        using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        return await reader.ReadToEndAsync(stop).ConfigureAwait(false);
    }

    /// <summary>Writes the file as it is read, and only lets it stand if it is the size and hash the list gives.</summary>
    static async Task CopyAsync(Stream source, string target, PatchFile file, CancellationToken stop)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
        byte[] buffer = new byte[131072];
        long done = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, stop).ConfigureAwait(false)) > 0)
        {
            if ((done += count) > file.Size) break;
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), stop).ConfigureAwait(false);
        }
        if (done != file.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(file.New ? $"The patch's {file.Path} isn't the expected file." : $"The installed {file.Path} isn't the expected file.");
    }

    /// <summary>
    /// Makes the patch that turns the portable folder <paramref name="oldFolder"/> into <paramref name="newFolder"/>
    /// (each holds Lyricod.exe, README.txt and app). For the release workflow; the same list the app reads.
    /// </summary>
    public static PatchManifest Create(string oldFolder, string newFolder, string patch, Version from, Version to, string arch, string full)
    {
        var files = new List<PatchFile>();
        foreach (string path in Directory.EnumerateFiles(newFolder, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(newFolder, file).Replace(Path.DirectorySeparatorChar, '/')).Order(StringComparer.Ordinal))
        {
            string made = Path.Combine(newFolder, path), had = Path.Combine(oldFolder, path);
            string hash = Hash(made);
            files.Add(new(path, new FileInfo(made).Length, hash, !File.Exists(had) || Hash(had) != hash));
        }
        var manifest = new PatchManifest(from, to, arch, full.ToLowerInvariant(), files);
        string json = Write(manifest);
        // Read it back: what the app would refuse, the workflow should hear about now.
        Read(json);
        using var zip = ZipFile.Open(patch, ZipArchiveMode.Create);
        // One date for every entry, so the same two folders always make the same patch.
        var date = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var list = zip.CreateEntry(ManifestName, CompressionLevel.SmallestSize);
        list.LastWriteTime = date;
        using (var writer = new StreamWriter(list.Open(), new UTF8Encoding(false))) writer.Write(json);
        foreach (var file in files.Where(f => f.New))
        {
            var entry = zip.CreateEntry(Carried + file.Path, CompressionLevel.SmallestSize);
            entry.LastWriteTime = date;
            using var source = File.OpenRead(Path.Combine(newFolder, file.Path));
            using var target = entry.Open();
            source.CopyTo(target);
        }
        return manifest;
    }

    static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
}
