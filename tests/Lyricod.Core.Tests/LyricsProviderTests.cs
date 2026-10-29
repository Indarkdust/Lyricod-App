using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lyricod.Core.Tests;

public class LyricsProviderTests
{
    const string LineJson = """{"lyric":"[00:01.00]Hello"}""";
    const string LegacyJson = """{"data":{"song":{"list":[{"songmid":"qq-song","songid":42,"songname":"Hello","singer":[{"name":"Singer"}],"interval":120}]}}}""";
    const string ModernJson = """{"req":{"data":{"body":{"song":{"list":[{"mid":"wrong-song","id":43,"name":"Hello","singer":[{"name":"Singer"}],"interval":120}]}}}}}""";
    static Track Track() => new() { TrackId = "test", Title = "Hello", Artist = "Singer", Duration = 120, QqSongMid = "qq-song", QqSongId = 42 };

    [Fact]
    public async Task FailedWordCheckShowsCacheFirstAndIsRetriedNextDay()
    {
        var events = new ConcurrentQueue<string>();
        using var fixture = new Fixture(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            events.Enqueue(path);
            if (path.Contains("lyric_download")) return Response("unavailable", HttpStatusCode.ServiceUnavailable);
            return Response(path.Contains("fcg_query_lyric") ? LineJson : "{}");
        });
        var track = Track();
        var file = fixture.Seed(track);
        var original = File.ReadAllBytes(file);
        // Due (saved two days ago), then not (just tried), then due again the next day.
        foreach (var daysAgo in new[] { 2, 0, 2 })
        {
            if (daysAgo > 0) Fixture.Age(file, daysAgo);
            Lyrics? shown = null;
            var result = await fixture.Provider.GetLyrics(track, ct: TestContext.Current.CancellationToken, onCached: cached =>
            {
                Assert.Equal("Hello", cached.Lines[0].Text);
                shown = cached;
                events.Enqueue("cached");
            });
            Assert.Equal("NetEase", result?.Source);
            Assert.False(result?.IsWordSynced);
            // Unchanged: the very lyrics already shown, so the app needn't show them again.
            if (shown != null) Assert.Same(shown, result);
            Assert.Equal(original, File.ReadAllBytes(file));
        }
        Assert.Equal("cached", events.First());
        Assert.Equal(2, events.Count(e => e.Contains("lyric_download")));
        Assert.Equal(2, events.Count(e => e == "cached"));
    }

    [Fact]
    public async Task RecentlyCheckedCacheSkipsNetwork()
    {
        var requests = 0;
        using var fixture = new Fixture(_ => { requests++; return Response("{}"); });
        var track = Track();
        fixture.Seed(track);
        var callbacks = 0;
        var result = await fixture.Provider.GetLyrics(track, ct: TestContext.Current.CancellationToken, onCached: _ => callbacks++);
        Assert.Equal("Hello", result?.Lines[0].Text);
        Assert.Equal(0, requests);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task OldCheckedMarkIsIgnored()
    {
        var paths = new ConcurrentQueue<string>();
        using var fixture = new Fixture(request => { paths.Enqueue(request.RequestUri!.AbsolutePath); return Response("{}"); });
        var track = Track();
        var file = fixture.Seed(track);
        // 0.1.0 marked songs as checked even when QQ Music couldn't be asked.
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["wordsChecked"] = true;
        File.WriteAllText(file, json.ToJsonString());
        Fixture.Age(file, 2);
        await fixture.Provider.GetLyrics(track, ct: TestContext.Current.CancellationToken);
        Assert.Contains(paths, p => p.Contains("lyric_download"));
    }

    [Fact]
    public async Task CancellationAfterCachedCallbackDoesNotRequestOrMutateCache()
    {
        var requests = 0;
        using var fixture = new Fixture(_ => { requests++; return Response("{}"); });
        using var cancellation = new CancellationTokenSource();
        var track = Track();
        var file = fixture.Seed(track);
        var original = File.ReadAllBytes(file);
        var savedAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, savedAt);
        var callbacks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.GetLyrics(track, ct: cancellation.Token,
            onCached: _ => { callbacks++; cancellation.Cancel(); }));
        Assert.Equal(1, callbacks);
        Assert.Equal(0, requests);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Equal(savedAt, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task CancellationDuringFetchDoesNotCreateCache()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture(_ => { cancellation.Cancel(); return Response(LineJson); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.GetLyrics(Track(),
            pinned: new LyricsRef(LyricsSource.Qq, "qq-song"), ct: cancellation.Token));
        Assert.Equal(0, fixture.Provider.CachedCount());
    }

    [Fact]
    public async Task PinnedCacheDoesNotRequestNetwork()
    {
        var requests = 0;
        using var fixture = new Fixture(_ => { requests++; return Response("{}"); });
        var track = Track();
        fixture.Seed(track);
        var result = await fixture.Provider.GetLyrics(track, pinned: LyricsRef.Netease(7), ct: TestContext.Current.CancellationToken);
        Assert.Equal("Hello", result?.Lines[0].Text);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task OddNeteaseReplyFallsThroughToTheNextSource()
    {
        using var fixture = new Fixture(request => Response(
            request.RequestUri!.Host.EndsWith("163.com") ? """{"result":42}"""
            : request.RequestUri.AbsolutePath.Contains("fcg_query_lyric") ? LineJson : "{}"));
        var result = await fixture.Provider.GetLyrics(Track(), ct: TestContext.Current.CancellationToken);
        Assert.Equal("QQ Music", result?.Source);
    }

    [Theory]
    [InlineData("{\"req\":0}")]
    [InlineData("{\"req\":{\"data\":{\"body\":{\"song\":{\"list\":[0]}}}}}")]
    [InlineData("not-json")]
    public async Task MalformedQqSearchFallsBackToLegacy(string malformed)
    {
        var hosts = new List<string>();
        using var fixture = new Fixture(request =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Response(request.RequestUri.Host == "u.y.qq.com" ? malformed : LegacyJson);
        });
        var results = await fixture.Provider.QqCandidates("Hello Singer", Track(), TestContext.Current.CancellationToken);
        Assert.Equal("qq-song", Assert.Single(results).Id.Id);
        Assert.Equal(["u.y.qq.com", "shc.y.qq.com"], hosts);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "wrong-song")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "qq-song")]
    public async Task QqSearchStatusControlsWhetherItsJsonIsAccepted(HttpStatusCode status, string expected)
    {
        using var fixture = new Fixture(request => request.RequestUri!.Host == "u.y.qq.com"
            ? Response(ModernJson, status) : Response(LegacyJson));
        var results = await fixture.Provider.QqCandidates("Hello Singer", Track(), TestContext.Current.CancellationToken);
        Assert.Equal(expected, Assert.Single(results).Id.Id);
    }

    static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    sealed class Fixture : IDisposable
    {
        readonly string _dir = Path.Combine(Path.GetTempPath(), "lyricod-provider-tests", Guid.NewGuid().ToString());
        readonly StubHandler _handler;
        public LyricsProvider Provider { get; }
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _handler = new StubHandler(respond);
            Provider = new LyricsProvider(_dir, "test", _handler);
        }
        public string Seed(Track track)
        {
            var file = Path.Combine(_dir, LyricsProvider.CacheKey(track) + ".json");
            File.WriteAllText(file, JsonSerializer.Serialize(new RawLyrics
            {
                Source = "NetEase", Lrc = "[00:01.00]Hello", Ref = LyricsRef.Netease(7),
            }));
            return file;
        }
        /// <summary>Saved <paramref name="days"/> ago, as far as the once-a-day word check is concerned.</summary>
        public static void Age(string file, double days) => File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-days));
        public void Dispose() { _handler.Dispose(); Directory.Delete(_dir, recursive: true); }
    }
}
