using System.Net;
using System.Text.Json;
using LemonNeo.Desktop.Services;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class GitHubReleaseClientTests
{
    [Theory]
    [InlineData("v0.1.10", "0.1.9", true)]
    [InlineData("v0.2.0", "0.1.99", true)]
    [InlineData("v0.1.3", "0.1.3", false)]
    [InlineData("v0.1.3", "0.1.3.0", false)]
    [InlineData("v0.1.3", "0.1.3+commit", false)]
    [InlineData("v0.1.3+build", "0.1.3", false)]
    [InlineData("v0.1.3", "0.1.3-rc.1+commit", true)]
    [InlineData("v0.1.3", "0.2.0-beta.1", false)]
    [InlineData("V1.0.0", "0.9.0", true)]
    [InlineData("0.1.3", "0.1.4", false)]
    public void ComparesReleaseAndBuildVersions(string latest, string current, bool expected)
    {
        Assert.Equal(expected, GitHubReleaseClient.IsNewerVersion(latest, current));
    }

    [Theory]
    [InlineData("nightly", "0.1.3")]
    [InlineData("v0.1.4-beta.1", "0.1.3")]
    [InlineData("vv0.1.4", "0.1.3")]
    [InlineData("v0.1.4", "unknown")]
    public void RejectsUncomparableVersions(string latest, string current)
    {
        Assert.Throws<InvalidDataException>(() => GitHubReleaseClient.IsNewerVersion(latest, current));
    }

    [Fact]
    public async Task UsesPublicApiHeadersAndTrustedReleaseUrl()
    {
        using var client = CreateClient(request =>
        {
            Assert.Equal("https://api.github.com/repos/TwilightLemon/LemonNeo/releases/latest", request.RequestUri?.AbsoluteUri);
            Assert.Contains("LemonNeo.Desktop/", request.Headers.UserAgent.ToString());
            Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
            Assert.True(request.Headers.Contains("X-GitHub-Api-Version"));
            Assert.Null(request.Headers.Authorization);
            return Json("""{"tag_name":"v0.1.4","html_url":"https://example.com/untrusted"}""");
        });

        var release = Assert.IsType<DesktopRelease>(await client.GetLatestAsync());
        Assert.Equal("v0.1.4", release.Version);
        Assert.Equal("https://github.com/TwilightLemon/LemonNeo/releases/tag/v0.1.4", release.ReleaseUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.2.0","draft":true}""")]
    [InlineData("""{"tag_name":"v0.2.0-beta.1","prerelease":true}""")]
    public async Task IgnoresDraftsAndPrereleases(string json)
    {
        using var client = CreateClient(_ => Json(json));
        Assert.Null(await client.GetLatestAsync());
    }

    [Fact]
    public async Task HandlesRepositoryWithoutStableReleases()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await client.GetLatestAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReportsHttpFailures(HttpStatusCode status)
    {
        using var client = CreateClient(_ => new HttpResponseMessage(status));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetLatestAsync());
        Assert.Equal(status, error.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"tag_name":null}""")]
    [InlineData("""{"tag_name":"nightly"}""")]
    [InlineData("""{"tag_name":"v0.2.0-beta.1"}""")]
    public async Task RejectsMissingOrInvalidReleaseVersions(string json)
    {
        using var client = CreateClient(_ => Json(json));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetLatestAsync());
    }

    [Fact]
    public async Task RejectsMalformedJson()
    {
        using var client = CreateClient(_ => Json("not json"));
        await Assert.ThrowsAsync<JsonException>(() => client.GetLatestAsync());
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        using var client = CreateClient(_ => Json("{}"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLatestAsync(cancellation.Token));
    }

    private static GitHubReleaseClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
