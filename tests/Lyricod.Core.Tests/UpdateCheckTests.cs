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
    [InlineData("1.2.-3")]
    [InlineData("1.２.3")]
    [InlineData("999999999999999999999999.0.0")]
    public void MalformedVersions(string version) => Assert.Null(UpdateCheck.ParseVersion(version));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[]")]
    public void MalformedOrMissingReleases(string json) =>
        Assert.Equal(UpdateStatus.CouldntCheck, UpdateCheck.Evaluate(json, 200, "0.4.5").Status);

    [Fact]
    public void InvalidRunningVersionAndHttpFailure()
    {
        var json = JsonSerializer.Serialize(new[] { Release("v1.0.0") });
        Assert.Equal(UpdateStatus.CouldntCheck, UpdateCheck.Evaluate(json, 200, "dev").Status);
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
        using var client = new HttpClient(new StubHandler(request =>
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
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        var result = await UpdateCheck.FetchAsync(client, "0.4.5");
        Assert.Equal(UpdateStatus.CouldntCheck, result.Status);
        Assert.Equal(CheckFailure.Unreachable, result.Failure);
    }

    static readonly DateTimeOffset Now = new(2026, 11, 27, 3, 0, 0, TimeSpan.Zero);

    static HttpResponseMessage Answer(HttpStatusCode code, params (string, string)[] headers)
    {
        var response = new HttpResponseMessage(code) { Content = new StringContent("{}") };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    [Fact]
    public void RateLimitSaysWhenToTryAgain()
    {
        // GitHub's answer once an address has used its 60 requests for the hour.
        var reset = Now.AddMinutes(37);
        var result = UpdateCheck.Explain(Answer(HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString())), Now);
        Assert.Equal(CheckFailure.RateLimited, result.Failure);
        Assert.Equal(reset, result.RetryAt);

        result = UpdateCheck.Explain(Answer(HttpStatusCode.TooManyRequests, ("retry-after", "120")), Now);
        Assert.Equal(CheckFailure.RateLimited, result.Failure);
        Assert.Equal(Now.AddSeconds(120), result.RetryAt);

        // Limited, with no time given or one already past: no time to name.
        Assert.Null(UpdateCheck.Explain(Answer(HttpStatusCode.TooManyRequests), Now).RetryAt);
        Assert.Null(UpdateCheck.Explain(Answer(HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", Now.AddMinutes(-1).ToUnixTimeSeconds().ToString())), Now).RetryAt);
        Assert.Null(UpdateCheck.Explain(Answer(HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "0"),
            ("x-ratelimit-reset", "99999999999999999")), Now).RetryAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, CheckFailure.BadReply)] // refused, but not for the rate limit
    [InlineData(HttpStatusCode.NotFound, CheckFailure.BadReply)]
    [InlineData(HttpStatusCode.OK, CheckFailure.BadReply)] // a page that isn't a release list (a captive portal)
    [InlineData(HttpStatusCode.BadGateway, CheckFailure.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, CheckFailure.ServerError)]
    public void OtherFailures(HttpStatusCode code, CheckFailure failure) =>
        Assert.Equal(failure, UpdateCheck.Explain(Answer(code, ("x-ratelimit-remaining", "41")), Now).Failure);

    [Fact]
    public async Task FetchSaysWhyItFailed()
    {
        using var limited = new HttpClient(new StubHandler(_ => Answer(HttpStatusCode.Forbidden, ("x-ratelimit-remaining", "0"))));
        Assert.Equal(CheckFailure.RateLimited, (await UpdateCheck.FetchAsync(limited, "0.4.5")).Failure);
        using var garbled = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") }));
        Assert.Equal(CheckFailure.BadReply, (await UpdateCheck.FetchAsync(garbled, "0.4.5")).Failure);
        using var fine = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new[] { Release("v0.4.5") })) }));
        Assert.Equal(CheckFailure.None, (await UpdateCheck.FetchAsync(fine, "0.4.5")).Failure);
    }}
