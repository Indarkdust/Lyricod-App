using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Lyricod.Core.Fonts;

namespace Lyricod.Core.Tests;

public class FontTests
{
    [Theory]
    [InlineData(LyricWeight.Regular, 400, 330, 400)]
    [InlineData(LyricWeight.Medium, 500, 380, 400)]
    [InlineData(LyricWeight.Semibold, 600, 520, 700)]
    [InlineData(LyricWeight.Bold, 700, 630, 700)]
    [InlineData(LyricWeight.Heavy, 900, 700, 700)]
    public void WeightsMatchTheSelectedFont(LyricWeight weight, int standard, int miSans, int yaHei)
    {
        Assert.Equal(standard, FontWeights.Get(weight, LyricFont.Other));
        Assert.Equal(miSans, FontWeights.Get(weight, LyricFont.MiSans));
        Assert.Equal(yaHei, FontWeights.Get(weight, LyricFont.Default));
    }

    static byte[] Font()
    {
        var font = new byte[1100000];
        new Random(42).NextBytes(font);
        font[0] = 0; font[1] = 1; font[2] = font[3] = 0;
        return font;
    }

    static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var bytes = new MemoryStream();
        // Legacy names, with the UTF-8 flag off, just like Xiaomi's ZIP.
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true, Encoding.Latin1))
            foreach (var (name, data) in entries)
                using (var stream = zip.CreateEntry(name).Open()) stream.Write(data);
        return bytes.ToArray();
    }

    [Fact]
    public void LocatesLegacyNamesAndSkipsMacMetadata()
    {
        using var stream = new MemoryStream(Zip(("__MACOSX/??/MiSansVF.ttf", [1]), ("MiSans/¿É±ä×ÖÌå/MiSansVF.ttf", [2])));
        using var zip = new ZipArchive(stream);
        Assert.StartsWith("MiSans/", MiSansArchive.FindFont(zip).FullName);
    }

    [Fact]
    public void RejectsMissingOrAmbiguousFonts()
    {
        foreach (var entries in new[]
        {
            new[] { ("Other.ttf", new byte[] { 1 }), ("__MACOSX/MiSansVF.ttf", new byte[] { 2 }) },
            new[] { ("a/MiSansVF.ttf", new byte[] { 1 }), ("b/MiSansVF.ttf", new byte[] { 2 }) },
        })
        {
            using var stream = new MemoryStream(Zip(entries));
            using var zip = new ZipArchive(stream);
            Assert.Throws<InvalidDataException>(() => MiSansArchive.FindFont(zip));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadsExtractTheFontAndKeepUnknownHashes(bool fullZip)
    {
        byte[] font = Font(), padding = new byte[8 * 1024 * 1024];
        new Random(12).NextBytes(padding);
        using var handler = new ArchiveHandler(Zip(("large-unrelated.bin", padding), ("??/MiSansVF.ttf", font))) { IgnoreRange = fullZip };
        using var http = new HttpClient(handler);
        using var dir = new TempDirectory();
        var progress = new Reports();
        var hash = await MiSansArchive.DownloadAsync(http, dir.Path, progress, TestContext.Current.CancellationToken);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(font)), hash);
        Assert.NotEqual(MiSansArchive.KnownHash, hash);
        Assert.Equal(font, await File.ReadAllBytesAsync(System.IO.Path.Combine(dir.Path, "MiSansVF.ttf"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(System.IO.Path.Combine(dir.Path, "MiSans-License.pdf")));
        if (!fullZip) Assert.True(handler.BytesSent < handler.Zip.Length / 2, $"Downloaded {handler.BytesSent} of {handler.Zip.Length} bytes");
        Assert.Contains(progress.Values, p => p.Phase == FontDownloadPhase.Font && p.Completed == font.Length);
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length);
    }

    [Fact]
    public async Task IgnoredRangesUseASeekableTemporaryFile()
    {
        using var handler = new ArchiveHandler(Zip(("??/MiSansVF.ttf", Font()))) { IgnoreRange = true };
        using var http = new HttpClient(handler);
        var progress = new Reports();
        string path;
        using (var stream = await HttpRangeStream.OpenAsync(http, new Uri(MiSansArchive.ZipUrl), progress, TestContext.Current.CancellationToken))
        {
            var file = Assert.IsType<FileStream>(stream);
            path = file.Name;
            Assert.True(stream.CanSeek);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
            Assert.Equal(1100000, MiSansArchive.FindFont(zip).Length);
        }
        Assert.False(File.Exists(path));
        Assert.Contains(progress.Values, p => p.Phase == FontDownloadPhase.Archive && p.Completed == handler.Zip.Length);
    }

    [Fact]
    public async Task RangeStreamSeeksAcrossBuffersAndReadsEof()
    {
        byte[] bytes = new byte[3 * 1024 * 1024];
        new Random(1).NextBytes(bytes);
        using var handler = new ArchiveHandler(bytes);
        using var http = new HttpClient(handler);
        using var stream = await HttpRangeStream.OpenAsync(http, new Uri(MiSansArchive.ZipUrl), null, TestContext.Current.CancellationToken);
        var output = new byte[1500000];
        stream.Seek(300, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(bytes.AsSpan(300, output.Length).ToArray(), output);
        stream.Seek(-20, SeekOrigin.End);
        Assert.Equal(20, stream.Read(output, 0, 100));
        Assert.Equal(0, await stream.ReadAsync(output, TestContext.Current.CancellationToken));
        stream.Seek(-300, SeekOrigin.Current);
        Assert.Equal(bytes[^300], stream.ReadByte());
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
    }

    [Theory]
    [InlineData("range")]
    [InlineData("length")]
    [InlineData("etag")]
    [InlineData("truncated")]
    [InlineData("200")]
    public async Task RejectsInvalidOrChangingRangeResponses(string fault)
    {
        using var handler = new ArchiveHandler(new byte[1000]) { Fault = fault };
        using var http = new HttpClient(handler);
        using var stream = await HttpRangeStream.OpenAsync(http, new Uri(MiSansArchive.ZipUrl), null, TestContext.Current.CancellationToken);
        if (fault == "truncated")
            await Assert.ThrowsAsync<EndOfStreamException>(() => stream.ReadExactlyAsync(new byte[10], TestContext.Current.CancellationToken).AsTask());
        else if (fault == "200")
            await Assert.ThrowsAsync<RangeNotSupportedException>(() => stream.ReadExactlyAsync(new byte[10], TestContext.Current.CancellationToken).AsTask());
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => stream.ReadExactlyAsync(new byte[10], TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task RangeSupportLostAfterTheProbeRestartsWithTheWholeZip()
    {
        byte[] font = Font();
        using var handler = new ArchiveHandler(Zip(("MiSansVF.ttf", font))) { Fault = "200" };
        using var http = new HttpClient(handler);
        using var dir = new TempDirectory();
        var progress = new Reports();
        await MiSansArchive.DownloadAsync(http, dir.Path, progress, TestContext.Current.CancellationToken);
        Assert.Equal(font, await File.ReadAllBytesAsync(System.IO.Path.Combine(dir.Path, "MiSansVF.ttf"), TestContext.Current.CancellationToken));
        Assert.Contains(progress.Values, p => p.Phase == FontDownloadPhase.Archive && p.Completed == handler.Zip.Length);
    }

    [Fact]
    public async Task CancellationInterruptsAnInFlightRange()
    {
        using var stop = new CancellationTokenSource();
        using var handler = new ArchiveHandler(new byte[1000]) { BlockReads = true };
        using var http = new HttpClient(handler);
        using var stream = await HttpRangeStream.OpenAsync(http, new Uri(MiSansArchive.ZipUrl), null, stop.Token);
        var read = stream.ReadAsync(new byte[10], stop.Token).AsTask();
        await handler.Blocked.Task;
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationLeavesNoPublishedOrTemporaryFiles(bool fullZip)
    {
        using var stop = new CancellationTokenSource();
        using var handler = new ArchiveHandler(Zip(("MiSansVF.ttf", Font()))) { IgnoreRange = fullZip };
        using var http = new HttpClient(handler);
        using var dir = new TempDirectory();
        var progress = new Reports(_ => stop.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MiSansArchive.DownloadAsync(http, dir.Path, progress, stop.Token));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Theory]
    [InlineData("font")]
    [InlineData("licence")]
    [InlineData("network")]
    public async Task FailuresNeverPublishAnIncompleteDownload(string failure)
    {
        using var handler = new ArchiveHandler(Zip(("MiSansVF.ttf", failure == "font" ? new byte[1100000] : Font())))
        {
            BadLicence = failure == "licence", FailLicence = failure == "network",
        };
        using var http = new HttpClient(handler);
        using var dir = new TempDirectory();
        await Assert.ThrowsAnyAsync<Exception>(() => MiSansArchive.DownloadAsync(http, dir.Path, null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task SmallTrueTypeFilesAreRejected()
    {
        using var dir = new TempDirectory();
        var path = System.IO.Path.Combine(dir.Path, "tiny.ttf");
        await File.WriteAllBytesAsync(path, [0, 1, 0, 0], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => MiSansArchive.ValidateAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DiagnosticsIncludeTheFont()
    {
        var text = Diagnostics.Text("1", "Windows", "x64", null, null, false, null, null, false, false, 0, 0,
            font: "MiSans (downloaded, hash ok)");
        Assert.Contains("Font: MiSans (downloaded, hash ok)", text);
    }

    sealed class Reports(Action<FontDownloadProgress>? report = null) : IProgress<FontDownloadProgress>
    {
        public List<FontDownloadProgress> Values { get; } = [];
        public void Report(FontDownloadProgress value) { Values.Add(value); report?.Invoke(value); }
    }

    sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lyricod-font-test-" + Guid.NewGuid());
        public TempDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }

    sealed class ArchiveHandler(byte[] zip) : HttpMessageHandler
    {
        public byte[] Zip => zip;
        public long BytesSent;
        public bool IgnoreRange, BlockReads, BadLicence, FailLicence;
        public string? Fault;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested();
            if (request.RequestUri!.AbsolutePath.EndsWith(".pdf"))
                return new HttpResponseMessage(FailLicence ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new ByteArrayContent(Encoding.ASCII.GetBytes(BadLicence ? "<html>error</html>" : "%PDF-1.7\nlicence")) };
            if (request.Headers.Range == null)
            {
                BytesSent += zip.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            }
            var range = Assert.Single(request.Headers.Range.Ranges);
            long start = range.From!.Value, end = range.To!.Value;
            bool probe = start == 0 && end == 0;
            if (!probe && BlockReads)
            {
                Blocked.SetResult();
                await Task.Delay(Timeout.Infinite, stop);
            }
            if (IgnoreRange || (!probe && Fault == "200"))
            {
                BytesSent += zip.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
            }
            int count = checked((int)(end - start + 1));
            var data = zip.AsSpan((int)start, count - (!probe && Fault == "truncated" ? 1 : 0)).ToArray();
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(!probe && Fault == "range" ? start + 1 : start, end,
                zip.Length + (!probe && Fault == "length" ? 1 : 0));
            response.Headers.ETag = new EntityTagHeaderValue(!probe && Fault == "etag" ? "\"two\"" : "\"one\"");
            if (!probe) Assert.Equal("\"one\"", request.Headers.IfRange?.EntityTag?.Tag);
            BytesSent += data.Length;
            return response;
        }
    }
}
