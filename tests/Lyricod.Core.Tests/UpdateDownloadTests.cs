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

    [Fact]
    public async Task DownloadsChecksAndReusesOnlyVerifiedFiles()
    {
        int calls = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(Asset.Url, request.RequestUri);
            Assert.Equal(UpdateCheck.UserAgent("1.0.0"), request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        }));
        var progress = new ProgressLog();
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
    [InlineData("short")]
    [InlineData("long")]
    public async Task DamagedDownloadIsDeleted(string fault)
    {
        var asset = fault switch
        {
            "hash" => Asset with { Sha256 = new string('0', 64) },
            "short" => Asset with { Size = Payload.Length + 1 },
            _ => Asset with { Size = Payload.Length - 1 },
        };
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Payload) { Headers = { ContentLength = asset.Size } },
        })));
        await Assert.ThrowsAsync<DamagedUpdateException>(() => UpdateDownload.DownloadAsync(client, asset, _directory, "1.0.0", null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancelDuringDownloadDeletesPart()
    {
        using var stop = new CancellationTokenSource();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) })));
        var progress = new ProgressLog(p => { if (p.Completed > 0) stop.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateDownload.DownloadAsync(client, Asset, _directory, "1.0.0", progress, stop.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task StalledBodyTimesOutAndDeletesPart()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) { Headers = { ContentLength = Payload.Length } } })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateDownload.DownloadAsync(client, Asset, _directory, "1.0.0", null, TestContext.Current.CancellationToken,
            stallTimeout: TimeSpan.FromMilliseconds(50)));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task UntrustedUrlIsNeverRequested()
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Exception("Must not send")));
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateDownload.DownloadAsync(client,
            Asset with { Url = new("http://evil.test/update.zip") }, _directory, "1.0.0", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TestFeedCanDownloadOverHttp()
    {
        var asset = Asset with { Url = new("http://vm.test:8000/update.zip") };
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) })));
        string path = await UpdateDownload.DownloadAsync(client, asset, _directory, "1.0.0", null, TestContext.Current.CancellationToken,
            new("http://vm.test:8000/releases.json"));
        Assert.True(await UpdateDownload.VerifyAsync(path, asset, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsAnInsecureFinalResponse()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new(HttpMethod.Get, "http://cdn.test/update.zip"), Content = new ByteArrayContent(Payload),
        })));
        await Assert.ThrowsAsync<HttpRequestException>(() => UpdateDownload.DownloadAsync(client, Asset, _directory, "1.0.0", null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void CleanupKeepsFutureUpdatesAndUnrelatedFiles()
    {
        foreach (string name in new[] { "1.0.0", "1.2.3", "2.0.0", "other" }) Directory.CreateDirectory(Path.Combine(_directory, "Updates", name));
        Directory.CreateDirectory(Path.Combine(_directory, "app.new"));
        Directory.CreateDirectory(Path.Combine(_directory, "app.old"));
        Directory.CreateDirectory(Path.Combine(_directory, "app.old-12345", "x"));
        Directory.CreateDirectory(Path.Combine(_directory, "app"));
        File.WriteAllText(Path.Combine(_directory, "Lyricod.exe.new"), "stale");
        File.WriteAllText(Path.Combine(_directory, "Lyricod.exe.old-12345"), "old launcher");
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "keep");
        PortableUpdate.Cleanup(_directory, Path.Combine(_directory, "Updates"), new(1, 2, 3));
        Assert.False(Directory.Exists(Path.Combine(_directory, "Updates", "1.0.0")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "Updates", "1.2.3")));
        Assert.True(Directory.Exists(Path.Combine(_directory, "Updates", "2.0.0")));
        Assert.True(Directory.Exists(Path.Combine(_directory, "Updates", "other")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "app.new")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "app.old")));
        Assert.False(Directory.Exists(Path.Combine(_directory, "app.old-12345")));
        Assert.False(File.Exists(Path.Combine(_directory, "Lyricod.exe.old-12345")));
        Assert.True(Directory.Exists(Path.Combine(_directory, "app")));
        Assert.True(File.Exists(Path.Combine(_directory, "settings.json")));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    sealed class ProgressLog(Action<UpdateProgress>? report = null) : IProgress<UpdateProgress>
    {
        public List<UpdateProgress> Values { get; } = [];
        public void Report(UpdateProgress value) { Values.Add(value); report?.Invoke(value); }
    }

    sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
