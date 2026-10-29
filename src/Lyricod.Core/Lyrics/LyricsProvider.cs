using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lyricod.Core;

/// <summary>
/// Finds lyrics for a track: NetEase first (best coverage for Chinese music, has
/// translations and word timing), then QQ Music, then LRCLIB. Results are cached on disk.
/// A port of the macOS app's LyricsProvider; behaviour should stay the same.
/// </summary>
public sealed class LyricsProvider
{
    readonly HttpClient _http;
    readonly string _cacheDir;
    /// <summary>An artist's other names, by the name a player shows (see <see cref="WithAliases"/>).</summary>
    readonly ConcurrentDictionary<(string Artist, bool AllowGuess), IReadOnlyList<string>> _aliasCache = new();
    /// <summary>LRCLIB search results carry the lyrics too; kept so picking one needs no second request.</summary>
    readonly ConcurrentDictionary<string, RawLyrics> _lrclibSeen = new();

    static readonly JsonSerializerOptions CacheJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    /// <param name="cacheDir">Where fetched lyrics are kept (one JSON file per song).</param>
    /// <param name="appVersion">Sent in the User-Agent: LRCLIB asks clients to name themselves.</param>
    public LyricsProvider(string cacheDir, string appVersion, HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Lyricod/{appVersion} (+https://github.com/Indarkdust/Lyricod-App)");
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        Trim(cacheDir);
    }

    /// <summary>Keeps the lyrics cache to the 1500 most recently saved songs (~60 KB each).</summary>
    static void Trim(string dir)
    {
        var files = new DirectoryInfo(dir).GetFiles("*.json");
        if (files.Length <= 1500) return;
        foreach (var f in files.OrderByDescending(f => f.LastWriteTimeUtc).Skip(1500))
            try { f.Delete(); } catch (IOException) { }
    }

    // ---- Requests ----

    async Task<JsonNode?> GetJson(HttpRequestMessage req, CancellationToken ct, bool require200 = false)
    {
        var text = await GetText(req, ct, require200).ConfigureAwait(false);
        try { return text == null ? null : JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    async Task<string?> GetText(HttpRequestMessage req, CancellationToken ct, bool require200 = false)
    {
        try
        {
            using var request = req;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_http.Timeout);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode || require200 && res.StatusCode != HttpStatusCode.OK) return null;
            return await LyricData.ReadText(res.Content, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException or InvalidOperationException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    static string Query(params (string Key, string Value)[] items) =>
        string.Join('&', items.Select(i => Uri.EscapeDataString(i.Key) + "=" + Uri.EscapeDataString(i.Value)));

    static HttpRequestMessage NeteaseRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Referrer = new Uri("https://music.163.com/");
        return req;
    }

    static HttpRequestMessage QqRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Referrer = new Uri("https://y.qq.com/");
        return req;
    }

    static JsonNode? Field(JsonNode? node, params string[] path)
    {
        foreach (var key in path) node = (node as JsonObject)?[key];
        return node;
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    static long? Long(JsonNode? n) => n is JsonValue v ? (v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : null) : null;
    static IEnumerable<JsonNode> Arr(JsonNode? n) => n is JsonArray a ? a.OfType<JsonNode>() : [];

    // ---- Aliases ----

    /// <summary>
    /// <paramref name="track"/> with its artist's other names from NetEase's artist records:
    /// players often show an English name ("Crowd Lu") where lyric sites use the original
    /// (卢广仲), and the other way round.
    /// </summary>
    public async Task<Track> WithAliases(Track track, CancellationToken ct = default, bool allowGuess = true)
    {
        // The first credited artist ("A & B", "A / B", "A, B").
        var primary = track.Artist.Split(['&', '/', ',', '、', ';'])[0].Trim();
        if (primary.Length == 0) return track;
        if (_aliasCache.TryGetValue((primary, allowGuess), out var cached)) return track with { ArtistAliases = cached };
        var names = new List<string>();
        var res = await GetJson(NeteaseRequest(HttpMethod.Post,
            "https://music.163.com/api/cloudsearch/pc?" + Query(("s", primary), ("type", "100"), ("limit", "3"))), ct);
        var want = TrackMatcher.Normalize(primary);
        var artists = Arr(Field(res, "result", "artists")).Select(a => new[] { Str(Field(a, "name")) }.Concat(Arr(Field(a, "alias")).Select(Str)).Append(Str(Field(a, "trans")))
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList()).ToList();
        var found = artists.Where(all => all.Any(s => TrackMatcher.Normalize(s) == want)).Take(1).ToList();
        // A romanised name, as YouTube Music and Spotify show it, may be missing from NetEase's
        // records ("Kenshi Yonezu" for 米津玄師) or listed under someone else too (優莉 lists "Yuuri";
        // 優里, the top hit, doesn't): NetEase's top hit counts as well when it has a Chinese,
        // Japanese or Korean name. A wrong guess still needs the title or the length to agree.
        if (allowGuess && IsLatinName(primary) && artists.FirstOrDefault() is { } top && top.Any(HasCjk) && !found.Contains(top))
            found.Insert(0, top);
        names = found.SelectMany(all => all).Where(s => TrackMatcher.Normalize(s) != want).Distinct().ToList();
        // Remembered only when NetEase answered, so a failed lookup is tried again.
        if (res != null) _aliasCache[(primary, allowGuess)] = names;
        return track with { ArtistAliases = names };
    }

    static bool IsLatinName(string s) => s.Any(char.IsLetter) && s.All(c => c < 'ɐ' || char.IsWhiteSpace(c) || char.IsPunctuation(c));
    static bool HasCjk(string s) => s.Any(c => c >= '⺀');

    // ---- Cache ----

    /// <summary>Stable, filename-safe key for a song (the macOS app's FNV-1a key, so files match).</summary>
    public static string CacheKey(Track t)
    {
        var s = $"{t.Title}|{t.Artist}|{(long)t.Duration}";
        ulong h = 0xcbf29ce484222325;
        foreach (var b in Encoding.UTF8.GetBytes(s)) h = (h ^ b) * 0x100000001b3;
        return h.ToString("x", CultureInfo.InvariantCulture);
    }

    string CacheFile(string name) => Path.Combine(_cacheDir, name + ".json");

    RawLyrics? ReadCache(string file)
    {
        try { return JsonSerializer.Deserialize<RawLyrics>(File.ReadAllText(file)); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    void WriteCache(string file, RawLyrics raw)
    {
        try { File.WriteAllText(file, JsonSerializer.Serialize(raw, CacheJson)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void ClearAll()
    {
        foreach (var f in Directory.GetFiles(_cacheDir)) try { File.Delete(f); } catch (IOException) { }
    }

    public int CachedCount() => Directory.GetFiles(_cacheDir, "*.json").Length;

    public void ClearCache(Track track)
    {
        try { File.Delete(CacheFile(CacheKey(track))); } catch (IOException) { }
    }

    /// <summary>Keep manual choices when the same playing song reports its real length.</summary>
    public Track CorrectDuration(Track track, double duration, TrackOverrides overrides)
    {
        var corrected = track with { Duration = duration };
        var pinned = overrides[track].Lyrics;
        overrides.Move(track, corrected);
        var oldFile = CacheFile(CacheKey(track));
        var newFile = CacheFile(CacheKey(corrected));
        // Only manual lyrics belong to the corrected recording without another search.
        if (oldFile != newFile && pinned != null && ReadCache(oldFile)?.SongRef == pinned)
        {
            try
            {
                if (!File.Exists(newFile) && overrides[corrected].Lyrics == pinned) File.Move(oldFile, newFile);
                else File.Delete(oldFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return corrected;
    }

    // ---- Main lookup ----

    /// <param name="search">The player's acceptance bar and this lookup's best score.</param>
    /// <param name="pinned">Lyrics the user chose for this track; skips searching.</param>
    /// <param name="onCached">Saved lyrics to show now while QQ Music is checked for a word-by-word
    /// copy; the result is then this same object unless the check found one.</param>
    public async Task<Lyrics?> GetLyrics(Track track, bool ignoreCache = false, LyricsRef? pinned = null, CancellationToken ct = default, Action<Lyrics>? onCached = null, LyricsSearch? search = null)
    {
        search ??= new LyricsSearch(isMusicPlayer: true);
        var file = CacheFile(CacheKey(track));
        track = search.Prepare(track);
        if (!ignoreCache && ReadCache(file) is { } cachedRaw && (pinned == null || cachedRaw.SongRef == pinned)
            && (pinned != null || !search.AutomaticLookupAllowed || search.Accepts(cachedRaw.MatchScore, cachedRaw.TitleMatched))
            && cachedRaw.Parse() is { } cached) // cached junk (e.g. credits only) is fetched again
        {
            search.Observe(cachedRaw.MatchScore);
            if (!search.AutomaticLookupAllowed || pinned != null || !NeedsWordCheck(cachedRaw, cached) || !WordCheckDue(file)) return cached;
            onCached?.Invoke(cached);
            ct.ThrowIfCancellationRequested();
            var words = await WordTimed(await WithAliases(track, ct, allowGuess: search.IsMusicPlayer), cachedRaw, requireTitle: true, ct, search);
            ct.ThrowIfCancellationRequested();
            if (words?.Parse() is not { } upgraded)
            {
                // Not this time (no word-by-word copy, or QQ Music didn't answer): try again tomorrow.
                try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return cached;
            }
            WriteCache(file, words);
            return upgraded;
        }
        if (!search.AutomaticLookupAllowed && pinned == null) return null;
        if (pinned == null) track = await WithAliases(track, ct, allowGuess: search.IsMusicPlayer);
        RawLyrics? raw;
        if (pinned != null) raw = await Fetch(pinned, ct);
        else
        {
            // Timed lyrics from any source beat untimed ones from an earlier source (QQ Music has
            // plain uploads where LRCLIB has the timed version); untimed is the last resort. Word
            // timing beats line timing: see WordTimed.
            // Results whose title matches come first, from every source; one that only has the
            // artist and length (an English title for a song listed only in Chinese) is a fallback.
            // A player can report the wrong length (QQ Music did), and the artist's other song of
            // that length then won from the first source searched.
            raw = null;
            foreach (bool requireTitle in search.IsMusicPlayer ? new[] { true, false } : new[] { true })
            {
                RawLyrics? untimed = null;
                foreach (var source in new Func<Track, bool, CancellationToken, Task<RawLyrics?>>[]
                {
                    (t, r, c) => FromNetease(t, r, c, search),
                    (t, r, c) => FromQq(t, r, c, search: search),
                    (t, r, c) => FromLrclib(t, r, c, search),
                })
                {
                    var found = await source(track, requireTitle, ct);
                    if (found?.Parse() is not { } candidate) continue;
                    if (candidate.IsSynced) { raw = found; break; }
                    untimed ??= found;
                }
                if (raw != null) { raw = await WordTimed(track, raw, requireTitle, ct, search) ?? raw; break; }
                if (untimed != null) { raw = untimed; break; }
            }
        }
        if (raw == null || pinned == null && !search.Accepts(raw.MatchScore, raw.TitleMatched)
            || raw.Parse() is not { } parsed) return null;
        ct.ThrowIfCancellationRequested();
        WriteCache(file, raw);
        return parsed;
    }

    /// <summary>NetEase lyrics with line timing only: QQ Music may have the song word by word.</summary>
    static bool NeedsWordCheck(RawLyrics raw, Lyrics parsed) =>
        raw.SongRef?.Source == LyricsSource.Netease && parsed is { IsSynced: true, IsWordSynced: false };

    /// <summary>
    /// Saved line-timed lyrics are checked against QQ Music at most once a day, so a song QQ Music
    /// doesn't have word by word costs one lookup a day, and one it couldn't be asked about
    /// (offline, throttled) is tried again. The file's date marks the last try.
    /// </summary>
    static bool WordCheckDue(string file) => DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(24);

    /// <summary>
    /// NetEase comes first but has many songs with line timing only that QQ Music has word by word
    /// (周杰伦's 龙卷风): for those, QQ Music's copy, when it's clearly this recording (title, artist
    /// and length agree, or the exact song QQ Music is playing) and keeps any translation. Null
    /// when <paramref name="raw"/> should stay.
    /// </summary>
    async Task<RawLyrics?> WordTimed(Track track, RawLyrics raw, bool requireTitle, CancellationToken ct, LyricsSearch search)
    {
        if (raw.Parse() is not { } lines || !NeedsWordCheck(raw, lines)) return null;
        if (await FromQq(track, requireTitle, ct, minScore: 7, search: search) is not { } qq || qq.Parse() is not { IsWordSynced: true } words) return null;
        ct.ThrowIfCancellationRequested();
        static bool Translated(Lyrics l) => l.Lines.Any(x => x.Translation != null);
        return Translated(lines) && !Translated(words) ? null : qq;
    }

    /// <summary>Lyrics for one song in any source.</summary>
    public Task<RawLyrics?> Fetch(LyricsRef r, CancellationToken ct = default) => r.Source switch
    {
        LyricsSource.Netease when long.TryParse(r.Id, out var id) => FetchNetease(id, ct),
        LyricsSource.Netease => Task.FromResult<RawLyrics?>(null),
        LyricsSource.Qq => FetchQqSong(r.Id, r.QqId, ct),
        _ => FetchLrclib(r.Id, ct),
    };

    /// <summary>
    /// Search results from every source, scored against <paramref name="track"/>, best first.
    /// For Choose Lyrics. Among good matches, <paramref name="preferred"/> sources come first:
    /// LRCLIB alone often has a dozen uploads of one song.
    /// </summary>
    public async Task<List<LyricsCandidate>> Candidates(string query, Track track, IReadOnlyList<LyricsSource> preferred, CancellationToken ct = default)
    {
        track = await WithAliases(track, ct);
        var netease = NeteaseCandidates(query, track, ct);
        var qq = QqCandidates(query, track, ct);
        var lrclib = LrclibCandidates(query, track, ct);
        var all = (await netease).Concat(await qq).Concat((await lrclib).Take(10)).ToList();
        static int Tier(LyricsCandidate c) => c.Score >= 7 ? 2 : c.Score >= 3 ? 1 : 0;
        int Rank(LyricsCandidate c) { var i = preferred.ToList().IndexOf(c.Id.Source); return i < 0 ? preferred.Count : i; }
        return all.Select((c, i) => (c, i))
            .OrderByDescending(x => Tier(x.c)).ThenBy(x => Rank(x.c)).ThenByDescending(x => x.c.Score).ThenBy(x => x.i)
            .Select(x => x.c).ToList();
    }

    // ---- NetEase ----

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

    // ---- QQ Music ----

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

    // ---- LRCLIB ----

    static RawLyrics? LrclibRaw(JsonNode? r) => r == null ? null : new RawLyrics
    {
        Source = "LRCLIB", Lrc = Str(Field(r, "syncedLyrics")), Plain = Str(Field(r, "plainLyrics")),
        Ref = Long(Field(r, "id")) is { } id ? new LyricsRef(LyricsSource.Lrclib, id.ToString(CultureInfo.InvariantCulture)) : null,
    };

    async Task<RawLyrics?> FromLrclib(Track track, bool requireTitle, CancellationToken ct, LyricsSearch search)
    {
        var url = "https://lrclib.net/api/get?" + Query(("track_name", track.Title), ("artist_name", track.Artist),
            ("album_name", track.Album), ("duration", ((long)Math.Round(track.Duration)).ToString(CultureInfo.InvariantCulture)));
        // The exact lookup goes by title, so it's only worth asking on the first pass.
        if (requireTitle)
        {
            var response = await GetJson(new HttpRequestMessage(HttpMethod.Get, url), ct, require200: true);
            if (LrclibRaw(response) is { } exact)
            {
                var title = Str(Field(response, "trackName"));
                if (title == null)
                {
                    if (search.IsMusicPlayer) return exact;
                }
                else
                {
                    var artist = Str(Field(response, "artistName")) ?? "";
                    double score = TrackMatcher.Score([title], [artist], Num(Field(response, "duration")) ?? 0, track, Str(Field(response, "albumName")));
                    bool titleMatched = TrackMatcher.TitleMatches([title], track);
                    search.Observe(score);
                    if (search.Accepts(score, titleMatched)) return exact.Matched(score, titleMatched);
                }
            }
        }
        // The exact lookup needs the album too, which browsers often don't report: search, and
        // take the best match that has timed lyrics.
        foreach (var c in search.Observe(await LrclibCandidates($"{track.Title} {track.Artist}", track, ct)))
        {
            if (c.Score < (search.IsMusicPlayer ? 6 : LyricsSearch.ConfidentScore)) break;
            if (requireTitle && !c.TitleMatched) continue;
            if (!search.Accepts(c.Score, c.TitleMatched)) continue;
            if (_lrclibSeen.TryGetValue(c.Id.Id, out var seen) && !string.IsNullOrEmpty(seen.Lrc)) return seen.Matched(c.Score, c.TitleMatched);
        }
        return null;
    }

    public async Task<RawLyrics?> FetchLrclib(string id, CancellationToken ct = default)
    {
        if (_lrclibSeen.TryGetValue(id, out var seen)) return seen;
        return LrclibRaw(await GetJson(new HttpRequestMessage(HttpMethod.Get, "https://lrclib.net/api/get/" + Uri.EscapeDataString(id)), ct, require200: true));
    }

    /// <summary>LRCLIB search results scored against <paramref name="track"/>, best first. Records without lyrics are left out.</summary>
    public async Task<List<LyricsCandidate>> LrclibCandidates(string query, Track track, CancellationToken ct = default)
    {
        var res = await GetJson(new HttpRequestMessage(HttpMethod.Get, "https://lrclib.net/api/search?" + Query(("q", query))), ct, require200: true);
        var list = new List<LyricsCandidate>();
        foreach (var r in Arr(res).Take(20))
        {
            if (Long(Field(r, "id")) is not { } id || Str(Field(r, "trackName")) is not { } title) continue;
            if (string.IsNullOrEmpty(Str(Field(r, "syncedLyrics")) ?? Str(Field(r, "plainLyrics")))) continue;
            var key = id.ToString(CultureInfo.InvariantCulture);
            _lrclibSeen[key] = LrclibRaw(r)!;
            var artist = Str(Field(r, "artistName")) ?? "";
            var duration = Num(Field(r, "duration")) ?? 0;
            list.Add(new LyricsCandidate(new LyricsRef(LyricsSource.Lrclib, key), title, artist, Str(Field(r, "albumName")) ?? "", duration,
                TrackMatcher.Score([title], [artist], duration, track, Str(Field(r, "albumName"))), TrackMatcher.TitleMatches([title], track)));
        }
        return [.. list.OrderByDescending(c => c.Score)];
    }
}
