using System.Runtime.InteropServices;

namespace Lyricod.Core;

public enum InstallKind { Unknown, Installer, Portable }

public static class UpdateFiles
{
    /// <summary>The launcher's layout: Lyricod runs from an "app" folder with the launcher one level up. A portable
    /// copy has it, and so does an installed one since Setup began laying down the same files.</summary>
    public static bool HasLauncher(string baseDirectoryName, bool hasParentLauncher) =>
        hasParentLauncher && baseDirectoryName.Equals("app", StringComparison.OrdinalIgnoreCase);

    /// <param name="hasUninstaller">Setup's uninstaller is beside Lyricod: an installed copy from before the launcher's layout.</param>
    /// <param name="hasParentUninstaller">It is beside the launcher: an installed copy with that layout.</param>
    public static InstallKind DetectKind(string baseDirectoryName, bool hasUninstaller, bool hasParentLauncher, bool hasParentUninstaller = false) =>
        hasUninstaller ? InstallKind.Installer : !HasLauncher(baseDirectoryName, hasParentLauncher) ? InstallKind.Unknown
            : hasParentUninstaller ? InstallKind.Installer : InstallKind.Portable;

    public static bool IsAllowedUrl(Uri url, Uri? testFeed = null)
    {
        if (!url.IsAbsoluteUri || url.UserInfo.Length != 0 || url.Fragment.Length != 0) return false;
        if (url.Scheme == "https" && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && url.IsDefaultPort
            && url.AbsolutePath.StartsWith("/Indarkdust/Lyricod-App/releases/download/", StringComparison.Ordinal)) return true;
        return testFeed is { IsAbsoluteUri: true } && (url.Scheme == "https" || url.Scheme == "http")
            && url.Scheme == testFeed.Scheme && url.Host.Equals(testFeed.Host, StringComparison.OrdinalIgnoreCase) && url.Port == testFeed.Port;
    }

    public static string? ArchName(Architecture architecture) =>
        architecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };

    /// <summary>
    /// The small download that takes exactly version <paramref name="running"/> to the release, for a copy with
    /// the launcher's layout. The release must also have this copy's whole download to fall back on, and the
    /// whole zip, which every patch is made from and tied to.
    /// </summary>
    public static UpdateAsset? SelectPatch(AppRelease release, InstallKind kind, bool hasLauncher, Architecture architecture, Version running, Uri? testFeed = null)
    {
        if (!hasLauncher || ArchName(architecture) is not { } arch || SelectAsset(release, kind, architecture, testFeed) is not { } whole
            || PatchBase(release, architecture, testFeed) == null) return null;
        string name = UpdatePatch.AssetName(release.Version, arch, running);
        return release.Assets.FirstOrDefault(a => a.Name == name && a.Size > 0 && a.Size < whole.Size && ValidHash(a.Sha256) && IsAllowedUrl(a.Url, testFeed));
    }

    /// <summary>The SHA-256 of the release's whole zip: what a patch says it was made from.</summary>
    public static string? PatchBase(AppRelease release, Architecture architecture, Uri? testFeed = null) =>
        SelectAsset(release, InstallKind.Portable, architecture, testFeed)?.Sha256;

    public static UpdateAsset? SelectAsset(AppRelease release, InstallKind kind, Architecture architecture, Uri? testFeed = null)
    {
        string? arch = ArchName(architecture);
        string? suffix = kind switch { InstallKind.Installer => "-setup.exe", InstallKind.Portable => ".zip", _ => null };
        if (arch == null || suffix == null) return null;
        string name = $"Lyricod-{release.Version.ToString(3)}-windows-{arch}{suffix}";
        return release.Assets.FirstOrDefault(a => a.Name == name && a.Size > 0 && ValidHash(a.Sha256) && IsAllowedUrl(a.Url, testFeed));
    }

    internal static bool ValidHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    // Return a relative path under the portable root, or skip the entry.
    public static string? StagedPath(string entryName)
    {
        string name = entryName.Replace('\\', '/');
        var parts = name.TrimEnd('/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ')
            || p.Any(c => c < 32 || ":<>\"|?*".Contains(c)) || IsDevice(p))) return null;
        if (parts.Length < 2 || parts[0] != "Lyricod") return null;
        if (parts.Length == 2 && !name.EndsWith('/'))
            return parts[1] switch { "Lyricod.exe" => "Lyricod.exe.new", "README.txt" => "README.txt.new", _ => null };
        if (parts[1] != "app") return null;
        return Path.Combine(new[] { "app.new" }.Concat(parts.Skip(2)).ToArray());
    }

    static bool IsDevice(string part)
    {
        string stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3]));
    }
}
