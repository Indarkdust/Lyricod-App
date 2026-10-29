using System.Text.Json.Serialization;

namespace Lyricod.Core;

/// <summary>Raw lyric text as returned by a source, before parsing. This is what's cached.
/// Field names match the macOS app's cache files.</summary>
public sealed class RawLyrics
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("lrc")] public string? Lrc { get; set; }
    [JsonPropertyName("translation")] public string? Translation { get; set; }
    [JsonPropertyName("yrc")] public string? Yrc { get; set; }
    /// <summary>QQ Music word-by-word lyrics (decrypted QRC).</summary>
    [JsonPropertyName("qrc")] public string? Qrc { get; set; }
    [JsonPropertyName("plain")] public string? Plain { get; set; }
    /// <summary>The source's own romanisation (NetEase's romaji LRC, QQ Music's decrypted romaji QRC).</summary>
    [JsonPropertyName("romanization")] public string? Romanization { get; set; }
    /// <summary>Older macOS cache entries only recorded a NetEase id; see <see cref="SongRef"/>.</summary>
    [JsonPropertyName("neteaseID")] public long? NeteaseId { get; set; }
    [JsonPropertyName("ref")] public LyricsRef? Ref { get; set; }
    // 0.1.0 saved a "wordsChecked" flag here; it's ignored now (see LyricsProvider.WordCheckDue).

    [JsonIgnore] public LyricsRef? SongRef => Ref ?? (NeteaseId is { } id ? LyricsRef.Netease(id) : null);

    public Lyrics? Parse()
    {
        Lyrics? l = null;
        if (!string.IsNullOrEmpty(Yrc)) l = LyricParser.ParseYrc(Yrc, Translation, Source);
        if (l == null && !string.IsNullOrEmpty(Qrc)) l = LyricParser.ParseQrc(Qrc, Translation, Source);
        if (l == null && !string.IsNullOrEmpty(Lrc)) l = LyricParser.ParseLrc(Lrc, Translation, Source);
        if (l == null && !string.IsNullOrEmpty(Plain)) l = LyricParser.PlainText(Plain, Source);
        if (l == null) return null;
        if (!string.IsNullOrEmpty(Romanization)) LyricParser.AttachPronunciation(Romanization, l);
        l.Ref = SongRef;
        // Some uploads are only credits ("作词: 无"); that's not lyrics.
        return l.Lines.Any(x => !x.IsCredit) ? l : null;
    }
}
