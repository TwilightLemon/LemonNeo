using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Core.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Application;
using LemonNeo.Application.Playback;
using LemonNeo.Plugin.GdStudioNetease;
using LemonNeo.Plugin.QqMusic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Buffers.Binary;
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

    [Theory]
    [InlineData(AudioQuality.Lossless)]
    [InlineData(AudioQuality.HiRes)]
    [InlineData(AudioQuality.Automatic)]
    public async Task QqDoesNotRetryLowerQualitiesWhenVkeyIsMissing(AudioQuality requested)
    {
        var handler = new Responses("""{"req_0":{"code":0,"data":{"midurlinfo":[{"purl":""}]}}}""");
        using var client = new HttpClient(handler);
        var provider = new QqMusicPlaybackProvider(client, () => new("test", "1", 0), NullLogger<QqMusicPlaybackProvider>.Instance);
        Assert.Null(await provider.ResolvePlaybackAsync(new(provider.Id, "songmid"), new(requested), Token));
        var uri = Assert.Single(handler.Uris);
        Assert.Contains("F000songmidsongmid.flac", Uri.UnescapeDataString(uri.Query));
    }

    [Theory]
    [InlineData(AudioQuality.Low, "M500", AudioQuality.Low)]
    [InlineData(AudioQuality.Medium, "C600", AudioQuality.Medium)]
    [InlineData(AudioQuality.High, "M800", AudioQuality.High)]
    [InlineData(AudioQuality.Lossless, "F000", AudioQuality.Lossless)]
    [InlineData(AudioQuality.HiRes, "F000", AudioQuality.Lossless)]
    [InlineData(AudioQuality.Automatic, "F000", AudioQuality.Lossless)]
    public async Task QqResolvesOnlyItsSelectedFormat(AudioQuality requested, string prefix, AudioQuality actual)
    {
        var handler = new Responses("""{"req_0":{"code":0,"data":{"midurlinfo":[{"purl":"https://example.com/audio"}]}}}""",
            FlacHeader(16, 44100));
        using var client = new HttpClient(handler);
        var provider = new QqMusicPlaybackProvider(client, () => new("test", "1", 0), NullLogger<QqMusicPlaybackProvider>.Instance);
        var source = await provider.ResolvePlaybackAsync(new(provider.Id, "songmid"), new(requested), Token);
        Assert.Equal(actual, source!.Quality);
        Assert.Contains(prefix + "songmidsongmid", Uri.UnescapeDataString(Assert.Single(handler.Uris, uri => uri.Host == "u.y.qq.com").Query));
        Assert.Equal(prefix == "F000" ? 2 : 1, handler.Uris.Count);
    }

    [Theory]
    [InlineData(AudioQuality.HiRes, 24, 44100, AudioQuality.HiRes)]
    [InlineData(AudioQuality.HiRes, 24, 96000, AudioQuality.HiRes)]
    [InlineData(AudioQuality.HiRes, 16, 96000, AudioQuality.HiRes)]
    [InlineData(AudioQuality.HiRes, 16, 48000, AudioQuality.Lossless)]
    [InlineData(AudioQuality.Automatic, 24, 44100, AudioQuality.HiRes)]
    [InlineData(AudioQuality.Lossless, 24, 44100, AudioQuality.HiRes)]
    public async Task QqReportsFlacResolutionFromTheFileInsteadOfTheRequestedTier(
        AudioQuality requested, int bitDepth, int sampleRate, AudioQuality expected)
    {
        var handler = new Responses("""{"req_0":{"code":0,"data":{"midurlinfo":[{"purl":"F000songmidsongmid.flac?vkey=test"}]}}}""",
            FlacHeader(bitDepth, sampleRate));
        using var client = new HttpClient(handler);
        var provider = new QqMusicPlaybackProvider(client, () => new("test", "1", 0), NullLogger<QqMusicPlaybackProvider>.Instance);

        var source = Assert.IsType<HttpPlaybackSource>(await provider.ResolvePlaybackAsync(new(provider.Id, "songmid"), new(requested), Token));

        Assert.Equal(expected, source.Quality);
        Assert.Equal(2, handler.Uris.Count);
        Assert.Equal(source.Uri, handler.Uris[1]);
        Assert.Equal("bytes=0-41", Assert.Single(handler.Ranges));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("not-flac")]
    [InlineData("invalid-streaminfo")]
    [InlineData("invalid-sample-rate")]
    [InlineData("http-error")]
    [InlineData("timeout")]
    public async Task QqKeepsTheUrlWithUnknownQualityWhenTheHeaderCannotBeVerified(string failure)
    {
        var header = FlacHeader(24, 44100);
        switch (failure)
        {
            case "truncated": header = header[..20]; break;
            case "not-flac": header[0] = 0; break;
            case "invalid-streaminfo": header[4] = 1; break;
            case "invalid-sample-rate": header = FlacHeader(24, 0); break;
        }
        var handler = new Responses("""{"req_0":{"code":0,"data":{"midurlinfo":[{"purl":"https://example.com/audio.flac"}]}}}""",
            header, failure);
        using var client = new HttpClient(handler);
        var provider = new QqMusicPlaybackProvider(client, () => new("test", "1", 0), NullLogger<QqMusicPlaybackProvider>.Instance);

        var source = Assert.IsType<HttpPlaybackSource>(await provider.ResolvePlaybackAsync(new(provider.Id, "songmid"), new(AudioQuality.HiRes), Token));

        Assert.Null(source.Quality);
        Assert.Equal(new Uri("https://example.com/audio.flac"), source.Uri);
        Assert.Equal(2, handler.Uris.Count);
    }

    [Fact]
    public async Task VerifiedQqHiResStopsSourceSearchAndCachesUnderItsActualQuality()
    {
        using var paths = new TemporaryPaths();
        var header = FlacHeader(24, 44100);
        var handler = new Responses("""{"req_0":{"code":0,"data":{"midurlinfo":[{"purl":"https://example.com/audio.flac"}]}}}""", header);
        using var client = new HttpClient(handler);
        var qq = new QqMusicPlaybackProvider(client, () => new("test", "1", 0), NullLogger<QqMusicPlaybackProvider>.Instance);
        var fallback = new FallbackProvider();
        await using var host = new AppHost(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory), services =>
        {
            services.AddSingleton<IMusicProvider>(qq);
            services.AddSingleton<IMusicProvider>(fallback);
        });
        var id = new ExternalMusicId("qqmusic", "songmid", "song-mid");
        var track = HostFixture.Track() with { ExternalIds = [id] };

        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(track, cancellationToken: Token);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(qq.Id, candidate.ProviderId);
        Assert.Equal(AudioQuality.HiRes, candidate.Source.Quality);
        Assert.Equal(0, fallback.Calls);
        Assert.False(result.MetadataExpanded);
        var cache = host.Get<PlaybackAudioCache>();
        using (var writer = cache.Start(candidate.SourceId, Assert.IsType<HttpPlaybackSource>(candidate.Source)))
        {
            Assert.NotNull(writer);
            writer.SetExpectedLength(header.Length);
            writer.Write(header);
            writer.Complete();
        }
        Assert.NotNull(await cache.TryGetAsync(id, AudioQuality.HiRes, Token));
        Assert.Null(await cache.TryGetAsync(id, AudioQuality.Lossless, Token));
    }

    private sealed class FallbackProvider : IMusicProvider, IPlaybackProvider
    {
        public string Id => "zz-fallback";
        public ProviderDescriptor Descriptor { get; } = new("Fallback", "Should not be reached after verified QQ HiRes.");
        public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes => [new("qqmusic", "song-mid")];
        public int Calls { get; private set; }
        public Task<PlaybackSource?> ResolvePlaybackAsync(MusicItemId trackId, PlaybackRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<PlaybackSource?>(null);
        }
    }

    private static byte[] FlacHeader(int bitDepth, int sampleRate)
    {
        var header = new byte[42];
        "fLaC"u8.CopyTo(header);
        header[4] = 0x80; // Last metadata block, type STREAMINFO.
        header[7] = 34;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 4096);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), 4096);
        const int channels = 2;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(18),
            ((ulong)sampleRate << 44) | ((ulong)(channels - 1) << 41) | ((ulong)(bitDepth - 1) << 36));
        return header;
    }

    private sealed class Responses(string json, byte[]? audioHeader = null, string? failure = null) : HttpMessageHandler
    {
        public List<Uri> Uris { get; } = [];
        public List<string> Ranges { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!);
            if (request.Headers.Range is { } range)
            {
                Ranges.Add(range.ToString());
                Assert.Null(request.Headers.Authorization);
                Assert.False(request.Headers.Contains("Cookie"));
                if (failure == "timeout") throw new TaskCanceledException("Probe timed out.");
                return Task.FromResult(new HttpResponseMessage(failure == "http-error" ? HttpStatusCode.Forbidden : HttpStatusCode.PartialContent)
                    { Content = new ByteArrayContent(audioHeader ?? []) });
            }
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
