using LemonNeo.Abstractions.Playback;
using LemonNeo.Application;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Lyrics;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class CachePersistenceTests
{
    [Fact]
    public async Task ImageBytesRemainAvailableFromDiskWithoutTheirSource()
    {
        using var paths = new TemporaryPaths();
        var source = Path.Combine(paths.Root, "image.png");
        byte[] bytes = [137, 80, 78, 71, 13, 10, 26, 10];
        await File.WriteAllBytesAsync(source, bytes, TestContext.Current.CancellationToken);
        using var client = new HttpClient();
        await using (var cache = new ImageCache(paths.CacheDirectory, client, NullLogger<ImageCache>.Instance))
        {
            var image = await cache.GetAsync(new Uri(source), TestContext.Current.CancellationToken);
            Assert.False(image!.IsCached);
            Assert.Equal(bytes, image.Data.ToArray());
        }
        File.Delete(source);
        await using var reloaded = new ImageCache(paths.CacheDirectory, client, NullLogger<ImageCache>.Instance);
        var cached = await reloaded.GetAsync(new Uri(source), TestContext.Current.CancellationToken);
        Assert.True(cached!.IsCached);
        Assert.Equal(bytes, cached.Data.ToArray());
    }

    [Fact]
    public async Task LyricsModelsRoundTripWithTimingAndExternalIdentityAliases()
    {
        using var paths = new TemporaryPaths();
        var identity = new ExternalMusicId("isrc", "ABC000000001");
        var sourceId = new ExternalMusicId("netease", "123");
        var track = HostFixture.Track() with { ExternalIds = [identity] };
        var lyrics = new Lyrics("Hello", IsSynchronized: true,
            Lines: [new LyricsLine("Hello", Words: [new LyricsWord("Hello", 100, 500)])], SourceId: sourceId);
        var cache = new LyricsCache(paths.CacheDirectory, NullLogger<LyricsCache>.Instance);
        await cache.StoreAsync(track, lyrics, "lyrics-provider", TestContext.Current.CancellationToken);
        var reloaded = new LyricsCache(paths.CacheDirectory, NullLogger<LyricsCache>.Instance);
        var found = await reloaded.TryGetAsync(track with { ExternalIds = [sourceId] }, TestContext.Current.CancellationToken);
        Assert.Equal("lyrics-provider", found!.ProviderId);
        Assert.Equal("Hello", found.Lyrics!.Text);
        Assert.Equal(new LyricsWord("Hello", 100, 500), Assert.Single(Assert.Single(found.Lyrics.Lines!).Words!));
        Assert.Equal(sourceId, found.Lyrics.SourceId);
    }

    [Fact]
    public async Task CompletedAudioPlaysWithoutProvidersAndCorruptBytesAreRejected()
    {
        using var paths = new TemporaryPaths();
        var player = new TestPlayer();
        await using var host = new AppHost(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory),
            services => services.AddSingleton<IPlayerProvider>(player));
        await host.InitializeAsync(TestContext.Current.CancellationToken);
        var track = HostFixture.Track() with { ExternalIds = [new ExternalMusicId("isrc", "ABC000000001")] };
        var cache = host.Get<PlaybackAudioCache>();
        var request = new PlaybackRequest();
        var source = new HttpPlaybackSource { Uri = new Uri("https://unavailable.example/audio.mp3") };
        byte[] bytes = [1, 2, 3, 4, 5];
        using (var download = cache.Start(track, request, source))
        {
            Assert.NotNull(download);
            download.Write(bytes);
            download.Complete();
            Assert.Null(await cache.TryGetAsync(track, request, TestContext.Current.CancellationToken));
            download.SetExpectedLength(bytes.Length);
        }
        var resolver = host.Get<PlaybackResolver>();
        var cached = Assert.IsType<FilePlaybackSource>(await resolver.PlayAsync(track, request, TestContext.Current.CancellationToken));
        Assert.Equal(1, player.PlayCalls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(cached.Path, TestContext.Current.CancellationToken));
        await File.WriteAllBytesAsync(cached.Path, [5, 4, 3, 2, 1], TestContext.Current.CancellationToken);
        Assert.Null(await cache.TryGetAsync(track, request, TestContext.Current.CancellationToken));
    }
}
