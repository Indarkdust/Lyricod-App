using System.Runtime.InteropServices;
using System.Text.Json;

namespace Lyricod.Core.Tests;

public class UpdateFilesTests
{
    [Theory]
    [InlineData("app", true, true, InstallKind.Installer)]
    [InlineData("Lyricod", true, false, InstallKind.Installer)]
    [InlineData("APP", false, true, InstallKind.Portable)]
    [InlineData("app", false, false, InstallKind.Unknown)]
    [InlineData("publish", false, true, InstallKind.Unknown)]
    public void DetectsLayout(string name, bool uninstall, bool launcher, InstallKind expected) =>
        Assert.Equal(expected, UpdateFiles.DetectKind(name, uninstall, launcher));

    [Theory]
    [InlineData("https://github.com/Indarkdust/Lyricod-App/releases/download/v1.2.3/a.zip", true)]
    [InlineData("http://github.com/Indarkdust/Lyricod-App/releases/download/v1.2.3/a.zip", false)]
    [InlineData("https://github.com.evil.test/Indarkdust/Lyricod-App/releases/download/a", false)]
    [InlineData("https://github.com:444/Indarkdust/Lyricod-App/releases/download/a", false)]
    [InlineData("https://github.com/other/Lyricod-App/releases/download/a", false)]
    [InlineData("https://github.com/Indarkdust/Lyricod-App/releases/download/../../evil", false)]
    [InlineData("https://user@github.com/Indarkdust/Lyricod-App/releases/download/a", false)]
    [InlineData("https://github.com/Indarkdust/Lyricod-App/releases/download/a#fragment", false)]
    [InlineData("http://vm.test:8000/asset.zip", false)]
    public void RestrictsDownloadOrigin(string url, bool expected) => Assert.Equal(expected, UpdateFiles.IsAllowedUrl(new(url)));

    [Theory]
    [InlineData("http://vm.test:8000/a", true)]
    [InlineData("http://vm.test:8001/a", false)]
    [InlineData("https://vm.test:8000/a", false)]
    [InlineData("http://else.test:8000/a", false)]
    public void TestFeedAllowsOnlySameOrigin(string url, bool expected) =>
        Assert.Equal(expected, UpdateFiles.IsAllowedUrl(new(url), new("http://vm.test:8000/releases.json")));

    [Fact]
    public void ReadsAssetsAndSelectsExactKindAndArchitecture()
    {
        string[] names = ["Lyricod-1.2.3-windows-x64-setup.exe", "Lyricod-1.2.3-windows-arm64-setup.exe",
            "Lyricod-1.2.3-windows-x64.zip", "Lyricod-1.2.3-windows-arm64.zip"];
        var json = JsonSerializer.Serialize(new[] { new { tag_name = "v1.2.3", html_url = "https://github.com/release", draft = false, prerelease = false,
            assets = names.Select(name => new { name, size = 123, browser_download_url = "https://github.com/Indarkdust/Lyricod-App/releases/download/v1.2.3/" + name, digest = "sha256:" + new string('a', 64) }) } });
        var release = UpdateCheck.Evaluate(json, 200, "1.0.0").Release!;
        Assert.Equal(4, release.Assets.Count);
        Assert.Equal(names[0], UpdateFiles.SelectAsset(release, InstallKind.Installer, Architecture.X64)!.Name);
        Assert.Equal(names[1], UpdateFiles.SelectAsset(release, InstallKind.Installer, Architecture.Arm64)!.Name);
        Assert.Equal(names[2], UpdateFiles.SelectAsset(release, InstallKind.Portable, Architecture.X64)!.Name);
        Assert.Equal(names[3], UpdateFiles.SelectAsset(release, InstallKind.Portable, Architecture.Arm64)!.Name);
        Assert.Null(UpdateFiles.SelectAsset(release, InstallKind.Unknown, Architecture.X64));
        Assert.Null(UpdateFiles.SelectAsset(release, InstallKind.Portable, Architecture.X86));
        Assert.Null(UpdateFiles.SelectAsset(release with { Assets = [release.Assets[0] with { Sha256 = null }] }, InstallKind.Installer, Architecture.X64));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sha256:abc")]
    [InlineData("md5:0123456789abcdef")]
    [InlineData("sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void BadDigestKeepsReleaseButCannotInstall(string? digest)
    {
        var json = JsonSerializer.Serialize(new[] { new { tag_name = "v1.2.3", html_url = "https://github.com/release", draft = false, prerelease = false,
            assets = new[] { new { name = "Lyricod-1.2.3-windows-x64.zip", size = 123, browser_download_url = "https://github.com/Indarkdust/Lyricod-App/releases/download/v1.2.3/a.zip", digest } } } });
        var result = UpdateCheck.Evaluate(json, 200, "1.0.0");
        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Null(UpdateFiles.SelectAsset(result.Release!, InstallKind.Portable, Architecture.X64));
    }

    [Theory]
    [InlineData("Lyricod/app/Lyricod.exe", "app.new/Lyricod.exe")]
    [InlineData("Lyricod\\app\\Assets\\a.png", "app.new/Assets/a.png")]
    [InlineData("Lyricod/app/", "app.new")]
    [InlineData("Lyricod/Lyricod.exe", "Lyricod.exe.new")]
    [InlineData("Lyricod/README.txt", "README.txt.new")]
    [InlineData("Lyricod/app/../../bad", null)]
    [InlineData("/Lyricod/app/bad", null)]
    [InlineData("C:\\Lyricod\\app\\bad", null)]
    [InlineData("Other/app/bad", null)]
    [InlineData("Lyricod/settings.json", null)]
    [InlineData("Lyricod/app/dir/../bad", null)]
    [InlineData("Lyricod/app/foo:bar", null)]
    [InlineData("Lyricod/app/dir. /bad", null)]
    [InlineData("Lyricod/app/CON.txt", null)]
    [InlineData("Lyricod/app//bad", null)]
    public void MapsOnlySafePayloadEntries(string entry, string? expected) =>
        Assert.Equal(expected?.Replace('/', Path.DirectorySeparatorChar), UpdateFiles.StagedPath(entry));

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3.99", true)]
    [InlineData("1.2.4.0", false)]
    [InlineData(null, false)]
    public void IgnoresFourthVersionPart(string? fileVersion, bool expected) =>
        Assert.Equal(expected, PortableUpdate.MatchesVersion(fileVersion, new(1, 2, 3)));
}
