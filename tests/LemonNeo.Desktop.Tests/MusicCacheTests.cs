using System.Text.Json;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class MusicCacheTests
{
    private static readonly ExternalMusicId SongId = new("qqmusic", "Ab123", "song-mid");
    private static readonly ExternalMusicId OtherId = new("netease", "456", "song-id");
    private static readonly HttpPlaybackSource HttpSource = new()
    {
        Uri = new Uri("https://audio.test/song.mp3?signature=expired"),
        CacheKey = "unused-provider-key"
    };
    private static readonly byte[] Audio = [1, 3, 5, 7, 9];
    private static readonly PlaybackRequest Request = new(AudioQuality.High, "source");

    [Fact]
    public async Task CachesShareExternalIdsAcrossTrackProvidersAndIgnoreIdOrderAndAdditionalIds()
    {
        using var paths = new TestPaths();
        var original = TrackWith(SongId, OtherId, SongId);
        var audio = AudioCache(paths);
        var lyrics = LyricsCache(paths);
        StoreAudio(audio, original, Request);
        await lyrics.StoreAsync(original, RichLyrics(), "lyrics-provider", cancellationToken: TestContext.Current.CancellationToken);

        // Restart the caches and change both components of Track.Id.
        audio = AudioCache(paths);
        lyrics = LyricsCache(paths);
        var snapshots = new[]
        {
            TrackWith(SongId with { Namespace = "QQMUSIC", Kind = "SONG-MID" }),
            TrackWith(OtherId),
            TrackWith(OtherId, new("spotify", "extra", "track-id"), SongId)
        };
        foreach (var snapshot in snapshots)
        {
            var anotherProvider = snapshot with { Id = new("another-provider", "unrelated-track-id") };
            var cachedAudio = Assert.IsType<FilePlaybackSource>(await audio.TryGetAsync(anotherProvider, Request, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(Audio, await File.ReadAllBytesAsync(cachedAudio.Path, cancellationToken: TestContext.Current.CancellationToken));
            var cachedLyrics = await lyrics.TryGetAsync(anotherProvider, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotNull(cachedLyrics);
            Assert.Equal("lyrics-provider", cachedLyrics.ProviderId);
            AssertRichLyrics(cachedLyrics.Lyrics);
        }
        Assert.Equal(3, Directory.GetFiles(Path.Combine(paths.CacheDirectory, "Music"), "*.json").Length);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(paths.CacheDirectory, "Lyrics"), "*.json").Length);
    }

    [Fact]
    public async Task CacheHitsLearnNewExternalAliasesWithoutRedownloading()
    {
        using var paths = new TestPaths();
        var audio = AudioCache(paths);
        var lyrics = LyricsCache(paths);
        StoreAudio(audio, TrackWith(SongId), Request);
        await lyrics.StoreAsync(TrackWith(SongId), new Lyrics("lyrics"), "lyrics-provider",
            cancellationToken: TestContext.Current.CancellationToken);
        var enriched = TrackWith(SongId, OtherId);
        Assert.NotNull(await audio.TryGetAsync(enriched, Request, TestContext.Current.CancellationToken));
        Assert.NotNull(await lyrics.TryGetAsync(enriched, TestContext.Current.CancellationToken));

        // A later snapshot has only the ID learned on the cache hit.
        Assert.NotNull(await AudioCache(paths).TryGetAsync(TrackWith(OtherId), Request, TestContext.Current.CancellationToken));
        Assert.NotNull(await LyricsCache(paths).TryGetAsync(TrackWith(OtherId), TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(Path.Combine(paths.CacheDirectory, "Music"), "*.mp3"));
        Assert.Empty(Directory.GetFiles(paths.CacheDirectory, "*.part", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("netease", "song-mid", "Ab123")]
    [InlineData("qqmusic", "song-id", "Ab123")]
    [InlineData("qqmusic", null, "Ab123")]
    [InlineData("qqmusic", "song-mid", "ab123")]
    public async Task CachesSeparateNamespaceTypeAndCaseSensitiveValue(string ns, string? kind, string value)
    {
        using var paths = new TestPaths();
        var audio = AudioCache(paths);
        var lyrics = LyricsCache(paths);
        var original = TrackWith(SongId);
        StoreAudio(audio, original, Request);
        await lyrics.StoreAsync(original, new Lyrics("original lyrics"), "lyrics-provider", cancellationToken: TestContext.Current.CancellationToken);

        // Keeping Track.Id identical must not make different external identities collide.
        var different = original with { ExternalIds = [new(ns, value, kind)] };
        Assert.Null(await audio.TryGetAsync(different, Request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await lyrics.TryGetAsync(different, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AudioSeparatesQualityAndRequiredSourceAndInvalidatesAllTrackAliases()
    {
        using var paths = new TestPaths();
        var audio = AudioCache(paths);
        var track = TrackWith(SongId, OtherId);
        StoreAudio(audio, track, Request);
        StoreAudio(audio, track, new PlaybackRequest(AudioQuality.Low, "source"));

        Assert.NotNull(await audio.TryGetAsync(track, Request with { RequiredSourceProviderId = "SOURCE" }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await audio.TryGetAsync(track, Request with { Quality = AudioQuality.Lossless }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await audio.TryGetAsync(track, Request with { RequiredSourceProviderId = "other-source" }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await audio.TryGetAsync(track, Request with { RequiredSourceProviderId = null }, cancellationToken: TestContext.Current.CancellationToken));
        audio.Invalidate(TrackWith(SongId), Request);
        Assert.Null(await audio.TryGetAsync(TrackWith(SongId), Request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await audio.TryGetAsync(TrackWith(OtherId), Request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await audio.TryGetAsync(track, new PlaybackRequest(AudioQuality.Low, "source"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CachesDoNotFallBackToTrackIdWhenExternalIdsAreAbsentOrInvalid()
    {
        using var paths = new TestPaths();
        var audio = AudioCache(paths);
        var lyrics = LyricsCache(paths);
        var track = TrackWith(new("", "value", "song-id"), new("qqmusic", " ", "song-mid"));
        Assert.Null(audio.Start(track, Request, HttpSource));
        await lyrics.StoreAsync(track, new Lyrics("lyrics"), "lyrics-provider", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await audio.TryGetAsync(track, Request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await lyrics.TryGetAsync(track, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(paths.CacheDirectory));
    }

    [Fact]
    public async Task LyricsSourceIdIsAnAliasAndFullLyricsRemainAvailableWithoutProviders()
    {
        using var paths = new TestPaths();
        var lyrics = LyricsCache(paths);
        await lyrics.StoreAsync(TrackWith(SongId), RichLyrics() with { SourceId = OtherId }, "lyrics-provider", cancellationToken: TestContext.Current.CancellationToken);
        var service = new LyricsService(new ProviderRegistry(), LyricsCache(paths));
        var result = await service.GetLyricsAsync(TrackWith(OtherId) with { Id = new("offline", "other") }, cancellationToken: TestContext.Current.CancellationToken);
        AssertRichLyrics(result.Lyrics);
        Assert.Equal(OtherId, result.Lyrics!.SourceId);
        Assert.Equal("lyrics-provider", result.ProviderId);
    }

    [Fact]
    public async Task InvalidAliasDoesNotHideAnotherValidAliasAndCorruptedAudioIsRejected()
    {
        using var paths = new TestPaths();
        var track = TrackWith(SongId, OtherId);
        var audio = AudioCache(paths);
        var lyrics = LyricsCache(paths);
        StoreAudio(audio, TrackWith(OtherId), Request);
        await lyrics.StoreAsync(TrackWith(OtherId), new Lyrics("lyrics"), "lyrics-provider", cancellationToken: TestContext.Current.CancellationToken);
        var musicDirectory = Path.Combine(paths.CacheDirectory, "Music");
        var firstAudioIndex = Assert.Single(Directory.GetFiles(musicDirectory, "*.json"));
        var firstLyricsIndex = Assert.Single(Directory.GetFiles(Path.Combine(paths.CacheDirectory, "Lyrics"), "*.json"));
        StoreAudio(audio, track, Request);
        await lyrics.StoreAsync(track, new Lyrics("lyrics"), "lyrics-provider", cancellationToken: TestContext.Current.CancellationToken);
        // The first sorted alias (NETEASE) is broken; QQMUSIC remains usable.
        await File.WriteAllTextAsync(firstAudioIndex, "{broken", cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(firstLyricsIndex, "{broken", cancellationToken: TestContext.Current.CancellationToken);
        var source = Assert.IsType<FilePlaybackSource>(await audio.TryGetAsync(track, Request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await lyrics.TryGetAsync(track, cancellationToken: TestContext.Current.CancellationToken));

        await File.WriteAllBytesAsync(source.Path, [9, 7, 5, 3, 1], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(await audio.TryGetAsync(track, Request, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InterruptedDownloadsPublishNoAliasesAndCompletedDownloadsKeepOriginalBytes()
    {
        using var paths = new TestPaths();
        var audio = AudioCache(paths);
        var track = TrackWith(SongId, OtherId);
        using (var sink = Assert.IsAssignableFrom<IPlaybackDownloadSink>(audio.Start(track, Request, HttpSource)))
        {
            sink.SetExpectedLength(Audio.Length);
            sink.Write(Audio.AsSpan(0, 2));
            Assert.Null(await audio.TryGetAsync(track, Request, cancellationToken: TestContext.Current.CancellationToken));
            sink.Complete();
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(paths.CacheDirectory, "Music")));

        using (var sink = Assert.IsAssignableFrom<IPlaybackDownloadSink>(audio.Start(track, Request, HttpSource)))
        {
            sink.Write(Audio);
            sink.Complete();
            Assert.Null(await audio.TryGetAsync(track, Request, cancellationToken: TestContext.Current.CancellationToken));
            sink.SetExpectedLength(Audio.Length);
        }
        Assert.NotNull(await audio.TryGetAsync(TrackWith(SongId), Request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await audio.TryGetAsync(TrackWith(OtherId), Request, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.Combine(paths.CacheDirectory, "Music"), "*.part"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossProviderDiscoverySavesExternalIdsAndRestoresOfflineWithoutSourcePlugin(bool hasOriginalId)
    {
        using var paths = new TestPaths();
        var original = hasOriginalId ? TrackWith(SongId) : TrackWith();
        var provider = new SourceProvider(TrackWith(OtherId) with { Id = new("source", "provider-track") });
        var player = new StreamingPlayer();
        var resolver = Resolver(paths, new ProviderRegistry([provider]), player);
        var repository = new MemoryPlaybackRepository();
        var queue = new PlaybackQueue();
        await using (var session = new PlaybackSessionService(resolver, queue, repository,
            NullLogger<PlaybackSessionService>.Instance))
        {
            await session.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
            await session.PlayTrackAsync(original, Request, cancellationToken: TestContext.Current.CancellationToken);
            var queued = Assert.Single(queue.Items);
            Assert.Equal(original.Id, queued.Track.Id);
            Assert.Equal(original.Title, queued.Track.Title);
            Assert.Contains(MusicCacheIdentity.Normalize(OtherId), queued.Track.ExternalIds);
            if (hasOriginalId) Assert.Contains(MusicCacheIdentity.Normalize(SongId), queued.Track.ExternalIds);
            Assert.Equal(1, player.Downloads);
        }

        var offlinePlayer = new StreamingPlayer();
        var offlineResolver = Resolver(paths, new ProviderRegistry(), offlinePlayer);
        await using var restoredSession = new PlaybackSessionService(offlineResolver, new PlaybackQueue(), repository,
            NullLogger<PlaybackSessionService>.Instance);
        await restoredSession.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);
        await restoredSession.PlayCurrentAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<FilePlaybackSource>(offlinePlayer.LastSource);
        Assert.Equal(0, offlinePlayer.Downloads);
        Assert.Equal(original.Id, restoredSession.Queue.CurrentItem!.Track.Id);
        Assert.Equal(1, provider.Resolutions);

        // The discovered catalogue ID also serves an unrelated Track snapshot.
        Assert.NotNull(await AudioCache(paths).TryGetAsync(TrackWith(OtherId), Request, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoveryReusesAudioAlreadyCachedUnderItsNewlyDiscoveredId()
    {
        using var paths = new TestPaths();
        StoreAudio(AudioCache(paths), TrackWith(OtherId), Request);
        var provider = new SourceProvider(TrackWith(OtherId) with { Id = new("source", "provider-track") });
        var player = new StreamingPlayer();
        var resolver = Resolver(paths, new ProviderRegistry([provider]), player);
        var result = await resolver.PlayAsync(TrackWith(), Request, cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<FilePlaybackSource>(result);
        Assert.Equal(0, player.Downloads);
        Assert.Contains(MusicCacheIdentity.Normalize(OtherId), resolver.CurrentQueueTrack!.ExternalIds);
        await resolver.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    private static Track TrackWith(params ExternalMusicId[] identifiers) => new()
    {
        Id = new("queue-provider", "queue-track"), Title = "Song", ExternalIds = identifiers
    };

    private static PlaybackAudioCache AudioCache(TestPaths paths) =>
        new(paths.CacheDirectory, NullLogger<PlaybackAudioCache>.Instance);

    private static LyricsCache LyricsCache(TestPaths paths) =>
        new(paths.CacheDirectory, NullLogger<LyricsCache>.Instance);

    private static void StoreAudio(PlaybackAudioCache cache, Track track, PlaybackRequest request)
    {
        using var sink = Assert.IsAssignableFrom<IPlaybackDownloadSink>(cache.Start(track, request, HttpSource));
        sink.SetExpectedLength(Audio.Length);
        sink.Write(Audio);
        sink.Complete();
    }

    private static Lyrics RichLyrics() => new("words", Source: "fixture", Lines:
        [new LyricsLine("words", 100, 200, Words: [new("words", 100, 200)],
            BackgroundLine: new("background", 100, 200))],
        TranslationLines: [new("translation", 100, 200)],
        RomanizationLines: [new([new("romanized", 100, 200)])]);

    private static void AssertRichLyrics(Lyrics? lyrics)
    {
        Assert.NotNull(lyrics);
        Assert.Equal("words", lyrics.Text);
        Assert.Equal("fixture", lyrics.Source);
        var line = Assert.Single(lyrics.Lines!);
        Assert.Equal(new LyricsWord("words", 100, 200), Assert.Single(line.Words!));
        Assert.Equal("background", line.BackgroundLine!.Text);
        Assert.Equal("translation", Assert.Single(lyrics.TranslationLines!).Text);
        Assert.Equal("romanized", Assert.Single(lyrics.RomanizationLines!).Text);
    }

    private static PlaybackResolver Resolver(TestPaths paths, ProviderRegistry registry, StreamingPlayer player) =>
        new(registry, registry, new PlayerRegistry([new PlayerProvider(player)]),
            new PlaybackSourceDiscoveryService(registry, new MetadataService(registry)), AudioCache(paths));

    private sealed class PlayerProvider(StreamingPlayer player) : IPlayerProvider
    {
        public string Id => "test-player";
        public PlayerDescriptor Descriptor { get; } = new("Test Player", "Silent download fixture");
        public int Priority => 0;
        public IPlayer Player => player;
        public bool CanPlay(PlaybackSource source) => true;
    }

    private sealed class StreamingPlayer : IStreamingPlayer
    {
        public int Downloads { get; private set; }
        public PlaybackSource? LastSource { get; private set; }
        public PlayerState State { get; private set; }
        public Track? CurrentTrack { get; private set; }
        public PlayerTimeline Timeline => new(TimeSpan.Zero, null);
        public Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default)
        {
            CurrentTrack = track;
            LastSource = source;
            State = PlayerState.Playing;
            return Task.CompletedTask;
        }
        public Task PlayAsync(Track track, HttpPlaybackSource source, IPlaybackDownloadSink downloadSink,
            CancellationToken cancellationToken = default)
        {
            Downloads++;
            downloadSink.SetExpectedLength(Audio.Length);
            downloadSink.Write(Audio);
            downloadSink.Complete();
            return PlayAsync(track, source, cancellationToken);
        }
        public Task PauseAsync(CancellationToken cancellationToken = default) { State = PlayerState.Paused; return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken cancellationToken = default) { State = PlayerState.Playing; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            State = PlayerState.Stopped;
            CurrentTrack = null;
            return Task.CompletedTask;
        }
    }

    private sealed class SourceProvider(Track track) :
        IMusicProvider, IPlaybackProvider, ITrackIdResolverProvider, IMetadataProvider
    {
        public string Id => "source";
        public ProviderDescriptor Descriptor { get; } = new("Test Source", "Cross-provider metadata fixture");
        public IReadOnlyCollection<string> MetadataNamespaces { get; } = [OtherId.Namespace];
        public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; } = [new(OtherId.Namespace, OtherId.Kind)];
        public int Resolutions { get; private set; }
        public Task<MetadataLookupResult> LookupMetadataAsync(MetadataLookupRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MetadataLookupResult([new MetadataMatch
            {
                MetadataProviderId = Id, SourceNamespace = OtherId.Namespace,
                Confidence = 0.95, MatchLevel = "fixture",
                Metadata = new TrackMetadata { Title = track.Title, ExternalIds = track.ExternalIds }
            }], new Dictionary<string, string>()));
        public Task<Track?> ResolveTrackIdAsync(ExternalMusicId identifier, TrackMetadata metadata,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Track?>(identifier == OtherId ? track : null);
        public Task<PlaybackSource?> ResolvePlaybackAsync(Track value, PlaybackRequest request,
            CancellationToken cancellationToken = default)
        {
            Resolutions++;
            return Task.FromResult<PlaybackSource?>(HttpSource);
        }
    }

    private sealed class MemoryPlaybackRepository : IPlaybackStateRepository
    {
        private string _json = JsonSerializer.Serialize(new PlaybackQueueState());
        public Task<PlaybackQueueState> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.Deserialize<PlaybackQueueState>(_json)!);
        public Task SaveAsync(PlaybackQueueState state, CancellationToken cancellationToken = default)
        {
            _json = JsonSerializer.Serialize(state);
            return Task.CompletedTask;
        }
    }
}
