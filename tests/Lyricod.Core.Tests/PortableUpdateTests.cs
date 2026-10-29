using System.Diagnostics;
using System.IO.Compression;

namespace Lyricod.Core.Tests;

public sealed class PortableUpdateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "lyricod-stage-tests-" + Guid.NewGuid());
    string Zip => Path.Combine(_root, "update.zip");
    static string AppFile => typeof(UpdateCheck).Assembly.Location;
    static Version AppVersion => Version.Parse(FileVersionInfo.GetVersionInfo(AppFile).FileVersion!);

    void Archive(bool duplicate = false, bool includeLauncher = true)
    {
        Directory.CreateDirectory(_root);
        using var zip = ZipFile.Open(Zip, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(AppFile, "Lyricod/app/Lyricod.exe");
        if (includeLauncher) zip.CreateEntryFromFile(AppFile, "Lyricod/Lyricod.exe");
        using (var writer = new StreamWriter(zip.CreateEntry("Lyricod/README.txt").Open())) writer.Write("read me");
        using (var writer = new StreamWriter(zip.CreateEntry("Lyricod/app/../../escape.txt").Open())) writer.Write("skip");
        using (var writer = new StreamWriter(zip.CreateEntry("other/settings.json").Open())) writer.Write("skip");
        if (duplicate) zip.CreateEntryFromFile(AppFile, "Lyricod/app/lyricod.exe");
    }

    [Fact]
    public async Task StagesAllPayloadFilesAndReplacesOldStage()
    {
        Archive();
        Directory.CreateDirectory(Path.Combine(_root, "app.new"));
        File.WriteAllText(Path.Combine(_root, "app.new", "stale.txt"), "old stage");
        await PortableUpdate.StageAsync(Zip, _root, AppVersion, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(_root, "app.new", "Lyricod.exe")));
        Assert.True(File.Exists(Path.Combine(_root, "Lyricod.exe.new")));
        Assert.Equal("read me", File.ReadAllText(Path.Combine(_root, "README.txt.new")));
        Assert.False(File.Exists(Path.Combine(_root, "app.new", "stale.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
        Assert.False(Directory.Exists(Path.Combine(_root, "other")));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task InvalidPayloadLeavesNoStage(string fault)
    {
        Archive(duplicate: fault == "duplicate", includeLauncher: fault != "missing");
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdate.StageAsync(Zip, _root,
            fault == "version" ? new(999, 0, 0) : AppVersion, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(_root, "app.new")));
        Assert.False(File.Exists(Path.Combine(_root, "Lyricod.exe.new")));
        Assert.False(File.Exists(Path.Combine(_root, "README.txt.new")));
    }

    [Fact]
    public async Task CancelLeavesNoStage()
    {
        Archive();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PortableUpdate.StageAsync(Zip, _root, AppVersion, stop.Token));
        Assert.False(Directory.Exists(Path.Combine(_root, "app.new")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
