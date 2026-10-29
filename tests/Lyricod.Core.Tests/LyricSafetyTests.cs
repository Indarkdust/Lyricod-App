using System.IO.Compression;
using System.Net;
using System.Text;

namespace Lyricod.Core.Tests;

public class LyricSafetyTests
{
    [Theory]
    [InlineData("NaN:00")]
    [InlineData("Infinity:00")]
    [InlineData("1e309:00")]
    [InlineData("-1:00")]
    [InlineData("01:-1")]
    [InlineData("00:NaN")]
    [InlineData("1440:01")]
    public void LrcAndLrclibSkipBadTimes(string time)
    {
        Assert.Null(LyricParser.ParseTimestamp(time));
        var raw = new RawLyrics { Source = "LRCLIB", Lrc = $"[{time}]bad\n[00:01]good" };
        var line = Assert.Single(raw.Parse()!.Lines);
        Assert.Equal("good", line.Text);
        Assert.Equal(1, line.Start);
        Assert.Null(LyricParser.ParseLrc($"[{time}]bad", null, "t"));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1e309")]
    [InlineData("86400001")]
    [InlineData("\"NaN\"")]
    [InlineData("null")]
    public void JsonCreditTimesAreValidatedInLrcAndYrc(string time)
    {
        var credit = "{\"t\":" + time + ",\"c\":[{\"tx\":\"bad\"}]}\n";
        Assert.Equal("good", Assert.Single(LyricParser.ParseLrc(credit + "[00:01]good", null, "t")!.Lines).Text);
        Assert.Equal("good", Assert.Single(LyricParser.ParseYrc(credit + "[1000,1000](1000,1000,0)good", null, "t")!.Lines).Text);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e309")]
    [InlineData("-1")]
    [InlineData("86400001")]
    [InlineData("not-a-number")]
    public void YrcAndQrcSkipBadLinesAndFallBackForBadWords(string bad)
    {
        foreach (bool qrc in new[] { false, true })
        {
            Lyrics? Parse(string text) => qrc ? LyricParser.ParseQrc(text, null, "t") : LyricParser.ParseYrc(text, null, "t");
            string Word(string start, string duration, string text) => qrc ? $"{text}({start},{duration})" : $"({start},{duration},0){text}";
            var good = "[1000,2000]" + Word("1000", "1000", "good");
            foreach (var header in new[] { $"[{bad},1000]", $"[1000,{bad}]", "[86400000,1]" })
                Assert.Equal("good", Assert.Single(Parse(header + Word("1000", "1000", "bad") + "\n" + good)!.Lines).Text);
            foreach (var (start, duration) in new[] { (bad, "1000"), ("1000", bad), ("86400000", "1") })
            {
                var lyrics = Parse("[1000,2000]" + Word(start, duration, "Hello ") + Word("2000", "1000", "world") + "\n" + good)!;
                Assert.Equal("Hello world", lyrics.Lines[0].Text);
                Assert.Null(lyrics.Lines[0].Words);
                Assert.NotNull(lyrics.Lines[1].Words);
            }
        }
    }

    [Fact]
    public void WordParsingKeepsOrdinaryParenthesesWithCommas()
    {
        var yrc = LyricParser.ParseYrc("[1000,3000](1000,1000,0)Hello (oh, dear)(2000,1000,0) world", null, "t")!;
        var qrc = LyricParser.ParseQrc("[1000,3000]Hello (oh, dear)(1000,1000) world(2000,1000)", null, "t")!;
        foreach (var lyrics in new[] { yrc, qrc })
        {
            Assert.Equal("Hello (oh, dear) world", lyrics.Lines[0].Text);
            Assert.Equal(2, lyrics.Lines[0].Words!.Count);
        }
    }

    [Fact]
    public void IncompleteWordTagsFallBackToLineTiming()
    {
        var yrc = LyricParser.ParseYrc("[1000,3000](1000,,0)Hello (2000,1000)world", null, "t")!;
        var qrc = LyricParser.ParseQrc("[1000,3000]Hello (1000,)world(2000,1000,0)", null, "t")!;
        foreach (var lyrics in new[] { yrc, qrc })
        {
            Assert.Equal("Hello world", lyrics.Lines[0].Text);
            Assert.Null(lyrics.Lines[0].Words);
        }
    }

    [Theory]
    [InlineData("NaN:00")]
    [InlineData("1e309:00")]
    [InlineData("-1:00")]
    [InlineData("1440:01")]
    [InlineData("00:00")]
    public void EnhancedLrcFallsBackToLineText(string bad)
    {
        var line = Assert.Single(LyricParser.ParseLrc($"[00:01]<00:01>Hello <{bad}>world", null, "t")!.Lines);
        Assert.Equal("Hello world", line.Text);
        Assert.Null(line.Words);
    }

    [Fact]
    public void TimesAtTheLimitHaveBoundedEnds()
    {
        var lrc = LyricParser.ParseLrc("[1440:00]last", null, "t")!;
        Assert.Equal(86400, Assert.Single(lrc.Lines).End);
        var yrc = LyricParser.ParseYrc("{\"t\":86400000,\"c\":[{\"tx\":\"credit\"}]}\n[86400000,0](86400000,0,0)last", null, "t")!;
        Assert.All(yrc.Lines, l => Assert.Equal(86400, l.End));
        Assert.Equal(86400, yrc.Lines[1].Words![0].End);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpContentIsCountedEvenWithoutAnAccurateLength(bool declared)
    {
        using var stream = new CountingStream(LyricData.MaxBytes + 1);
        using var content = new StreamContent(stream);
        if (declared) content.Headers.ContentLength = 1; // a false small length cannot bypass counting
        await Assert.ThrowsAsync<InvalidDataException>(() => LyricData.ReadText(content, TestContext.Current.CancellationToken));
        Assert.InRange(stream.BytesRead, LyricData.MaxBytes + 1, LyricData.MaxBytes + 8192);
    }

    [Fact]
    public async Task OversizedContentLengthIsRejectedBeforeReading()
    {
        using var stream = new CountingStream(1);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = LyricData.MaxBytes + 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => LyricData.ReadText(content, TestContext.Current.CancellationToken));
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task HttpContentAtTheLimitIsAccepted()
    {
        using var stream = new CountingStream(LyricData.MaxBytes);
        using var content = new StreamContent(stream);
        Assert.Equal(LyricData.MaxBytes, (await LyricData.ReadText(content, TestContext.Current.CancellationToken)).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderCapsJsonAndQrcTextResponses(bool qrc)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lyricod-limits", Guid.NewGuid().ToString());
        var stream = new CountingStream(LyricData.MaxBytes + 100000);
        using var handler = new ResponseHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = !qrc || request.RequestUri!.AbsolutePath.Contains("lyric_download")
                ? new StreamContent(stream) : new StringContent("{}"),
        });
        try
        {
            var provider = new LyricsProvider(dir, "test", handler);
            if (qrc) Assert.Null(await provider.FetchQqSong("test", 1, TestContext.Current.CancellationToken));
            else Assert.Null(await provider.FetchLrclib("1", TestContext.Current.CancellationToken));
            Assert.InRange(stream.BytesRead, LyricData.MaxBytes + 1, LyricData.MaxBytes + 8192);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void QrcInflationHasTheSameLimit(int excess)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(Encoding.UTF8.GetBytes(new string('a', LyricData.MaxBytes + excess)));
        var result = QrcDecrypter.Inflate(compressed.ToArray());
        if (excess == 0) Assert.Equal(LyricData.MaxBytes, result?.Length);
        else Assert.Null(result);
    }

    sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    sealed class CountingStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(count, length - BytesRead);
            buffer.AsSpan(offset, read).Fill((byte)'a');
            BytesRead += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
