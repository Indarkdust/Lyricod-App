using System.Globalization;

namespace Lyricod.Core;

/// <summary>
/// Deliberately English, including numbers and source names, for bug reports. Grouped, a blank line
/// apart: Lyricod and Windows, then the player, the song, its lyrics, and the last error.
/// </summary>
public static class Diagnostics
{
    public static string Text(string version, string system, string architecture,
        string? playerName, string? playerId, bool isPlaying, Track? track, Lyrics? lyrics,
        bool searching, bool pinned, double songOffset, double offset, string? lastError = null, string? userProfile = null,
        bool? playerTimeline = null, (int Width, int Height)? cover = null, string? font = null, InstallKind? installKind = null,
        bool? musicPlayer = null, bool? treatedAsSong = null, double? bestMatchScore = null, bool? automaticLookupAllowed = null,
        long? coverBytes = null, bool? coverWhole = null, string? glass = null)
    {
        // Error messages and some player ids carry paths with the user's name in them.
        playerId = Redact(playerId, userProfile);
        lastError = Redact(lastError, userProfile);

        List<string> app = [$"Lyricod {version} on {system}, {architecture}"];
        if (installKind is { } installation) app.Add($"Install: {installation.ToString().ToLowerInvariant()}");
        if (font != null) app.Add($"Font: {Redact(font, userProfile)}");
        if (glass != null) app.Add($"Glass capture: {glass}");

        List<string> player = [];
        if (playerId != null)
        {
            string line = $"Player: {playerName ?? playerId} ({playerId}), {(isPlaying ? "playing" : "paused")}";
            // Without the player's position, Lyricod times the song itself, and seeking can put it out of step.
            if (playerTimeline is { } timeline) line += timeline ? ", position from the player" : ", no position from the player";
            player.Add(line);
        }
        if (musicPlayer is { } music) player.Add($"Music player: {(music ? "yes" : "no")}");
        if (automaticLookupAllowed is { } lookup) player.Add($"Automatic lyric lookup: {(lookup ? "allowed" : "blocked")}");

        List<string> song = [];
        if (track != null)
        {
            var details = new List<string>();
            if (double.IsFinite(track.Duration) && track.Duration > 0 && track.Duration < int.MaxValue)
            {
                int seconds = (int)Math.Round(track.Duration);
                details.Add(FormattableString.Invariant($"{seconds / 60}:{seconds % 60:00}"));
            }
            if ((track.QqSongMid ?? track.QqSongId?.ToString(CultureInfo.InvariantCulture)) is { } qq) details.Add($"QQ {qq}");
            song.Add($"Song: {track.Title} — {track.Artist}" + (details.Count > 0 ? $" ({string.Join(", ", details)})" : ""));
        }
        if (treatedAsSong is { } songState) song.Add($"Treated as a song: {(songState ? "yes" : "no")}");
        if (cover is { } c)
        {
            // How big the cover the player handed over is, and whether it came whole (see ArtworkCheck).
            string size = coverBytes is not { } n ? "" : n < 1024 * 1024 ? FormattableString.Invariant($", {Math.Max(1, n / 1024)} KB")
                : FormattableString.Invariant($", {n / (1024.0 * 1024):0.0} MB");
            string whole = coverWhole is not { } w ? "" : w ? ", whole" : ", cut short";
            song.Add(FormattableString.Invariant($"Cover: {c.Width}×{c.Height} px{size}{whole}"));
        }

        List<string> words = [];
        if (lyrics != null)
        {
            string Name(LyricsSource? s) => s switch
            {
                LyricsSource.Qq => "QQ Music", LyricsSource.Netease => "NetEase", LyricsSource.Lrclib => "LRCLIB", _ => lyrics.Source,
            };
            string source = Name(lyrics.Ref?.Source);
            string kind = !lyrics.IsSynced ? "unsynced" : lyrics.IsWordSynced ? "word by word" : "line by line";
            string line = $"Lyrics: {source}, {kind}, {lyrics.Lines.Count} line{(lyrics.Lines.Count == 1 ? "" : "s")}";
            if (lyrics.Ref is { } reference) line += $" ({reference.Source.ToString().ToLowerInvariant()}:{reference.Id})";
            words.Add(line);
            int count = lyrics.Lines.Count;
            int translated = lyrics.Lines.Count(l => !string.IsNullOrWhiteSpace(l.Translation));
            // The player's platform's translation over another platform's word-by-word lines.
            string from = lyrics.TranslationRef is { } tr && tr != lyrics.Ref ? $", from {Name(tr.Source)} ({tr.Source.ToString().ToLowerInvariant()}:{tr.Id})" : "";
            if (translated > 0) words.Add($"Translation: {translated} of {count} lines{from}");
            // Where each pronunciation came from: the source's romaji as given, the source's grouped by
            // word (Windows read the line the same), or Lyricod's own.
            var said = lyrics.Lines.Select(l => l.Pronunciation).OfType<Pronunciation>().Where(p => !string.IsNullOrWhiteSpace(p.Text)).ToList();
            if (said.Count > 0)
            {
                var origins = new[] { (PronunciationOrigin.Source, "source's as given"), (PronunciationOrigin.SourceByWord, "source's by word"), (PronunciationOrigin.Lyricod, "Lyricod's") }
                    .Select(o => (o.Item2, Lines: said.Count(p => p.Origin == o.Item1))).Where(o => o.Lines > 0).Select(o => $"{o.Item1} {o.Lines}");
                words.Add($"Pronunciation: {said.Count} of {count} lines ({string.Join(", ", origins)})");
            }
            words.Add($"Picked by hand: {(pinned ? "yes" : "no")}");
        }
        else if (track != null) words.Add(searching ? "Lyrics: searching" : "Lyrics: none");
        if (bestMatchScore != null || treatedAsSong != null)
            words.Add("Best match score: " + (bestMatchScore?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unverified"));
        string songTiming = track == null ? "" : $"this song {Seconds(songOffset)}, ";
        words.Add($"Timing: {songTiming}all songs {Seconds(offset)}");

        List<string> error = string.IsNullOrEmpty(lastError) ? [] : [$"Last error: {lastError}"];
        return string.Join("\n\n", new[] { app, player, song, words, error }.Where(g => g.Count > 0).Select(g => string.Join("\n", g)));
    }

    static string? Redact(string? text, string? userProfile) =>
        text == null || string.IsNullOrEmpty(userProfile) ? text
        : text.Replace(userProfile.TrimEnd('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

    static string Seconds(double value) => value.ToString("+0.00;−0.00;+0.00", CultureInfo.InvariantCulture) + " s";
}
