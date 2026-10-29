using System.Net;
using System.Net.Http.Headers;

namespace Lyricod.Core.Fonts;

/// <summary>A read-only ZIP source. Synchronous ZIP reads must run on a worker thread.</summary>
public sealed class HttpRangeStream : Stream
{
    const int ReadAhead = 1024 * 1024;
    readonly HttpClient _http;
    readonly Uri _url;
    readonly CancellationToken _stop;
    readonly string? _etag;
    readonly DateTimeOffset? _modified;
    readonly long _length;
    byte[] _buffer = [];
    long _bufferStart, _position;
    bool _disposed;

    HttpRangeStream(HttpClient http, Uri url, long length, HttpResponseMessage response, CancellationToken stop)
    {
        _http = http; _url = url; _length = length; _stop = stop;
        _etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
        _modified = response.Content.Headers.LastModified;
    }

    public static async Task<Stream> OpenAsync(HttpClient http, Uri url,
        IProgress<FontDownloadProgress>? progress, CancellationToken stop, bool useRanges = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (useRanges) request.Headers.Range = new RangeHeaderValue(0, 0);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (useRanges && response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is not { Unit: "bytes", From: 0, To: 0, Length: > 0 })
                throw new InvalidDataException("Invalid HTTP range response.");
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var probe = new byte[1];
            await body.ReadExactlyAsync(probe, timeout.Token).ConfigureAwait(false);
            return new HttpRangeStream(http, url, range.Length.Value, response, stop);
        }
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Expected a ZIP download.");
        // The server ignored Range. Keep the whole archive on disk, never in memory.
        var file = new FileStream(Path.Combine(Path.GetTempPath(), "lyricod-font-" + Guid.NewGuid() + ".zip"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            using var body = await response.Content.ReadAsStreamAsync(stop).ConfigureAwait(false);
            await CopyAsync(body, file, response.Content.Headers.ContentLength, FontDownloadPhase.Archive, progress, stop).ConfigureAwait(false);
            file.Position = 0;
            return file;
        }
        catch { await file.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static async Task CopyAsync(Stream source, Stream destination, long? total, FontDownloadPhase phase,
        IProgress<FontDownloadProgress>? progress, CancellationToken stop, long limit = 512L * 1024 * 1024)
    {
        byte[] buffer = new byte[131072];
        long done = 0;
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            int count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            if (count == 0) break;
            done += count;
            if (done > limit) throw new InvalidDataException("Download is too large.");
            await destination.WriteAsync(buffer.AsMemory(0, count), stop).ConfigureAwait(false);
            progress?.Report(new(phase, done, total));
        }
        if (total.HasValue && done != total.Value) throw new InvalidDataException("Incomplete download.");
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        timeout.Token.ThrowIfCancellationRequested();
        if (destination.Length == 0 || _position >= _length) return 0;
        if (_position < _bufferStart || _position >= _bufferStart + _buffer.Length)
        {
            long end = Math.Min(_length - 1, _position + ReadAhead - 1);
            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Range = new RangeHeaderValue(_position, end);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (_etag != null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(_etag));
            else if (_modified.HasValue) request.Headers.IfRange = new RangeConditionHeaderValue(_modified.Value);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.OK) throw new RangeNotSupportedException();
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                range.From != _position || range.To != end || range.Length != _length ||
                (_etag != null && response.Headers.ETag?.ToString() != _etag))
                throw new InvalidDataException("The ZIP changed or the server stopped supporting ranges. Retry the download.");
            var buffer = new byte[checked((int)(end - _position + 1))];
            using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await body.ReadExactlyAsync(buffer, timeout.Token).ConfigureAwait(false);
            _buffer = buffer;
            _bufferStart = _position;
        }
        int offset = checked((int)(_position - _bufferStart));
        int count = Math.Min(destination.Length, _buffer.Length - offset);
        _buffer.AsMemory(offset, count).CopyTo(destination);
        _position += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long position = checked(origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => _length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) });
        if (position < 0) throw new IOException("Cannot seek before the ZIP.");
        return _position = position;
    }
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override long Length => _length;
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { _disposed = true; _buffer = []; base.Dispose(disposing); }
}

internal sealed class RangeNotSupportedException() : IOException("The server ignored the HTTP range.");
