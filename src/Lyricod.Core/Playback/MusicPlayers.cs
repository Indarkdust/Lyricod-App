namespace Lyricod.Core;

/// <summary>Players whose media sessions are normally songs.</summary>
public static class MusicPlayers
{
    static readonly string[] Executables =
    [
        "cloudmusic.exe", "QQMusic.exe", "Spotify.exe", "AppleMusic.exe", "wmplayer.exe",
        "KuGou.exe", "Kuwo.exe", "KwMusic.exe", "foobar2000.exe", "MusicBee.exe", "AIMP.exe",
        "TIDAL.exe", "Amazon Music.exe", "Deezer.exe", "lx-music-desktop.exe", "LX Music.exe",
        "YesPlayMusic.exe", "Cider.exe",
    ];

    static readonly string[] PackagePrefixes =
    [
        "AppleInc.AppleMusicWin_", "Microsoft.ZuneMusic_", "SpotifyAB.SpotifyMusic_",
        "AmazonMobileLLC.AmazonMusic_", "Deezer.62021768415AF_",
    ];

    /// <summary>NetEase Cloud Music, which only reports its position with a plugin (InfLink-rs).</summary>
    public static bool IsNetease(string? appId) =>
        !string.IsNullOrWhiteSpace(appId) && appId.Replace('\\', '/').Split('/')[^1].Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsMusicPlayer(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        if (PackagePrefixes.Any(prefix => appId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        // Windows paths also work when Core runs on another OS.
        var name = appId.Replace('\\', '/').Split('/')[^1];
        return Executables.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
