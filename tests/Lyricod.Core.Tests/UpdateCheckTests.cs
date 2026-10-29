using System.Net;
using System.Text.Json;

namespace Lyricod.Core.Tests;

public class UpdateCheckTests
{
    static object Release(string tag, bool draft = false, bool prerelease = false) =>
        new { tag_name = tag, html_url = $"https://github.com/Indarkdust/Lyricod-App/releases/tag/{tag}", draft, prerelease };

    [Fact]
    public void NewestStableRelease()
    {
        var json = JsonSerializer.Serialize(new[] { Release("v0.9.0"), Release("nightly-99"),
            Release("v90.0.0", draft: true), Release("v80.0.0", prerelease: true),
            Release("v0.10.0"), Release("v0.8.0"), Release("v1.2"), Release("v1.2.3-beta") });
        var result = UpdateCheck.Evaluate(json, 200, "0.4.5");
        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal(new Version(0, 10, 0), result.Release!.Version);
        Assert.EndsWith("/v0.10.0", result.Release.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("0.4.5")]
    [InlineData("0.4.6")]
    public void CurrentOrNewerBuildIsUpToDate(string current) => Assert.Equal(UpdateStatus.UpToDate,
        UpdateCheck.Evaluate(JsonSerializer.Serialize(new[] { Release("v0.4.5") }), 200, current).Status);

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.3-beta")]
    [InlineData("v1.2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("1.２.3")]
    [InlineData("999999999999999999999999.0.0")]
    public void MalformedVersions(string version) => Assert.Null(UpdateCheck.ParseVersion(version));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("null")]
    [InlineData("[]")]
    public void MalformedOrMissingReleases(string json) =>
        Assert.Equal(UpdateStatus.CouldntCheck, UpdateCheck.Evaluate(json, 200, "0.4.5").Status);

    [Fact]
    public void InvalidRunningVersionAndHttpFailure()
    {
        var json = JsonSerializer.Serialize(new[] { Release("v1.0.0") });
        Assert.Equal(UpdateStatus.CouldntCheck, UpdateCheck.Evaluate(json, 200, "dev").Status);
        Assert.Equal(UpdateStatus.CouldntCheck, UpdateCheck.Evaluate(json, 404, "0.4.5").Status);
        Assert.Equal(UpdateStatus.CouldntCheck, UpdateCheck.Evaluate(json, 500, "0.4.5").Status);
    }

    [Fact]
    public void CheckSchedule()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(UpdateCheck.IsDue(null, now));
        Assert.False(UpdateCheck.IsDue(now.AddSeconds(-86399), now));
        Assert.True(UpdateCheck.IsDue(now.AddDays(-1), now));
        Assert.True(UpdateCheck.IsDue(now.AddSeconds(1), now));
    }

    [Fact]
    public async Task RequestHeadersAnd404()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(UpdateCheck.Endpoint, request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
            Assert.Contains("Lyricod/0.4.5", request.Headers.UserAgent.ToString());
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }));
        Assert.Equal(UpdateStatus.CouldntCheck, (await UpdateCheck.FetchAsync(client, "0.4.5")).Status);
    }

    [Fact]
    public async Task NetworkFailure()
    {
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException("offline")));
        Assert.Equal(UpdateStatus.CouldntCheck, (await UpdateCheck.FetchAsync(client, "0.4.5")).Status);
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
