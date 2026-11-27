using System.Net;
using System.Security.Cryptography;

namespace Lyricod.Core.Tests;

public sealed class UpdateDownloadTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "lyricod-update-tests-" + Guid.NewGuid());
    static readonly byte[] Payload = "a verified update"u8.ToArray();
    static UpdateAsset Asset => new("Lyricod-1.2.3-windows-x64.zip", Payload.Length,
        new("https://github.com/Indarkdust/Lyricod-App/releases/download/v1.2.3/Lyricod-1.2.3-windows-x64.zip"),
        Convert.ToHexString(SHA256.HashData(Payload)));

    static Task<HttpResponseMessage> Ok() =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });

    /// <summary>The rest of the payload from <paramref name="from"/>, as a server that honours the range sends it.</summary>
    static Task<HttpResponseMessage> Rest(int from, int? end = null)
    {
        int to = end ?? Payload.Length;
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Payload[from..to]) };
        response.Content.Headers.ContentRange = new(from, to - 1, Payload.Length);
        return Task.FromResult(response);
    }

    /// <summary>Headed as the payload from <paramref name="from"/> on; <paramref name="body"/> is what really arrives.</summary>
    static Task<HttpResponseMessage> Send(int from, Stream body)
    {
        var response = new HttpResponseMessage(from == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(body) { Headers = { ContentLength = Payload.Length - from } },
        };
        if (from > 0) response.Content.Headers.ContentRange = new(from, Payload.Length - 1, Payload.Length);
        return Task.FromResult(response);
    }

    /// <summary>A download that doesn't wait between tries.</summary>
    Task<string> Download(HttpClient client, UpdateAsset asset, IProgress<UpdateProgress>? progress = null) =>
        UpdateDownload.DownloadAsync(client, asset, _directory, "1.0.0", progress, TestContext.Current.CancellationToken, retryDelay: TimeSpan.Zero);

    string In(params string[] parts) => Path.Combine([_directory, .. parts]);

    [Fact]
    public async Task DownloadsChecksAndReusesOnlyVerifiedFiles()
    {
        int calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            Assert.Equal(Asset.Url, request.RequestUri);
            Assert.Equal(UpdateCheck.UserAgent("1.0.0"), request.Headers.UserAgent.ToString());
            return Ok();
        }));
        var progress = new ProgressLog<UpdateProgress>();
        string path = await UpdateDownload.DownloadAsync(client, Asset, _directory, "1.0.0", progress, TestContext.Current.CancellationToken);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal(100, progress.Values.Last().Percent);
        await UpdateDownload.DownloadAsync(client, Asset, _directory, "1.0.0", null, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        await File.WriteAllBytesAsync(path, new byte[Payload.Length], TestContext.Current.CancellationToken);
        await UpdateDownload.DownloadAsync(client, Asset, _directory, "1.0.0", null, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("long")]
    [InlineData("length")]
    public async Task DamagedDownloadIsDeleted(string fault)
    {
        var asset = fault switch
        {
            "hash" => Asset with { Sha256 = new string('0', 64) },
            "long" => Asset with { Size = Payload.Length - 1 },
            _ => Asset,
        };
        int calls = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Payload) { Headers = { ContentLength = fault == "length" ? asset.Size + 5 : asset.Size } },
            });
        }));
        await Assert.ThrowsAsync<DamagedUpdateException>(() => Download(client, asset));
        Assert.Equal(1, calls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ADroppedDownloadCarriesOnFromItsPart()
    {
        int calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            if (++calls == 1)
            {
                Assert.Null(request.Headers.Range);
                return Send(0, new DroppedStream(Payload[..5]));
            }
            Assert.Equal("bytes=5-", request.Headers.Range?.ToString());
            return Rest(5);
        }));
        var progress = new ProgressLog<UpdateProgress>();
        string path = await Download(client, Asset, progress);
        Assert.Equal(2, calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal([path], Directory.GetFiles(_directory));
        // The second try reports from where the first one got to, never from nothing.
        Assert.Equal(5, progress.Values.SkipWhile(p => p.Completed < 5).Min(p => p.Completed));
        Assert.Equal(100, progress.Values.Last().Percent);
    }

    [Fact]
    public async Task ASecondCallCarriesOnFromThePartTheFirstLeft()
    {
        int calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            if (request.Headers.Range == null) return Send(0, new DroppedStream(Payload[..5]));
            Assert.Equal("bytes=5-", request.Headers.Range.ToString());
            return calls < 5 ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) : Rest(5);
        }));
        // Three tries in a row that bring nothing new are enough: it gives up, and keeps what it has.
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(client, Asset));
        Assert.Equal(4, calls);
        Assert.Equal(Payload[..5], await File.ReadAllBytesAsync(In(Asset.Name + ".part"), TestContext.Current.CancellationToken));
        string path = await Download(client, Asset);
        Assert.Equal(5, calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AServerThatIgnoresTheRangeSendsItAllAgain()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(In(Asset.Name + ".part"), Payload[..5], TestContext.Current.CancellationToken);
        int calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            Assert.Equal("bytes=5-", request.Headers.Range?.ToString());
            return Ok();
        }));
        string path = await Download(client, Asset);
        Assert.Equal(1, calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(17)]
    [InlineData(40)]
    public async Task APartThatIsNotThisFilesIsFetchedAgainWhole(int length)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(In(Asset.Name + ".part"), new byte[length], TestContext.Current.CancellationToken);
        var ranges = new List<string?>();
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            return request.Headers.Range == null ? Ok() : Rest(5);
        }));
        string path = await Download(client, Asset);
        // Shorter than the file: its rest is fetched, the whole doesn't check out, and it starts over.
        // As long or longer: nothing of it can be used.
        Assert.Equal(length == 5 ? ["bytes=5-", null] : [null], ranges);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("bytes 4-16/17")]
    [InlineData("bytes 5-16/18")]
    [InlineData("bytes 5-16/*")]
    // The parser refuses an end before the start or at the length: ContentRange reads as null.
    [InlineData("bytes 5-4/17")]
    [InlineData("bytes 5-17/17")]
    public async Task AnAnswerForAnotherRangeOrFileIsDamagedButThePartStays(string range)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(In(Asset.Name + ".part"), Payload[..5], TestContext.Current.CancellationToken);
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Payload[5..]) };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", range);
            return Task.FromResult(response);
        }));
        await Assert.ThrowsAsync<DamagedUpdateException>(() => Download(client, Asset));
        // A wrong answer says nothing about the bytes already here.
        Assert.Equal(Payload[..5], await File.ReadAllBytesAsync(In(Asset.Name + ".part"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWholeAnswerOfTheWrongLengthIsDamagedButThePartStays()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(In(Asset.Name + ".part"), Payload[..5], TestContext.Current.CancellationToken);
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("sign in"u8.ToArray()) })));
        await Assert.ThrowsAsync<DamagedUpdateException>(() => Download(client, Asset));
        Assert.Equal(Payload[..5], await File.ReadAllBytesAsync(In(Asset.Name + ".part"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AServerThatSendsTheRangeInPiecesIsFollowed()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(In(Asset.Name + ".part"), Payload[..5], TestContext.Current.CancellationToken);
        var ranges = new List<string?>();
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            int from = (int)request.Headers.Range!.Ranges.Single().From!.Value;
            return Rest(from, Math.Min(from + 4, Payload.Length));
        }));
        string path = await Download(client, Asset);
        Assert.Equal(["bytes=5-", "bytes=9-", "bytes=13-"], ranges);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TriesGoOnForAsLongAsEachBringsSomethingNew()
    {
        // Three bytes and a drop, time after time, with a busy server twice in between: never three
        // tries in a row with nothing new.
        int calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            if (calls is 2 or 3 or 5 or 6) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            int from = (int?)request.Headers.Range?.Ranges.Single().From ?? 0;
            return Send(from, new DroppedStream(Payload[from..Math.Min(from + 3, Payload.Length)]));
        }));
        string path = await Download(client, Asset);
        // Six tries of three bytes for seventeen (the last one is cut too, with nothing left to send), and four busy answers.
        Assert.Equal(10, calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APartThatShrinksAndGrowsAgainIsNoProgress()
    {
        // A server that ignores the range sends it all again, and the link cuts it at ten bytes, then five.
        var ranges = new List<string?>();
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            return Send(0, new DroppedStream(Payload[..(ranges.Count % 2 == 1 ? 10 : 5)]));
        }));
        await Assert.ThrowsAsync<HttpIOException>(() => Download(client, Asset));
        Assert.Equal([null, "bytes=10-", "bytes=5-", "bytes=10-"], ranges);
        Assert.Equal(Payload[..5], await File.ReadAllBytesAsync(In(Asset.Name + ".part"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APartLongerThanTheFileIsNoMarkForTheTriesAfterIt()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(In(Asset.Name + ".part"), new byte[40], TestContext.Current.CancellationToken);
        int calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            int from = (int?)request.Headers.Range?.Ranges.Single().From ?? 0;
            return Send(from, new DroppedStream(Payload[from..Math.Min(from + 3, Payload.Length)]));
        }));
        string path = await Download(client, Asset);
        Assert.Equal(6, calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStallPartwayKeepsWhatArrived()
    {
        bool stalling = true;
        var ranges = new List<string?>();
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            ranges.Add(request.Headers.Range?.ToString());
            if (!stalling) return Rest(5);
            bool first = request.Headers.Range == null;
            return Send(first ? 0 : 5, new StalledStream(first ? Payload[..5] : []));
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateDownload.DownloadAsync(client, Asset, _directory, "1.0.0", null, TestContext.Current.CancellationToken,
            stallTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.Zero));
        Assert.Equal([null, "bytes=5-", "bytes=5-", "bytes=5-"], ranges);
        Assert.Equal(Payload[..5], await File.ReadAllBytesAsync(In(Asset.Name + ".part"), TestContext.Current.CancellationToken));
        stalling = false;
        string path = await Download(client, Asset);
        Assert.Equal("bytes=5-", ranges.Last());
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task APartLongerThanTheServersFileStartsOver()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(In(Asset.Name + ".part"), Payload[..5], TestContext.Current.CancellationToken);
        int calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
            ++calls == 1 ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)) : Ok()));
        string path = await Download(client, Asset);
        Assert.Equal(2, calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABusyServerIsTriedAgainButAMissingFileIsNot()
    {
        int calls = 0;
        using var busy = new HttpClient(new StubHandler((_, _) =>
            ++calls < 3 ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) : Ok()));
        await Download(busy, Asset);
        Assert.Equal(3, calls);
        File.Delete(In(Asset.Name));
        calls = 0;
        using var missing = new HttpClient(new StubHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)); }));
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(missing, Asset));
        Assert.Equal(1, calls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task ABodyThatKeepsEndingEarlyGivesUpAndKeepsItsPart()
    {
        int calls = 0;
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            calls++;
            return request.Headers.Range == null ? Send(0, new MemoryStream(Payload[..5])) : Send(5, new MemoryStream());
        }));
        // The connection's fault, not the file's: it isn't reported as damaged.
        await Assert.ThrowsAsync<HttpIOException>(() => Download(client, Asset));
        Assert.Equal(4, calls);
        Assert.Equal(Payload[..5], await File.ReadAllBytesAsync(In(Asset.Name + ".part"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelKeepsThePartForNextTime()
    {
        using var stop = new CancellationTokenSource();
        int calls = 0;
        using var client = new HttpClient(new StubHandler((_, _) => { calls++; return Ok(); }));
        var progress = new ProgressLog<UpdateProgress>(p => { if (p.Completed > 0) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateDownload.DownloadAsync(client, Asset, _directory, "1.0.0", progress, stop.Token, retryDelay: TimeSpan.Zero));
        Assert.Equal([In(Asset.Name + ".part")], Directory.GetFiles(_directory));
        // All of it had arrived: the next call only checks it.
        string path = await Download(client, Asset);
        Assert.Equal(1, calls);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStalledDownloadIsTriedAgainThenTimesOut()
    {
        int calls = 0;
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            calls++;
            return Send(0, new StalledStream());
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateDownload.DownloadAsync(client, Asset, _directory, "1.0.0", null, TestContext.Current.CancellationToken,
            stallTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.Zero));
        Assert.Equal(3, calls);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task UntrustedUrlIsNeverRequested()
    {
        using var client = new HttpClient(new StubHandler((_, _) => throw new Exception("Must not send")));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDownload.DownloadAsync(client,
            Asset with { Url = new("http://evil.test/update.zip") }, _directory, "1.0.0", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TestFeedCanDownloadOverHttp()
    {
        var asset = Asset with { Url = new("http://vm.test:8000/update.zip") };
        using var client = new HttpClient(new StubHandler((_, _) => Ok()));
        string path = await UpdateDownload.DownloadAsync(client, asset, _directory, "1.0.0", null, TestContext.Current.CancellationToken,
            new("http://vm.test:8000/releases.json"));
        Assert.True(await UpdateDownload.VerifyAsync(path, asset, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CleanupKeepsFutureUpdatesAndUnrelatedFiles()
    {
        foreach (string name in new[] { "1.0.0", "1.2.3", "2.0.0", "other" }) Directory.CreateDirectory(In("Updates", name));
        Directory.CreateDirectory(In("app.new"));
        Directory.CreateDirectory(In("app.old"));
        Directory.CreateDirectory(In("app.old-12345", "x"));
        Directory.CreateDirectory(In("app"));
        File.WriteAllText(In("Lyricod.exe.new"), "stale");
        File.WriteAllText(In("Lyricod.exe.old-12345"), "old launcher");
        File.WriteAllText(In("settings.json"), "keep");
        PortableUpdate.Cleanup(_directory, In("Updates"), new(1, 2, 3));
        Assert.False(Directory.Exists(In("Updates", "1.0.0")));
        Assert.False(Directory.Exists(In("Updates", "1.2.3")));
        Assert.True(Directory.Exists(In("Updates", "2.0.0")));
        Assert.True(Directory.Exists(In("Updates", "other")));
        Assert.False(Directory.Exists(In("app.new")));
        Assert.False(Directory.Exists(In("app.old")));
        Assert.False(Directory.Exists(In("app.old-12345")));
        Assert.False(File.Exists(In("Lyricod.exe.old-12345")));
        Assert.True(Directory.Exists(In("app")));
        Assert.True(File.Exists(In("settings.json")));
    }

    [Fact]
    public void CleanupRightAfterAnUpdateLeavesTheOldCopyToTheLauncher()
    {
        Directory.CreateDirectory(In("app.old-777", "x"));
        File.WriteAllText(In("Lyricod.exe.old-777"), "old launcher");
        Directory.CreateDirectory(In("app.new"));
        PortableUpdate.Cleanup(_directory, In("Updates"), new(1, 2, 3), keepOldCopies: true);
        Assert.True(Directory.Exists(In("app.old-777")));
        Assert.True(File.Exists(In("Lyricod.exe.old-777")));
        Assert.False(Directory.Exists(In("app.new")));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    /// <summary>Sends its bytes, then the connection is gone.</summary>
    sealed class DroppedStream(byte[] sent) : MemoryStream(sent)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await base.ReadAsync(buffer, cancellationToken);
            return count > 0 ? count : throw new HttpIOException(HttpRequestError.ResponseEnded);
        }
    }

    /// <summary>Sends its bytes, if any, then nothing more comes.</summary>
    sealed class StalledStream(byte[]? sent = null) : MemoryStream(sent ?? [])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await base.ReadAsync(buffer, cancellationToken);
            if (count == 0) await Task.Delay(Timeout.Infinite, cancellationToken);
            return count;
        }
    }
}
