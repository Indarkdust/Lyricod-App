using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Lyricod.Core.Fonts;

namespace Lyricod.Core.Tests;

public class WenKaiDownloadTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "lyricod-wenkai-" + Guid.NewGuid().ToString("N"));
    static readonly byte[] Data = Bytes(300_000);
    static readonly FontFile Test = new("https://example.test/font.ttf", "font.ttf", Data.Length, Convert.ToHexStringLower(SHA256.HashData(Data)));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    static byte[] Bytes(int n)
    {
        var b = new byte[n];
        new Random(7).NextBytes(b);
        return b;
    }

    string Published => Path.Combine(_dir, Test.FileName);
    string Part => Published + ".part";

    Task Download(Server server, IProgress<FontDownloadProgress>? progress = null, CancellationToken stop = default) =>
        FontFileDownload.DownloadAsync(new HttpClient(server), Test, _dir, progress, stop);

    void LeavePart(int bytes)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Part, Data[..bytes]);
    }

    [Fact]
    public async Task DownloadsTheWholeFileAndPublishesItOnlyWhenItMatches()
    {
        var server = new Server();
        var seen = new List<FontDownloadProgress>();
        await Download(server, new Collect(seen));
        Assert.Equal(Data, File.ReadAllBytes(Published));
        Assert.False(File.Exists(Part));
        Assert.Null(server.Ranges.Single());
        Assert.Equal(Data.Length, seen[^1].Completed);
        Assert.Equal(Data.Length, seen[^1].Total);
    }

    [Fact]
    public async Task PicksUpWhereAnEarlierTryStopped()
    {
        LeavePart(120_000);
        var server = new Server();
        var seen = new List<FontDownloadProgress>();
        await Download(server, new Collect(seen));
        Assert.Equal(Data, File.ReadAllBytes(Published));
        Assert.Equal(120_000, server.Ranges.Single()!.Ranges.Single().From);
        Assert.Equal(Data.Length - 120_000, server.Sent);
        // Progress counts the whole file, starting from what was already there.
        Assert.True(seen[0].Completed > 120_000);
        Assert.All(seen, p => Assert.Equal(Data.Length, p.Total));
    }

    [Fact]
    public async Task StartsOverWhenTheServerSendsTheWholeFile()
    {
        LeavePart(120_000);
        var server = new Server { IgnoreRange = true };
        await Download(server);
        Assert.Equal(Data, File.ReadAllBytes(Published));
        Assert.Equal(Data.Length, server.Sent);
    }

    [Fact]
    public async Task AWrongPartIsDroppedNotJoined()
    {
        LeavePart(120_000);
        var server = new Server { WrongStart = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(server));
        Assert.False(File.Exists(Part));
        Assert.False(File.Exists(Published));
    }

    [Fact]
    public async Task AnInterruptedDownloadKeepsItsPartAndFinishesNextTime()
    {
        var server = new Server { CutAfter = 100_000 };
        await Assert.ThrowsAnyAsync<Exception>(() => Download(server));
        Assert.False(File.Exists(Published));
        Assert.Equal(100_000, new FileInfo(Part).Length);
        server.CutAfter = null;
        await Download(server);
        Assert.Equal(Data, File.ReadAllBytes(Published));
        Assert.Equal(100_000, server.Ranges[^1]!.Ranges.Single().From);
    }

    [Fact]
    public async Task TheWrongFileIsNeverPublished()
    {
        var server = new Server { Corrupt = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(server));
        Assert.False(File.Exists(Published));
        Assert.False(File.Exists(Part));
    }

    [Fact]
    public async Task AFinishedPartIsCheckedAndPublishedWithoutDownloadingAgain()
    {
        LeavePart(Data.Length);
        var server = new Server();
        await Download(server);
        Assert.Equal(Data, File.ReadAllBytes(Published));
        Assert.Empty(server.Ranges);
    }

    [Fact]
    public async Task ARangeTheServerCantServeStartsOverNextTime()
    {
        LeavePart(120_000);
        var server = new Server { Unsatisfiable = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(server));
        Assert.False(File.Exists(Part));
        Assert.False(File.Exists(Published));
    }

    [Fact]
    public async Task CancellingPublishesNothing()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(new Server(), stop: stop.Token));
        Assert.False(File.Exists(Published));
    }

    [Fact]
    public void TheReleasedFileIsPinned()
    {
        var file = WenKaiFont.File;
        Assert.StartsWith("https://github.com/lxgw/LxgwWenKai-Lite/releases/download/v", file.Url);
        Assert.EndsWith("/" + file.FileName, file.Url);
        Assert.Matches("^[0-9a-f]{64}$", file.Sha256);
        Assert.InRange(file.Size, 10_000_000, 20_000_000);
        Assert.Contains("/v1.530/", WenKaiFont.LicenceUrl);
        Assert.Contains("/v1.530/", file.Url);
    }

    sealed class Collect(List<FontDownloadProgress> seen) : IProgress<FontDownloadProgress>
    {
        public void Report(FontDownloadProgress value) => seen.Add(value);
    }

    sealed class Server : HttpMessageHandler
    {
        public bool IgnoreRange, WrongStart, Corrupt, Unsatisfiable;
        public int? CutAfter;
        public long Sent;
        public List<RangeHeaderValue?> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            Ranges.Add(request.Headers.Range);
            if (Unsatisfiable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            long from = IgnoreRange ? 0 : request.Headers.Range?.Ranges.Single().From ?? 0;
            var body = Data[(int)from..].ToArray();
            if (Corrupt) body[^1] ^= 0xFF;
            if (CutAfter is int cut && cut - (int)from < body.Length) body = body[..Math.Max(cut - (int)from, 0)];
            Sent += body.Length;
            var response = new HttpResponseMessage(from > 0 || WrongStart ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            // The full length is what was sent: a connection that drops shows up as a short read.
            response.Content.Headers.ContentLength = body.Length;
            if (response.StatusCode == HttpStatusCode.PartialContent)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(WrongStart ? from + 1 : from, Data.Length - 1, Data.Length);
            return Task.FromResult(response);
        }
    }
}
