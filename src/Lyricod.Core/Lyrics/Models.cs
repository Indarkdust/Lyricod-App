using System.Text.Json.Serialization;

namespace Lyricod.Core;

/// <summary>One sung word (or syllable) with its timing, in seconds.</summary>
public sealed record LyricWord(double Start, double Duration, string Text)
{
    public double End => Start + Duration;

    /// <summary>0…1 fill progress of this word at time <paramref name="t"/>.</summary>
    public double Progress(double t) => Duration <= 0 ? (t >= Start ? 1 : 0) : Math.Clamp((t - Start) / Duration, 0, 1);
}

/// <summary>A language whose lyrics can get a pronunciation line.</summary>
public enum LyricLanguage { Japanese, Korean, Chinese }

/// <summary>How a line is said, in Latin letters: romaji, romanised Korean or pinyin.</summary>
/// <param name="Language">Null until <c>Romanizer.Fill</c> has looked at the line.</param>
public sealed record Pronunciation(string Text, LyricLanguage? Language = null);

public sealed record LyricLine
{
    /// <summary>Row index in its <see cref="Lyrics"/>.</summary>
    public int Id { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Present when the source has per-word timing (NetEase YRC, QQ QRC, enhanced LRC).</summary>
    public IReadOnlyList<LyricWord>? Words { get; init; }
    public string? Translation { get; init; }
    public Pronunciation? Pronunciation { get; init; }
    /// <summary>Songwriter / producer line ("作词: 周杰伦"), not sung.</summary>
    public bool IsCredit { get; init; }
}

public sealed class Lyrics
{
    public Lyrics(List<LyricLine> lines, string source, bool isSynced = true)
    {
        Lines = lines;
        Source = source;
        IsSynced = isSynced;
    }

    public List<LyricLine> Lines { get; set; }
    public string Source { get; set; }
    public bool IsSynced { get; set; }
    /// <summary>The song the lyrics came from, if known.</summary>
    public LyricsRef? Ref { get; set; }

    public bool IsWordSynced => Lines.Any(l => l.Words != null);

    /// <summary>Index of the line being sung at <paramref name="t"/>, or null before the first line.</summary>
    public int? LineIndex(double t)
    {
        if (!IsSynced || Lines.Count == 0 || t < Lines[0].Start) return null;
        int lo = 0, hi = Lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Lines[mid].Start <= t) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public Lyrics Copy() => new([.. Lines], Source, IsSynced) { Ref = Ref };
}

/// <summary>Which lyric source a song lives in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LyricsSource>))]
public enum LyricsSource
{
    [JsonStringEnumMemberName("netease")] Netease,
    [JsonStringEnumMemberName("qq")] Qq,
    [JsonStringEnumMemberName("lrclib")] Lrclib,
}

/// <summary>Where a set of lyrics lives: a song in one of the lyric sources.</summary>
/// <param name="Id">NetEase song id, QQ Music songmid, or LRCLIB record id.</param>
/// <param name="QqId">QQ Music's numeric song id, needed for word-by-word lyrics.</param>
public sealed record LyricsRef(
    [property: JsonPropertyName("source")] LyricsSource Source,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("qqID")] int? QqId = null)
{
    public static LyricsRef Netease(long id) => new(LyricsSource.Netease, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>The song a player is playing, as far as finding lyrics is concerned.</summary>
public sealed record Track
{
    public string TrackId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    /// <summary>Seconds; 0 when the player doesn't say.</summary>
    public double Duration { get; init; }
    /// <summary>QQ Music's ids for the exact recording, when known: lyrics are fetched for it instead of searched.</summary>
    public int? QqSongId { get; init; }
    public string? QqSongMid { get; init; }
    /// <summary>The artist's other names ("Crowd Lu" → 卢广仲), filled in by <c>LyricsProvider</c>.</summary>
    public IReadOnlyList<string> ArtistAliases { get; init; } = [];
    /// <summary>
    /// The song's other titles, learned from search results: YouTube Music shows 夢灯籠 as
    /// "Dream lantern", which NetEase lists only under its English version, translated 梦灯笼.
    /// </summary>
    public IReadOnlyList<string> TitleAliases { get; init; } = [];
}

/// <summary>A search result in Choose Lyrics.</summary>
/// <param name="TitleMatched">The title (or one of its other names) matches the playing one, rather than only the artist and length.</param>
public sealed record LyricsCandidate(LyricsRef Id, string Title, string Artists, string Album, double Duration, double Score, bool TitleMatched = true);
