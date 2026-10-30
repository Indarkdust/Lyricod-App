using System.Globalization;

namespace Lyricod.Core;

public sealed partial class LyricsProvider
{
    static HttpRequestMessage NeteaseRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Referrer = new Uri("https://music.163.com/");
        return req;
    }

    async Task<RawLyrics?> FromNetease(Track track, bool requireTitle, CancellationToken ct, LyricsSearch search)
    {
        // Player titles carry suffixes like "(Theme Song from …)" that bury the real match,
        // so also search the bare title, then keep the best across queries.
        var bare = TrackMatcher.BareTitle(track.Title);
        // Also under the artist's other name, and that name alone: an English title may only
        // exist in Chinese, where the artist and an exact length still identify it.
        var native = track.ArtistAliases.FirstOrDefault(a => a.Any(c => c > '⺀'));
        var queries = new List<string> { $"{bare} {track.Artist}", $"{track.Title} {track.Artist}", bare };
        if (native != null) queries.AddRange([$"{bare} {native}", native]);
        queries = queries.Distinct().ToList();

        LyricsCandidate? best = null;
        foreach (var query in queries)
        {
            foreach (var c in await NeteaseSearch(query, track, requireTitle, ct, search))
                if (c.Score > (best?.Score ?? 0)) best = c;
            if (best is { Score: >= 7 }) break; // title + artist + duration all agree
        }
        if (best is not { Score: >= 3 } b || !search.Accepts(b.Score, b.TitleMatched)) return null;
        if (await Fetch(b.Id, ct) is { } raw && raw.Parse() != null) return raw.Matched(b.Score, b.TitleMatched);
        // The best match had no usable lyrics: try the next few good candidates.
        var others = (await NeteaseSearch(queries[0], track, requireTitle, ct, search)).Where(c => c.Id != b.Id && c.Score >= b.Score - 1.5 && search.Accepts(c.Score, c.TitleMatched)).Take(3);
        foreach (var c in others)
            if (await Fetch(c.Id, ct) is { } r && r.Parse() != null) return r.Matched(c.Score, c.TitleMatched);
        return null;
    }

    /// <summary>Lyrics for one NetEase song id.</summary>
    public async Task<RawLyrics?> FetchNetease(long id, CancellationToken ct = default)
    {
        var req = NeteaseRequest(HttpMethod.Get, "https://interface3.music.163.com/api/song/lyric/v1?" +
            Query(("id", id.ToString(CultureInfo.InvariantCulture)), ("lv", "1"), ("tv", "1"), ("yv", "1"), ("rv", "1")));
        req.Headers.Add("Cookie", "os=pc");
        var res = await GetJson(req, ct);
        if (res == null) return null;
        var roma = Str(Field(res, "romalrc", "lyric"));
        return new RawLyrics
        {
            Source = "NetEase", Lrc = Str(Field(res, "lrc", "lyric")), Translation = Str(Field(res, "tlyric", "lyric")),
            Yrc = Str(Field(res, "yrc", "lyric")), Romanization = string.IsNullOrEmpty(roma) ? null : roma, Ref = LyricsRef.Netease(id),
        };
    }

    async Task<List<LyricsCandidate>> NeteaseSearch(string query, Track track, bool requireTitle, CancellationToken ct, LyricsSearch search) =>
        search.Observe(await NeteaseCandidates(query, track, ct))
            .Where(c => c.TitleMatched || !requireTitle).ToList();

    /// <summary>NetEase search results scored against <paramref name="track"/>, best first.</summary>
    public async Task<List<LyricsCandidate>> NeteaseCandidates(string query, Track track, CancellationToken ct = default)
    {
        var res = await GetJson(NeteaseRequest(HttpMethod.Post,
            "https://music.163.com/api/cloudsearch/pc?" + Query(("s", query), ("type", "1"), ("limit", "20"))), ct);
        var songs = new List<(long Id, string Name, List<string> Titles, List<string> Translated, List<string> Artists, string Artist, string Album, double Duration)>();
        foreach (var song in Arr(Field(res, "result", "songs")))
        {
            if (Long(Field(song, "id")) is not { } id || Str(Field(song, "name")) is not { } name) continue;
            var ar = Arr(Field(song, "ar")).ToList();
            // Players often use romanised names ("Crowd Lu") where NetEase uses 卢广仲, so
            // aliases and translated titles count as matches too.
            var artists = ar.SelectMany(a => new[] { Str(Field(a, "name")) }.Concat(Arr(Field(a, "alias")).Select(Str)).Concat(Arr(Field(a, "tns")).Select(Str)))
                .Where(s => s != null).Select(s => s!).ToList();
            var translated = Arr(Field(song, "tns")).Select(Str).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
            var titles = new[] { name }.Concat(Arr(Field(song, "alia")).Select(Str).Where(s => s != null).Select(s => s!)).Concat(translated).ToList();
            songs.Add((id, name, titles, translated, artists, string.Join(", ", ar.Select(a => Str(Field(a, "name"))).Where(s => s != null)),
                Str(Field(song, "al", "name")) ?? "", (Num(Field(song, "dt")) ?? 0) / 1000));
        }
        // A result with the playing title and artist names the song in other ways too: its
        // translated titles find the other versions (YouTube Music's "Dream lantern" is the
        // Japanese 夢灯籠; NetEase lists that title only under the English version, as 梦灯笼).
        var wanted = TrackMatcher.WantedTitles(track);
        var learned = songs.Where(s => s.Titles.Concat(s.Titles.SelectMany(TrackMatcher.Bracketed)).Any(t => wanted.Contains(TrackMatcher.Normalize(t)))
                                       && TrackMatcher.ArtistMatches(s.Artists, track))
            .SelectMany(s => s.Translated).Where(t => !wanted.Contains(TrackMatcher.Normalize(t))).Distinct().ToList();
        if (learned.Count > 0) track = track with { TitleAliases = [.. track.TitleAliases, .. learned] };
        return [.. songs.Select(s => new LyricsCandidate(LyricsRef.Netease(s.Id), s.Name, s.Artist, s.Album, s.Duration,
                TrackMatcher.Score(s.Titles, s.Artists, s.Duration, track, s.Album), TrackMatcher.TitleMatches(s.Titles, track)))
            .OrderByDescending(c => c.Score)];
    }
}
