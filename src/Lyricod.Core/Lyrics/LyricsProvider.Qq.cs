using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Lyricod.Core;

public sealed partial class LyricsProvider
{
    static HttpRequestMessage QqRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Referrer = new Uri("https://y.qq.com/");
        return req;
    }

    record QqSong(string Mid, int? Id, string Title, List<string> Artists, string Album, double Duration, string AlbumMid);

    /// <summary>A QQ Music song's details, found by title and artist.</summary>
    public sealed record QqSongDetails(string Mid, int? Id, string Album, string AlbumMid, double Duration);

    /// <summary>
    /// QQ Music search results scored against <paramref name="track"/>, best first. The current
    /// search API refuses bursts of requests (code 2001), so the older one backs it up.
    /// </summary>
    public async Task<List<LyricsCandidate>> QqCandidates(string query, Track track, CancellationToken ct = default)
    {
        var songs = await QqSearch(query, ct);
        if (songs.Count == 0) songs = await QqLegacySearch(query, ct);
        return [.. songs.Select(s => new LyricsCandidate(new LyricsRef(LyricsSource.Qq, s.Mid, s.Id), s.Title,
                string.Join(", ", s.Artists), s.Album, s.Duration, TrackMatcher.Score([s.Title], s.Artists, s.Duration, track, s.Album),
                TrackMatcher.TitleMatches([s.Title], track)))
            .OrderByDescending(c => c.Score)];
    }

    /// <summary>
    /// Looks a song up on QQ Music when only its title and artist are known: its exact
    /// recording, album and length.
    /// </summary>
    public async Task<QqSongDetails?> QqLookup(string title, string artist, CancellationToken ct = default)
    {
        var songs = await QqSearch($"{title} {artist}", ct);
        if (songs.Count == 0) songs = await QqLegacySearch($"{title} {artist}", ct);
        var wantTitle = TrackMatcher.Normalize(title);
        // QQ's label joins singers with "/"; any of them appearing counts.
        var wantArtists = artist.Split('/').Select(TrackMatcher.Normalize).Where(s => s.Length > 0).ToList();
        var matches = songs.Where(s => TrackMatcher.Normalize(s.Title) == wantTitle && !s.Title.Contains("纯音乐") && !s.Title.Contains("伴奏")
            && s.Artists.Any(a => wantArtists.Any(w => TrackMatcher.Normalize(a).Contains(w) || w.Contains(TrackMatcher.Normalize(a))))).ToList();
        // Prefer a release on an album over stray uploads (which often lack word-by-word lyrics and a cover).
        var match = matches.FirstOrDefault(s => s.AlbumMid.Length > 0) ?? matches.FirstOrDefault();
        return match == null ? null : new QqSongDetails(match.Mid, match.Id, match.Album, match.AlbumMid, match.Duration);
    }

    async Task<List<QqSong>> QqSearch(string query, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["comm"] = new JsonObject { ["ct"] = "19", ["cv"] = "1873", ["uin"] = "0" },
            ["req"] = new JsonObject
            {
                ["module"] = "music.search.SearchCgiService", ["method"] = "DoSearchForQQMusicDesktop",
                ["param"] = new JsonObject { ["query"] = query, ["num_per_page"] = 20, ["page_num"] = 1, ["search_type"] = 0 },
            },
        };
        var req = QqRequest(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        var res = await GetJson(req, ct);
        return [.. Arr(Field(res, "req", "data", "body", "song", "list")).Select(s => Str(Field(s, "mid")) is { } mid && Str(Field(s, "name")) is { } name
                ? new QqSong(mid, (int?)Long(Field(s, "id")), name, Arr(Field(s, "singer")).Select(x => Str(Field(x, "name"))).Where(x => x != null).Select(x => x!).ToList(),
                    Str(Field(s, "album", "name")) ?? "", Num(Field(s, "interval")) ?? 0, Str(Field(s, "album", "mid")) ?? "")
                : null).Where(s => s != null).Select(s => s!)];
    }

    async Task<List<QqSong>> QqLegacySearch(string query, CancellationToken ct)
    {
        var res = await GetJson(QqRequest(HttpMethod.Get, "https://shc.y.qq.com/soso/fcgi-bin/search_for_qq_cp?" +
            Query(("w", query), ("format", "json"), ("n", "20"))), ct);
        return [.. Arr(Field(res, "data", "song", "list")).Select(s => Str(Field(s, "songmid")) is { } mid && Str(Field(s, "songname")) is { } name
                ? new QqSong(mid, (int?)Long(Field(s, "songid")), name, Arr(Field(s, "singer")).Select(x => Str(Field(x, "name"))).Where(x => x != null).Select(x => x!).ToList(),
                    Str(Field(s, "albumname")) ?? "", Num(Field(s, "interval")) ?? 0, Str(Field(s, "albummid")) ?? "")
                : null).Where(s => s != null).Select(s => s!)];
    }

    public async Task<RawLyrics?> FetchQq(string mid, CancellationToken ct = default)
    {
        var res = await GetJson(QqRequest(HttpMethod.Get, "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?" +
            Query(("songmid", mid), ("format", "json"), ("nobase64", "1"))), ct);
        if (Str(Field(res, "lyric")) is not { Length: > 0 } lyric) return null;
        var trans = Str(Field(res, "trans")) is { } t ? LyricParser.UnescapeHtml(t) : null;
        return new RawLyrics
        {
            Source = "QQ Music", Lrc = LyricParser.UnescapeHtml(lyric), Translation = string.IsNullOrEmpty(trans) ? null : trans,
            Ref = new LyricsRef(LyricsSource.Qq, mid),
        };
    }

    /// <summary>
    /// Word-by-word QRC for a numeric QQ song id. The response is XML (inside an HTML
    /// comment) with hex-encrypted original and romaji; the translation is usually plain
    /// LRC, sometimes encrypted too.
    /// </summary>
    public async Task<RawLyrics?> FetchQrc(int? id, CancellationToken ct = default)
    {
        if (id == null) return null;
        var xml = await GetText(QqRequest(HttpMethod.Get, "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg?" +
            Query(("version", "15"), ("miniversion", "82"), ("lrctype", "4"), ("musicid", id.Value.ToString(CultureInfo.InvariantCulture)))), ct);
        if (xml == null || Cdata("content", xml) is not { } hex || QrcDecrypter.Decrypt(hex) is not { } qrc
            || LyricParser.ParseQrc(qrc, null, "QQ Music") == null) return null;
        var trans = Cdata("contentts", xml);
        if (trans != null && !trans.Contains('[') && QrcDecrypter.Decrypt(trans) is { } plain) trans = plain;
        if (trans != null && !trans.Contains('[')) trans = null;
        var roma = Cdata("contentroma", xml) is { } r ? QrcDecrypter.Decrypt(r) : null;
        return new RawLyrics { Source = "QQ Music", Translation = trans == null ? null : LyricParser.UnescapeHtml(trans), Qrc = qrc, Romanization = roma };
    }

    /// <summary>The CDATA text of <c>&lt;tag …&gt;</c>, or null if empty or missing.</summary>
    static string? Cdata(string tag, string xml)
    {
        int open = xml.IndexOf($"<{tag} ", StringComparison.Ordinal);
        if (open < 0) return null;
        int start = xml.IndexOf("<![CDATA[", open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += "<![CDATA[".Length;
        int end = xml.IndexOf("]]>", start, StringComparison.Ordinal);
        if (end < 0) return null;
        var text = xml[start..end].Trim();
        return text.Length == 0 ? null : text;
    }

    /// <param name="minScore">How well the best search result must match: 3 takes a likely
    /// match, 7 needs title, artist and length to agree.</param>
    async Task<RawLyrics?> FromQq(Track track, bool requireTitle, CancellationToken ct, double minScore = 3, LyricsSearch? search = null)
    {
        search ??= new LyricsSearch(isMusicPlayer: true);
        // Only a music player can identify a recording without a scored search.
        if (search.IsMusicPlayer && track.QqSongMid is { } mid) return await FetchQqSong(mid, track.QqSongId, ct);
        var bare = TrackMatcher.BareTitle(track.Title);
        var native = track.ArtistAliases.FirstOrDefault(a => a.Any(c => c > '⺀'));
        var queries = new List<string> { $"{bare} {track.Artist}", bare };
        if (native != null) queries.AddRange([$"{bare} {native}", native]);
        LyricsCandidate? best = null;
        foreach (var query in queries)
        {
            foreach (var c in search.Observe(await QqCandidates(query, track, ct)))
                if ((c.TitleMatched || !requireTitle) && c.Score > (best?.Score ?? 0)) best = c;
            if (best is { Score: >= 7 }) break;
        }
        if (best == null || best.Score < minScore || !search.Accepts(best.Score, best.TitleMatched)) return null;
        return (await FetchQqSong(best.Id.Id, best.Id.QqId, ct))?.Matched(best.Score, best.TitleMatched);
    }

    /// <summary>One QQ Music song's lyrics: word-by-word QRC when there is one, keeping the line LRC's translation if QRC has none.</summary>
    public async Task<RawLyrics?> FetchQqSong(string mid, int? id, CancellationToken ct = default)
    {
        var lrcTask = FetchQq(mid, ct);
        var qrcTask = FetchQrc(id, ct);
        var lrc = await lrcTask;
        var songRef = new LyricsRef(LyricsSource.Qq, mid, id);
        if (await qrcTask is { } qrc && qrc.Parse() != null)
        {
            qrc.Translation ??= lrc?.Translation;
            qrc.Ref = songRef;
            return qrc;
        }
        if (lrc == null || lrc.Parse() == null) return null;
        lrc.Ref = songRef;
        return lrc;
    }

    /// <summary>
    /// QQ Music's own lyrics for a track, whatever the display source is: when following
    /// QQ Music, its highlighted line is matched against these.
    /// </summary>
    public async Task<Lyrics?> QqReference(Track track, bool ignoreCache = false, CancellationToken ct = default)
    {
        // Keyed by QQ's song id when known, so each recording gets its own lyrics.
        var key = track.QqSongMid is { } m ? "mid-" + m : CacheKey(track);
        var file = CacheFile("qq3-" + key);
        if (!ignoreCache && ReadCache(file) is { } raw && raw.Parse() is { } parsed)
        {
            // Saved without word-by-word lyrics: the download may just have failed (QQ throttles),
            // so try again, at most once a day (the file's date marks the last try).
            var saved = File.GetLastWriteTimeUtc(file);
            if (raw.Qrc == null && raw.SongRef?.QqId is { } qid && DateTime.UtcNow - saved > TimeSpan.FromHours(24))
            {
                if (await FetchQrc(qid, ct) is { } upgrade && upgrade.Parse() != null)
                {
                    raw.Qrc = upgrade.Qrc;
                    raw.Translation ??= upgrade.Translation;
                    raw.Romanization ??= upgrade.Romanization;
                    WriteCache(file, raw);
                    return raw.Parse() ?? parsed;
                }
                try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch (IOException) { }
            }
            return parsed;
        }
        var fresh = await FromQq(track.QqSongMid == null ? await WithAliases(track, ct) : track, requireTitle: true, ct);
        if (fresh?.Parse() is not { } result) return null;
        WriteCache(file, fresh);
        return result;
    }
}
