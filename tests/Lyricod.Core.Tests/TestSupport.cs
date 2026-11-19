namespace Lyricod.Core.Tests;

/// <summary>A server that answers each request as the test says.</summary>
sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((request, _) => Task.FromResult(respond(request))) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}

sealed class ProgressLog<T>(Action<T>? report = null) : IProgress<T>
{
    public List<T> Values { get; } = [];
    public void Report(T value) { Values.Add(value); report?.Invoke(value); }
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lyricod-test-" + Guid.NewGuid());
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
