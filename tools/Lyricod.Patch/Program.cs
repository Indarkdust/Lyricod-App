using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Lyricod.Core;

// Makes the patch between two portable zips of Lyricod, and proves it: applied to the old one the way
// the app applies it, it must give every file of the new one and nothing else.
//   dotnet run --project tools/Lyricod.Patch -- <old zip> <new zip> <output folder>
// The zips are named as released (Lyricod-1.2.3-windows-x64.zip; a test build's label is allowed).
// No patch is made from a version that wouldn't know what to do with one, and one more than half the
// size of the new zip is not kept: the whole download serves as well. Whatever happens, a patch is
// left in the output folder only if it was proved.
// Exit code 0 when a patch was made or rightly not made, 1 when something is wrong.
if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Lyricod.Patch <old zip> <new zip> <output folder>");
    return 1;
}
var (from, oldArch) = Named(args[0]);
var (to, arch) = Named(args[1]);
if (oldArch != arch) return Fail($"The zips are for {oldArch} and {arch}.");
if (to <= from) return Fail($"{to} isn't newer than {from}.");
if (!KnowsPatches(args[0]))
{
    Console.WriteLine($"No patch from {from.ToString(3)}: that version is from before patches and would never ask for one.");
    return 0;
}
string work = Path.Combine(Path.GetTempPath(), "lyricod-patch-" + Guid.NewGuid());
string? patch = null;
bool proved = false;
try
{
    string before = Path.Combine(work, "old"), after = Path.Combine(work, "new");
    ZipFile.ExtractToDirectory(args[0], before);
    ZipFile.ExtractToDirectory(args[1], after);
    string installed = Path.Combine(before, "Lyricod"), wanted = Path.Combine(after, "Lyricod");
    string full;
    using (var zip = File.OpenRead(args[1])) full = Convert.ToHexStringLower(SHA256.HashData(zip));
    Directory.CreateDirectory(args[2]);
    patch = Path.Combine(args[2], UpdatePatch.AssetName(to, arch, from));
    File.Delete(patch);
    var manifest = UpdatePatch.Create(installed, wanted, patch, from, to, arch, full);
    long size = new FileInfo(patch).Length, whole = new FileInfo(args[1]).Length;
    Console.WriteLine($"{Path.GetFileName(patch)}: {manifest.Files.Count(f => f.New)} of {manifest.Files.Count} files, {size:N0} bytes ({100.0 * size / whole:0.#}% of the whole zip)");

    // Only Windows reads a Windows program's version; elsewhere that one check is left to the release workflow.
    Func<string, string?>? version = OperatingSystem.IsWindows() ? null : _ => to.ToString(3) + ".0";
    if (version != null) Console.WriteLine("Not on Windows: the program's version isn't read.");
    UpdatePatch.StageAsync(patch, installed, from, to, arch, full, CancellationToken.None, version).GetAwaiter().GetResult();
    var made = Tree(Path.Combine(installed, "app.new")).Select(f => ("app/" + f.Path, f.Hash))
        .Append(("Lyricod.exe", Hash(Path.Combine(installed, "Lyricod.exe.new")))).Append(("README.txt", Hash(Path.Combine(installed, "README.txt.new"))));
    var expected = Tree(wanted);
    var wrong = made.Except(expected).Concat(expected.Except(made)).Select(f => f.Item1).Distinct().ToList();
    if (wrong.Count > 0) return Fail("The patch doesn't make the new version: " + string.Join(", ", wrong.Take(10)));
    Console.WriteLine("Applied to the old zip, it makes the new one exactly.");
    if (size * 2 > whole) Console.WriteLine("Left out: more than half the whole zip.");
    else proved = true;
    return 0;
}
catch (Exception e) { return Fail(e.Message); }
finally
{
    // Half-made, unproved or too big: the release publishes whatever patch it finds here.
    if (!proved && patch != null)
    {
        try { File.Delete(patch); }
        catch (Exception) { }
    }
    try { Directory.Delete(work, true); }
    catch (Exception) { }
}

// A version knows patches if its own code names the list every patch carries.
static bool KnowsPatches(string zip)
{
    using var archive = ZipFile.OpenRead(zip);
    var core = archive.GetEntry("Lyricod/app/Lyricod.Core.dll") ?? throw new InvalidDataException($"{Path.GetFileName(zip)} has no Lyricod.Core.dll.");
    using var source = core.Open();
    using var bytes = new MemoryStream();
    source.CopyTo(bytes);
    return bytes.GetBuffer().AsSpan(0, (int)bytes.Length).IndexOf(Encoding.Unicode.GetBytes(UpdatePatch.ManifestName)) >= 0;
}

static (Version Version, string Arch) Named(string zip)
{
    var name = Regex.Match(Path.GetFileName(zip), @"^Lyricod-(\d+\.\d+\.\d+)(?:-[a-z0-9.]+)?-windows-(x64|arm64)\.zip$");
    if (!name.Success) throw new ArgumentException($"{Path.GetFileName(zip)} isn't named like a portable zip.");
    return (UpdateCheck.ParseVersion(name.Groups[1].Value)!, name.Groups[2].Value);
}

static IEnumerable<(string Path, string Hash)> Tree(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
    .Select(file => (Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/'), Hash(file)));

static string Hash(string path)
{
    using var file = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(file));
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
