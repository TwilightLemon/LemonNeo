using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Core.Playback;
using LemonNeo.Plugin.GdStudioNetease;
using LemonNeo.Plugin.QqMusic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class PlaybackProviderQualityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(AudioQuality.Automatic, "999")]
    [InlineData(AudioQuality.HiRes, "999")]
    [InlineData(AudioQuality.Lossless, "740")]
    [InlineData(AudioQuality.High, "320")]
    [InlineData(AudioQuality.Medium, "192")]
    [InlineData(AudioQuality.Low, "128")]
    public async Task GdStudioRequestsOneQualityAndReportsSilentlyDowngradedAudio(AudioQuality requested, string br)
    {
        var handler = new Responses("""{"url":"https://example.com/low.mp3","br":"128"}""");
        using var client = new HttpClient(handler);
        var plugin = new GdStudioNeteasePlugin();
        await plugin.InitializeAsync(new Context(client), Token);
        try
        {
            var result = await plugin.ResolvePlaybackAsync(new(plugin.Id, "123"), new(requested), Token);
            Assert.Contains($"&br={br}", Assert.Single(handler.Uris).Query);
            Assert.Equal(AudioQuality.Low, result!.Quality);
            Assert.Equal(128, result.BitrateKbps);
        }
        finally { await plugin.ShutdownAsync(Token); }
    }

    [Theory]
    [InlineData(320, AudioQuality.High)]
    [InlineData(192, AudioQuality.Medium)]
    [InlineData(900, AudioQuality.Lossless)]
    [InlineData(2500, AudioQuality.HiRes)]
    public async Task GdStudioInterpretsActualBitrateRatherThanRequestSelectors(int bitrate, AudioQuality expected)
    {
        var handler = new Responses($$"""{"url":"https://example.com/audio","br":{{bitrate}}}""");
        using var client = new HttpClient(handler);
        var plugin = new GdStudioNeteasePlugin();
        await plugin.InitializeAsync(new Context(client), Token);
        try
        {
            var source = await plugin.ResolvePlaybackAsync(new(plugin.Id, "123"), new(AudioQuality.HiRes), Token);
            Assert.Equal(expected, source!.Quality);
        }
        finally { await plugin.ShutdownAsync(Token); }
    }

    [Fact]
    public async Task QqDoesNotRetryLowerQualitiesWhenVkeyIsMissing()
    {
        var handler = new Responses("""{"req_0":{"code":0,"data":{"midurlinfo":[{"purl":""}]}}}""");
        using var client = new HttpClient(handler);
        var provider = new QqMusicPlaybackProvider(client, () => new("test", "1", 0), NullLogger<QqMusicPlaybackProvider>.Instance);
        Assert.Null(await provider.ResolvePlaybackAsync(new(provider.Id, "songmid"), new(AudioQuality.Lossless), Token));
        var uri = Assert.Single(handler.Uris);
        Assert.Contains("F000songmidsongmid.flac", Uri.UnescapeDataString(uri.Query));
    }

    [Theory]
    [InlineData(AudioQuality.Low, "M500", AudioQuality.Low)]
    [InlineData(AudioQuality.Medium, "C600", AudioQuality.Medium)]
    [InlineData(AudioQuality.High, "M800", AudioQuality.High)]
    [InlineData(AudioQuality.Lossless, "F000", AudioQuality.Lossless)]
    [InlineData(AudioQuality.Automatic, "F000", AudioQuality.Lossless)]
    public async Task QqResolvesOnlyItsSelectedFormat(AudioQuality requested, string prefix, AudioQuality actual)
    {
        var handler = new Responses("""{"req_0":{"code":0,"data":{"midurlinfo":[{"purl":"https://example.com/audio"}]}}}""");
        using var client = new HttpClient(handler);
        var provider = new QqMusicPlaybackProvider(client, () => new("test", "1", 0), NullLogger<QqMusicPlaybackProvider>.Instance);
        var source = await provider.ResolvePlaybackAsync(new(provider.Id, "songmid"), new(requested), Token);
        Assert.Equal(actual, source!.Quality);
        Assert.Contains(prefix + "songmidsongmid", Uri.UnescapeDataString(Assert.Single(handler.Uris).Query));
    }

    private sealed class Responses(string json) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Context(HttpClient client) : IPluginContext, IHttpClientFactory
    {
        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;
        public IHttpClientFactory HttpClientFactory => this;
        public ISettingsScope Settings => throw new NotSupportedException();
        public ICredentialVault Credentials => throw new NotSupportedException();
        public string CacheDirectory => throw new NotSupportedException();
        public HttpClient CreateClient(string name) => client;
        public void RegisterProvider(IMusicProvider provider) { }
        public void RegisterPlayer(IPlayerProvider player) { }
    }
}
