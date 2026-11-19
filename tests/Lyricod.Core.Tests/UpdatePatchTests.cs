using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Lyricod.Core.Tests;

public sealed class UpdatePatchTests : IDisposable
{
    readonly string _base = Path.Combine(Path.GetTempPath(), "lyricod-patch-tests-" + Guid.NewGuid());
    static string AppFile => typeof(UpdateCheck).Assembly.Location;
    static Version To => Version.Parse(FileVersionInfo.GetVersionInfo(AppFile).FileVersion!) is var v ? new(v.Major, v.Minor, v.Build) : null!;
    static readonly Version From = new(0, 0, 1);
    static readonly string Full = new('a', 64);
    /// <summary>The installed copy, the new version's folder, and the patch between them.</summary>
    string Root => Path.Combine(_base, "installed");
    string Next => Path.Combine(_base, "next");
    string Patch => Path.Combine(_base, "patch.zip");

    public UpdatePatchTests()
    {
        foreach (string folder in new[] { Root, Next })
        {
            Directory.CreateDirectory(Path.Combine(folder, "app", "sub"));
            File.Copy(AppFile, Path.Combine(folder, "Lyricod.exe"));
            File.Copy(AppFile, Path.Combine(folder, "app", "Lyricod.exe"));
            File.WriteAllText(Path.Combine(folder, "app", "keep.dll"), "the same in both");
            File.WriteAllText(Path.Combine(folder, "app", "sub", "kept too.txt"), "also the same");
        }
        File.WriteAllText(Path.Combine(Root, "README.txt"), "old read me");
        File.WriteAllText(Path.Combine(Root, "app", "changed.dll"), "old code");
        File.WriteAllText(Path.Combine(Root, "app", "gone.dll"), "no longer shipped");
        File.WriteAllText(Path.Combine(Root, "app", "the user's own.txt"), "not Lyricod's");
        File.WriteAllText(Path.Combine(Next, "README.txt"), "new read me");
        File.WriteAllText(Path.Combine(Next, "app", "changed.dll"), "new code, and more of it");
        File.WriteAllText(Path.Combine(Next, "app", "sub", "added.txt"), "new in this version");
    }

    PatchManifest Make() => UpdatePatch.Create(Root, Next, Patch, From, To, "x64", Full);

    Task Stage(Version? from = null, Version? to = null, string arch = "x64", string? full = null, CancellationToken? stop = null) =>
        UpdatePatch.StageAsync(Patch, Root, from ?? From, to ?? To, arch, full ?? Full, stop ?? TestContext.Current.CancellationToken);

    void AssertNoStage()
    {
        Assert.False(Directory.Exists(Path.Combine(Root, "app.new")));
        Assert.False(File.Exists(Path.Combine(Root, "Lyricod.exe.new")));
        Assert.False(File.Exists(Path.Combine(Root, "README.txt.new")));
    }

    static Dictionary<string, string> Tree(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(folder, file).Replace('\\', '/'), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));

    [Fact]
    public async Task ThePatchCarriesOnlyWhatChangedAndMakesTheNewVersionExactly()
    {
        var manifest = Make();
        Assert.Equal(["Lyricod.exe", "README.txt", "app/Lyricod.exe", "app/changed.dll", "app/keep.dll", "app/sub/added.txt", "app/sub/kept too.txt"],
            manifest.Files.Select(f => f.Path));
        Assert.Equal(["README.txt", "app/changed.dll", "app/sub/added.txt"], manifest.Files.Where(f => f.New).Select(f => f.Path));
        using (var zip = ZipFile.OpenRead(Patch))
            Assert.Equal(["patch.json", "files/README.txt", "files/app/changed.dll", "files/app/sub/added.txt"], zip.Entries.Select(e => e.FullName));

        Directory.CreateDirectory(Path.Combine(Root, "app.new"));
        File.WriteAllText(Path.Combine(Root, "app.new", "stale.txt"), "an earlier stage");
        await Stage();
        // What the whole zip would have unpacked: every file of the new version, and nothing else.
        Assert.Equal(Tree(Path.Combine(Next, "app")), Tree(Path.Combine(Root, "app.new")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Next, "Lyricod.exe")), File.ReadAllBytes(Path.Combine(Root, "Lyricod.exe.new")));
        Assert.Equal("new read me", File.ReadAllText(Path.Combine(Root, "README.txt.new")));
        // The installed copy is only read.
        Assert.Equal("old code", File.ReadAllText(Path.Combine(Root, "app", "changed.dll")));
        Assert.True(File.Exists(Path.Combine(Root, "app", "gone.dll")));
    }

    [Fact]
    public void TheSameFoldersAlwaysMakeTheSamePatch()
    {
        Make();
        byte[] first = File.ReadAllBytes(Patch);
        File.Delete(Patch);
        File.SetLastWriteTimeUtc(Path.Combine(Next, "app", "changed.dll"), new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        Make();
        Assert.Equal(first, File.ReadAllBytes(Patch));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    public async Task AnInstalledFileThatIsNotAsReleasedStopsThePatch(string fault)
    {
        Make();
        string kept = Path.Combine(Root, "app", "keep.dll");
        if (fault == "changed") File.WriteAllText(kept, "the same in bot!");
        else File.Delete(kept);
        if (fault == "changed") Assert.Contains("installed app/keep.dll", (await Assert.ThrowsAsync<InvalidDataException>(() => Stage())).Message);
        else await Assert.ThrowsAsync<FileNotFoundException>(() => Stage());
        AssertNoStage();
    }

    [Theory]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("arch")]
    [InlineData("full")]
    [InlineData("version")]
    public async Task APatchForAnotherStepOrBuildIsRefused(string fault)
    {
        Make();
        await Assert.ThrowsAsync<InvalidDataException>(() => fault switch
        {
            "from" => Stage(from: new(0, 0, 2)),
            "to" => Stage(to: new(To.Major, To.Minor, To.Build + 1)),
            "arch" => Stage(arch: "arm64"),
            "full" => Stage(full: new string('b', 64)),
            // The files are right by their hashes, but the program isn't the version the release says.
            _ => UpdatePatch.StageAsync(Patch, Root, From, To, "x64", Full, TestContext.Current.CancellationToken, _ => "9.9.9.0"),
        });
        AssertNoStage();
    }

    [Theory]
    [InlineData("other bytes")]
    [InlineData("longer")]
    [InlineData("left out")]
    [InlineData("extra")]
    [InlineData("twice")]
    [InlineData("no list")]
    public async Task APatchThatIsNotWhatItsListSaysIsRefused(string fault)
    {
        Make();
        Rewrite(fault);
        await Assert.ThrowsAsync<InvalidDataException>(() => Stage());
        AssertNoStage();
    }

    /// <summary>Copies the patch entry by entry, with one thing wrong.</summary>
    void Rewrite(string fault)
    {
        string good = Patch + ".good";
        File.Move(Patch, good);
        using var source = ZipFile.OpenRead(good);
        using var target = ZipFile.Open(Patch, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            if (fault == "no list" && entry.FullName == "patch.json") continue;
            if (fault == "left out" && entry.FullName == "files/app/changed.dll") continue;
            using var data = new MemoryStream();
            using (var stream = entry.Open()) stream.CopyTo(data);
            byte[] bytes = data.ToArray();
            if (entry.FullName == "files/app/changed.dll")
            {
                if (fault == "other bytes") bytes[0] ^= 1;
                if (fault == "longer") bytes = [.. bytes, 1];
            }
            for (int copy = 0; copy < (fault == "twice" && entry.FullName == "files/README.txt" ? 2 : 1); copy++)
                using (var stream = target.CreateEntry(entry.FullName).Open()) stream.Write(bytes);
        }
        if (fault == "extra") using (var stream = target.CreateEntry("files/app/unlisted.dll").Open()) stream.Write("not on the list"u8);
    }

    [Fact]
    public async Task CancelLeavesNoStage()
    {
        Make();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Stage(stop: stop.Token));
        AssertNoStage();
    }

    static PatchManifest Manifest(params PatchFile[] files) => new(From, new(1, 2, 3), "x64", Full,
        [new("Lyricod.exe", 1, Full, false), new("README.txt", 1, Full, true), new("app/Lyricod.exe", 1, Full, false), .. files]);

    [Fact]
    public void TheListReadsBackAsWritten()
    {
        var manifest = Manifest(new PatchFile("app/sub/a b.dll", 123456789, new string('C', 64), true));
        var read = UpdatePatch.Read(UpdatePatch.Write(manifest));
        Assert.Equal((manifest.From, manifest.To, manifest.Arch, manifest.Full), (read.From, read.To, read.Arch, read.Full));
        Assert.Equal(manifest.Files, read.Files);
    }

    [Theory]
    [InlineData("app/../escape.txt")]
    [InlineData("../escape.txt")]
    [InlineData("/app/x.dll")]
    [InlineData("C:/x.dll")]
    [InlineData("app\\x.dll")]
    [InlineData("settings.json")]
    [InlineData("other/x.dll")]
    [InlineData("app/con.txt")]
    [InlineData("app/x.dll.")]
    [InlineData("app/LYRICOD.EXE")]
    [InlineData("")]
    public void AListWithAFileOutsideThePortableFolderOrTwiceIsRefused(string path) =>
        Assert.Throws<InvalidDataException>(() => UpdatePatch.Read(UpdatePatch.Write(Manifest(new PatchFile(path, 1, Full, true)))));

    [Theory]
    [InlineData("\"format\": 1", "\"format\": 2")]
    [InlineData("\"from\": \"0.0.1\"", "\"from\": \"1.2.3\"")]
    [InlineData("\"from\": \"0.0.1\"", "\"from\": \"2.0.0\"")]
    [InlineData("\"to\": \"1.2.3\"", "\"to\": \"1.2\"")]
    [InlineData("\"arch\": \"x64\"", "\"arch\": \"x86\"")]
    [InlineData("\"path\": \"README.txt\"", "\"path\": \"README.md\"")]
    [InlineData("\"size\": 1,", "\"size\": -1,")]
    [InlineData("\"size\": 1,", "\"size\": 2000000000,")]
    [InlineData("\"size\": 1,", "\"size\": \"1\",")]
    [InlineData("\"new\": true", "\"new\": 1")]
    [InlineData("\"files\": [", "\"files\": 5, \"other\": [")]
    [InlineData("{", "[{")]
    public void AListThatIsWrongOrIncompleteIsRefused(string find, string replace)
    {
        string json = UpdatePatch.Write(Manifest());
        Assert.Contains(find, json);
        Assert.Throws<InvalidDataException>(() => UpdatePatch.Read(json.Replace(find, replace)));
    }

    [Fact]
    public void ABadHashInTheListIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => UpdatePatch.Read(UpdatePatch.Write(Manifest(new PatchFile("app/x.dll", 1, "abc", true)))));
        Assert.Throws<InvalidDataException>(() => UpdatePatch.Read(UpdatePatch.Write(Manifest() with { Full = "xyz" })));
    }

    static AppRelease Release(params (string Name, long Size)[] assets) => new(new(1, 2, 3), new("https://github.com/Indarkdust/Lyricod-App/releases/tag/v1.2.3"))
    {
        Assets = [.. assets.Select(a => new UpdateAsset(a.Name, a.Size, new($"https://github.com/Indarkdust/Lyricod-App/releases/download/v1.2.3/{a.Name}"), Full))],
    };

    [Fact]
    public void ThePatchChosenIsTheOneFromTheRunningVersionForThePortableCopy()
    {
        var release = Release(("Lyricod-1.2.3-windows-x64.zip", 4000), ("Lyricod-1.2.3-windows-x64-setup.exe", 3000),
            ("Lyricod-1.2.3-windows-x64-patch-from-1.2.2.zip", 300), ("Lyricod-1.2.3-windows-x64-patch-from-1.2.1.zip", 400),
            ("Lyricod-1.2.3-windows-arm64-patch-from-1.2.2.zip", 500));
        Assert.Equal(300, UpdateFiles.SelectPatch(release, InstallKind.Portable, Architecture.X64, new(1, 2, 2))?.Size);
        Assert.Equal(400, UpdateFiles.SelectPatch(release, InstallKind.Portable, Architecture.X64, new(1, 2, 1))?.Size);
        Assert.Null(UpdateFiles.SelectPatch(release, InstallKind.Portable, Architecture.X64, new(1, 2, 0)));
        Assert.Null(UpdateFiles.SelectPatch(release, InstallKind.Installer, Architecture.X64, new(1, 2, 2)));
        // No whole zip for ARM64 to fall back on.
        Assert.Null(UpdateFiles.SelectPatch(release, InstallKind.Portable, Architecture.Arm64, new(1, 2, 2)));
        // A patch as big as the whole download is no use.
        var big = Release(("Lyricod-1.2.3-windows-x64.zip", 4000), ("Lyricod-1.2.3-windows-x64-patch-from-1.2.2.zip", 4000));
        Assert.Null(UpdateFiles.SelectPatch(big, InstallKind.Portable, Architecture.X64, new(1, 2, 2)));
        // The whole download is still chosen as before, never a patch.
        Assert.Equal("Lyricod-1.2.3-windows-x64.zip", UpdateFiles.SelectAsset(release, InstallKind.Portable, Architecture.X64)?.Name);
    }

    public void Dispose() { if (Directory.Exists(_base)) Directory.Delete(_base, true); }
}
