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
    public async Task LyricsModelsRoundTripOnlyUnderTheirSourceIdentity()
    {
        using var paths = new TemporaryPaths();
        var identity = new ExternalMusicId("isrc", "ABC000000001");
        var sourceId = new ExternalMusicId("netease", "123");
        var lyrics = new Lyrics("Hello", IsSynchronized: true,
            Lines: [new LyricsLine("Hello", Words: [new LyricsWord("Hello", 100, 500)])], SourceId: sourceId);
        var cache = new LyricsCache(paths.CacheDirectory, NullLogger<LyricsCache>.Instance);
        await cache.StoreAsync(sourceId, lyrics, "lyrics-provider", TestContext.Current.CancellationToken);
        var reloaded = new LyricsCache(paths.CacheDirectory, NullLogger<LyricsCache>.Instance);
        Assert.Null(await reloaded.TryGetAsync(identity, TestContext.Current.CancellationToken));
        var found = await reloaded.TryGetAsync(sourceId, TestContext.Current.CancellationToken);
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
        var request = new PlaybackRequest(AudioQuality.High);
        var identity = track.ExternalIds[0];
        var source = new HttpPlaybackSource { Uri = new Uri("https://unavailable.example/audio.mp3"), Quality = AudioQuality.High };
        byte[] bytes = [1, 2, 3, 4, 5];
        using (var download = cache.Start(identity, source))
        {
            Assert.NotNull(download);
            download.Write(bytes);
            download.Complete();
            Assert.Null(await cache.TryGetAsync(identity, AudioQuality.High, TestContext.Current.CancellationToken));
            download.SetExpectedLength(bytes.Length);
        }
        var resolver = host.Get<PlaybackResolver>();
        var cached = Assert.IsType<FilePlaybackSource>(await resolver.PlayAsync(track, request, TestContext.Current.CancellationToken));
        Assert.Equal(1, player.PlayCalls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(cached.Path, TestContext.Current.CancellationToken));
        await File.WriteAllBytesAsync(cached.Path, [5, 4, 3, 2, 1], TestContext.Current.CancellationToken);
        Assert.Null(await cache.TryGetAsync(identity, AudioQuality.High, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task AudioAndLyricsRejectOtherCacheVersions()
    {
        using var paths = new TemporaryPaths();
        var id = new ExternalMusicId("catalog", "1", "song-id");
        var audio = new PlaybackAudioCache(paths.CacheDirectory, NullLogger<PlaybackAudioCache>.Instance);
        var source = new HttpPlaybackSource { Uri = new("https://example.com/audio.mp3"), Quality = AudioQuality.High };
        using (var writer = audio.Start(id, source))
        {
            Assert.NotNull(writer);
            writer.SetExpectedLength(1);
            writer.Write([1]);
            writer.Complete();
        }
        var lyrics = new LyricsCache(paths.CacheDirectory, NullLogger<LyricsCache>.Instance);
        await lyrics.StoreAsync(id, new Lyrics("Text", SourceId: id), "provider", TestContext.Current.CancellationToken);
        foreach (var directory in new[] { "Music", "Lyrics" })
        {
            var file = Assert.Single(Directory.GetFiles(Path.Combine(paths.CacheDirectory, directory), "*.json"));
            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken))!;
            var property = directory == "Music" ? "FormatVersion" : "formatVersion";
            Assert.Equal(1, json[property]!.GetValue<int>());
            json[property] = 2;
            await File.WriteAllTextAsync(file, json.ToJsonString(), TestContext.Current.CancellationToken);
        }
        Assert.Null(await audio.TryGetAsync(id, AudioQuality.High, TestContext.Current.CancellationToken));
        Assert.Null(await lyrics.TryGetAsync(id, TestContext.Current.CancellationToken));
    }

}
