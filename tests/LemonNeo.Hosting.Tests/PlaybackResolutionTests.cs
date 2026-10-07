using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Application;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Plugin.LocalMusic;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class PlaybackResolutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ExternalMusicId Id(string value) => new("catalog", value, "song-id");
    private static Track Track(params string[] ids) => HostFixture.Track() with { ExternalIds = ids.Select(Id).ToArray() };
    private static HttpPlaybackSource Audio(AudioQuality quality, string id = "audio") =>
        new() { Uri = new($"https://example.com/{id}.flac"), Quality = quality };

    [Theory]
    [InlineData(AudioQuality.Automatic, false)]
    [InlineData(AudioQuality.HiRes, true)]
    [InlineData(AudioQuality.Low, false)]
    public async Task MatchedLocalFilesWinBeforeRemoteAudioOrCachesRegardlessOfQuality(AudioQuality quality, bool freshCatalog)
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "original.mp3");
        await File.WriteAllBytesAsync(path, [1], Token);
        var local = new LocalMusicPlugin();
        var track = Assert.Single((await local.ScanFolderAsync(new(paths.Root), Token)).Tracks);
        var fileId = Assert.Single(track.ExternalIds);
        // Metadata matching may put several remote identifiers ahead of the original file.
        track = track with { ExternalIds = [Id("lyrics-1"), Id("lyrics-2"), new("qqmusic", "mid", "song-mid"), fileId] };
        var remote = new Source("remote", (_, _) => Audio(AudioQuality.HiRes));
        var metadata = new Metadata();
        await using var host = Host(paths, freshCatalog ? new LocalMusicPlugin() : local, remote, metadata);
        Cache(host, Id("lyrics-1"), AudioQuality.HiRes);

        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(track, new(quality), Token);

        var selected = Assert.Single(result.Candidates);
        Assert.Equal(path, Assert.IsType<FilePlaybackSource>(selected.Source).Path);
        Assert.Equal(fileId, selected.SourceId);
        Assert.Null(selected.Source.Quality); // Preference is not a claim about the file's resolution.
        Assert.False(selected.IsCached);
        Assert.Same(track, result.UpdatedTrack);
        Assert.Empty(remote.Calls);
        Assert.Equal(0, metadata.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingLocalFileFallsBackToKnownRemoteAudioOrCache(bool cached)
    {
        using var paths = new TemporaryPaths();
        var remote = new Source("remote", (_, _) => Audio(AudioQuality.HiRes));
        var metadata = new Metadata();
        await using var host = Host(paths, new LocalMusicPlugin(), remote, metadata);
        var track = Track("lyrics") with
        {
            Id = new("local", "original-hash"),
            ExternalIds = [new("file", Path.Combine(paths.Root, "missing.mp3"), "path"), Id("lyrics")]
        };
        if (cached) Cache(host, Id("lyrics"), AudioQuality.HiRes);

        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(track, cancellationToken: Token);

        var selected = Assert.Single(result.Candidates);
        Assert.Equal(Id("lyrics"), selected.SourceId);
        Assert.Equal(cached, selected.IsCached);
        Assert.Equal(cached ? 0 : 1, remote.Calls.Count);
        Assert.Equal(0, metadata.Calls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExplicitRemoteSelectionOverridesTheLocalFile(bool selectProvider, bool selectId)
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "original.flac");
        await File.WriteAllBytesAsync(path, [1], Token);
        var remote = new Source("remote", (_, _) => Audio(AudioQuality.HiRes));
        await using var host = Host(paths, new LocalMusicPlugin(), remote);
        var track = Track("lyrics") with { ExternalIds = [new("file", path, "path"), Id("lyrics")] };

        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(track,
            new(RequiredSourceProviderId: selectProvider ? remote.Id : null, RequiredSourceId: selectId ? Id("lyrics") : null), Token);

        Assert.IsType<HttpPlaybackSource>(Assert.Single(result.Candidates).Source);
        Assert.Equal(Id("lyrics"), Assert.Single(result.Candidates).SourceId);
        Assert.Single(remote.Calls);
    }

    [Fact]
    public async Task LocalPreferenceDoesNotHideRemoteChoicesOnTheDetailsPage()
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "original.flac");
        await File.WriteAllBytesAsync(path, [1], Token);
        var fileId = new ExternalMusicId("file", path, "path");
        var remote = new Source("remote", (_, _) => Audio(AudioQuality.HiRes));
        var metadata = new Metadata();
        await using var host = Host(paths, new LocalMusicPlugin(), remote, metadata);
        var track = Track("lyrics") with { ExternalIds = [fileId, Id("lyrics")] };

        var result = await host.Get<PlaybackSourceDiscoveryService>().FindKnownAsync(track, cancellationToken: Token);

        Assert.Equal(new[] { fileId, Id("lyrics") }, result.Candidates.Select(candidate => candidate.SourceId));
        Assert.IsType<FilePlaybackSource>(result.Candidates[0].Source);
        Assert.IsType<HttpPlaybackSource>(result.Candidates[1].Source);
        Assert.Equal(0, metadata.Calls);
    }

    [Fact]
    public async Task EachIdentityChecksItsCacheThenOnlineBeforeMovingToTheNextIdentity()
    {
        using var paths = new TemporaryPaths();
        var provider = new Source("source", (_, request) => Audio(request.Quality));
        var metadata = new Metadata();
        await using var host = Host(paths, provider, metadata);
        Cache(host, Id("2"), AudioQuality.HiRes);
        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(Track("1", "2"), cancellationToken: Token);
        Assert.False(Assert.Single(result.Candidates).IsCached);
        Assert.Equal("1", Assert.Single(provider.Calls).Id.RemoteId);
        Assert.Equal(0, metadata.Calls);
        provider.Calls.Clear();
        Cache(host, Id("1"), AudioQuality.HiRes);
        result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(Track("1", "2"), cancellationToken: Token);
        Assert.True(Assert.Single(result.Candidates).IsCached);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task ExpansionIsOnceInConfidenceOrderAndPersistsToQueueAndLibrary()
    {
        using var paths = new TemporaryPaths();
        var provider = new Source("source", (id, request) =>
            id.RemoteId == "best" && request.Quality == AudioQuality.High ? Audio(AudioQuality.High) : null);
        var metadata = new Metadata(("worse", .9), ("best", .98), ("rejected", .5));
        await using var host = Host(paths, provider, metadata);
        await host.InitializeAsync(Token);
        var playlists = host.Get<PlaylistService>();
        var playlist = playlists.Create("Saved");
        playlists.Add(playlist.Id, Track("known"));
        var session = host.Get<PlaybackSessionService>();
        await session.PlayTrackAsync(Track("known"), cancellationToken: Token);
        Assert.Equal(1, metadata.Calls);
        Assert.Equal(new[] { "known", "best", "worse" }, provider.Calls.Take(3).Select(c => c.Id.RemoteId));
        Assert.Equal(new[] { AudioQuality.HiRes, AudioQuality.Lossless, AudioQuality.High },
            provider.Calls.Select(c => c.Request.Quality).Distinct());
        Assert.Equal(AudioQuality.High, session.Snapshot.Quality);
        Assert.Contains(session.Queue.CurrentItem!.Track.ExternalIds, id => MusicCacheIdentity.GetKey(id) == MusicCacheIdentity.GetKey(Id("best")));
        Assert.DoesNotContain(session.Queue.CurrentItem.Track.ExternalIds, id => id.Value == "rejected");
        Assert.Contains(Id("best"), Assert.Single(playlist.Items).Track.ExternalIds);
        await session.PlayCurrentAsync(Token);
        Assert.Equal(2, metadata.Calls);
    }

    [Fact]
    public async Task ExpandedHighQualityWinsOverKnownDowngradedAudio()
    {
        using var paths = new TemporaryPaths();
        var provider = new Source("source", (id, _) => Audio(id.RemoteId == "expanded" ? AudioQuality.HiRes : AudioQuality.Low));
        var metadata = new Metadata(("expanded", .99));
        await using var host = Host(paths, provider, metadata);
        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(Track("known"), cancellationToken: Token);
        Assert.Equal(Id("expanded"), Assert.Single(result.Candidates).SourceId);
        Assert.Equal(1, metadata.Calls);
        Assert.All(provider.Calls, call => Assert.Equal(AudioQuality.HiRes, call.Request.Quality));
    }

    [Theory]
    [InlineData(false, "song-id")]
    [InlineData(false, "album-id")]
    [InlineData(false, null)]
    [InlineData(true, "song-id")]
    [InlineData(true, "album-id")]
    [InlineData(true, null)]
    public async Task MultipleExternalNamespacesSkipExpansionAndStillFindKnownSources(bool cached, string? otherKind)
    {
        using var paths = new TemporaryPaths();
        var provider = new Source("source", (id, request) =>
            id.RemoteId == "expanded" ? Audio(AudioQuality.HiRes) :
            !cached && request.Quality == AudioQuality.High ? Audio(AudioQuality.High) : null);
        var metadata = new Metadata(("expanded", .99));
        await using var host = Host(paths, provider, metadata);
        var track = Track("known") with
        {
            ExternalIds = [Id("known"), new("other", "other-id", otherKind)]
        };
        if (cached) Cache(host, Id("known"), AudioQuality.High);

        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(track, cancellationToken: Token);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(Id("known"), candidate.SourceId);
        Assert.Equal(AudioQuality.High, candidate.Source.Quality);
        Assert.Equal(cached, candidate.IsCached);
        Assert.Equal(0, metadata.Calls);
        Assert.False(result.MetadataExpanded);
        Assert.Same(track, result.UpdatedTrack);
    }

    [Theory]
    [InlineData("catalog", "song-id")]
    [InlineData("CATALOG", "album-id")]
    [InlineData("Catalog", null)]
    public async Task SameExternalNamespaceStillAllowsExpansionRegardlessOfKindOrCase(string otherNamespace, string? otherKind)
    {
        using var paths = new TemporaryPaths();
        var provider = new Source("source", (id, _) => id.RemoteId == "expanded" ? Audio(AudioQuality.HiRes) : null);
        var metadata = new Metadata(("expanded", .99));
        await using var host = Host(paths, provider, metadata);
        var track = Track("known") with
        {
            // The native Track.Id belongs to another namespace and must not affect the rule.
            ExternalIds = [Id("known"), new(otherNamespace, "other-id", otherKind)]
        };

        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(track, cancellationToken: Token);

        Assert.Equal(Id("expanded"), Assert.Single(result.Candidates).SourceId);
        Assert.Equal(1, metadata.Calls);
        Assert.True(result.MetadataExpanded);
    }

    [Fact]
    public async Task DowngradedUrlsAreReusedOnlyAfterOtherProvidersAndHigherQualitiesFail()
    {
        using var paths = new TemporaryPaths();
        var downgraded = Audio(AudioQuality.High, "retained");
        var first = new Source("first", (_, _) => downgraded);
        var second = new Source("second", (_, _) => null);
        var metadata = new Metadata();
        await using var host = Host(paths, first, second, metadata);
        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(Track("known"), cancellationToken: Token);
        Assert.Same(downgraded, Assert.Single(result.Candidates).Source);
        Assert.Equal(new[] { AudioQuality.HiRes, AudioQuality.Lossless }, second.Calls.Select(c => c.Request.Quality));
        Assert.DoesNotContain(first.Calls, c => c.Request.Quality == AudioQuality.High);
        Assert.Equal(1, metadata.Calls);
    }

    [Fact]
    public async Task KnownSourceInspectionReturnsOneBestResultPerIdAndNeverExpands()
    {
        using var paths = new TemporaryPaths();
        var provider = new Source("source", (id, request) => id.RemoteId switch
        {
            "2" => Audio(AudioQuality.High),
            "3" when request.Quality == AudioQuality.Lossless => Audio(AudioQuality.Lossless),
            _ => null
        });
        var metadata = new Metadata(("unexpected", .99));
        await using var host = Host(paths, provider, metadata);
        Cache(host, Id("1"), AudioQuality.HiRes);
        var result = await host.Get<PlaybackSourceDiscoveryService>().FindKnownAsync(Track("1", "2", "3"), cancellationToken: Token);
        Assert.Equal(new[] { "1", "2", "3" }, result.Candidates.Select(c => c.SourceId.Value));
        Assert.Equal(new AudioQuality?[] { AudioQuality.HiRes, AudioQuality.High, AudioQuality.Lossless }, result.Candidates.Select(c => c.Source.Quality));
        Assert.True(result.Candidates[0].IsCached);
        Assert.DoesNotContain(provider.Calls, c => c.Id.RemoteId == "1");
        Assert.Equal(0, metadata.Calls);
    }

    [Fact]
    public async Task RequestedQualityComesFirstThenFallbackRestartsFromHighest()
    {
        using var paths = new TemporaryPaths();
        var provider = new Source("source", (_, request) => request.Quality == AudioQuality.Lossless ? Audio(AudioQuality.Lossless) : null);
        await using var host = Host(paths, provider);
        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(Track("1"), new(AudioQuality.Medium), Token);
        Assert.Equal(AudioQuality.Lossless, Assert.Single(result.Candidates).Source.Quality);
        Assert.Equal(new[] { AudioQuality.Medium, AudioQuality.HiRes, AudioQuality.Lossless }, provider.Calls.Select(c => c.Request.Quality));
    }

    [Fact]
    public async Task AudioCacheUsesOnlyExactIdentityAndActualQualityAcrossProviders()
    {
        using var paths = new TemporaryPaths();
        var first = new Source("first", (_, _) => throw new Exception("Must use cache"));
        var second = new Source("second", (_, _) => throw new Exception("Must use cache"));
        await using var host = Host(paths, first, second);
        var cache = host.Get<PlaybackAudioCache>();
        Cache(host, Id("1"), AudioQuality.High);
        Assert.Null(await cache.TryGetAsync(Id("2"), AudioQuality.High, Token));
        Assert.Null(await cache.TryGetAsync(Id("1") with { Kind = "album-id" }, AudioQuality.High, Token));
        Assert.Null(await cache.TryGetAsync(Id("1") with { Namespace = "other" }, AudioQuality.High, Token));
        Assert.Null(await cache.TryGetAsync(Id("1"), AudioQuality.HiRes, Token));
        foreach (var provider in new[] { first, second })
        {
            var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(Track("1", "2"), new(AudioQuality.High, provider.Id), Token);
            Assert.True(Assert.Single(result.Candidates).IsCached);
            Assert.Empty(provider.Calls);
        }
        Assert.Single(Directory.GetFiles(Path.Combine(paths.CacheDirectory, "Music"), "*.json"));
        Assert.Null(cache.Start(Id("unknown"), new HttpPlaybackSource { Uri = new("https://example.com/audio.mp3") }));
    }

    [Fact]
    public async Task NativeIdAudioCanBeCachedWithoutAnyExternalIdsOrPlugins()
    {
        using var paths = new TemporaryPaths();
        await using var host = Host(paths);
        var track = Track();
        Cache(host, MusicCacheIdentity.FromTrackId(track.Id), AudioQuality.Lossless);
        var result = await host.Get<PlaybackResolver>().PlayAsync(track, cancellationToken: Token);
        Assert.IsType<FilePlaybackSource>(result);
        Assert.Equal(AudioQuality.Lossless, result.Quality);
    }

    [Fact]
    public async Task QualityPreferencePersistsAndExplicitRequestsOverrideIt()
    {
        using var paths = new TemporaryPaths();
        var provider = new Source("source", (_, request) => Audio(request.Quality));
        await using (var host = Host(paths, provider))
        {
            await host.InitializeAsync(Token);
            var scope = host.Settings.ForPackage(PackageNames.Application);
            scope.Apply<PlaybackPreferences>(p => p with { Quality = AudioQuality.Medium });
            var session = host.Get<PlaybackSessionService>();
            await session.PlayTrackAsync(Track("1"), cancellationToken: Token);
            Assert.Equal(AudioQuality.Medium, session.Snapshot.Quality);
            scope.Apply<PlaybackPreferences>(p => p with { Quality = AudioQuality.High });
            await session.PlayCurrentAsync(Token);
            Assert.Equal(AudioQuality.High, session.Snapshot.Quality);
            scope.Apply<PlaybackPreferences>(p => p with { Quality = AudioQuality.Medium });
            await session.PlayTrackAsync(Track("1"), new(AudioQuality.Lossless), Token);
            Assert.Equal(AudioQuality.Lossless, session.Snapshot.Quality);
            await session.PlayTrackAsync(Track("1"), new(AudioQuality.Automatic), Token);
            Assert.Equal(AudioQuality.HiRes, session.Snapshot.Quality);
        }
        await using var reloaded = Host(paths, provider);
        await reloaded.InitializeAsync(Token);
        Assert.Equal(AudioQuality.Medium, reloaded.Settings.ForPackage(PackageNames.Application).Get<PlaybackPreferences>().Quality);
    }

    [Fact]
    public async Task OneProviderFailureDoesNotSkipOtherProvidersOrIds()
    {
        using var paths = new TemporaryPaths();
        var failed = new Source("failed", (_, _) => throw new InvalidOperationException("Offline"));
        var working = new Source("working", (_, request) => Audio(request.Quality));
        await using var host = Host(paths, failed, working);
        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(Track("1"), cancellationToken: Token);
        Assert.Equal(working.Id, Assert.Single(result.Candidates).ProviderId);
        Assert.Single(result.ProviderErrors);
    }

    [Fact]
    public async Task SelectingOneSourceKeepsTheOriginalTrackAndDoesNotPlayAnotherId()
    {
        using var paths = new TemporaryPaths();
        var provider = new Source("source", (_, request) => Audio(request.Quality));
        var metadata = new Metadata(("expanded", .99));
        await using var host = Host(paths, provider, metadata);
        var track = Track("1", "2");
        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(track,
            new(RequiredSourceProviderId: provider.Id, RequiredSourceId: Id("2")), Token);
        Assert.Equal(Id("2"), Assert.Single(result.Candidates).SourceId);
        Assert.Equal("2", Assert.Single(provider.Calls).Id.RemoteId);
        Assert.Equal(track.Id, result.UpdatedTrack!.Id);
        Assert.Equal(0, metadata.Calls);
    }

    [Fact]
    public async Task UnknownQualityDoesNotWinOverAnAvailableKnownQuality()
    {
        using var paths = new TemporaryPaths();
        var unknown = new Source("unknown", (_, _) => new HttpPlaybackSource { Uri = new("https://example.com/unknown") });
        var known = new Source("known", (_, request) => request.Quality == AudioQuality.High ? Audio(AudioQuality.High) : null);
        await using var host = Host(paths, unknown, known);
        var result = await host.Get<PlaybackSourceDiscoveryService>().FindAsync(Track("1"), cancellationToken: Token);
        Assert.Equal(known.Id, Assert.Single(result.Candidates).ProviderId);
        Assert.Single(unknown.Calls);
    }

    private static AppHost Host(TemporaryPaths paths, params IMusicProvider[] providers) =>
        new(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory), services =>
        {
            services.AddSingleton<IPlayerProvider>(new TestPlayer());
            foreach (var provider in providers) services.AddSingleton(provider);
        });

    private static void Cache(AppHost host, ExternalMusicId id, AudioQuality quality)
    {
        using var writer = host.Get<PlaybackAudioCache>().Start(id, Audio(quality));
        Assert.NotNull(writer);
        writer.SetExpectedLength(3);
        writer.Write([1, 2, 3]);
        writer.Complete();
    }

    private sealed class Source(string id, Func<MusicItemId, PlaybackRequest, PlaybackSource?> resolve) : IMusicProvider, IPlaybackProvider
    {
        public string Id => id;
        public ProviderDescriptor Descriptor { get; } = new("Source", "Test source");
        public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes => [new("catalog", "song-id")];
        public List<(MusicItemId Id, PlaybackRequest Request)> Calls { get; } = [];
        public Task<PlaybackSource?> ResolvePlaybackAsync(MusicItemId trackId, PlaybackRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((trackId, request));
            return Task.FromResult(resolve(trackId, request));
        }
    }

    private sealed class Metadata(params (string Id, double Confidence)[] matches) : IMusicProvider, IMetadataProvider
    {
        public string Id => "metadata";
        public ProviderDescriptor Descriptor { get; } = new("Metadata", "Test matches");
        public IReadOnlyCollection<string> MetadataNamespaces => ["catalog"];
        public int Calls { get; private set; }
        public Task<MetadataLookupResult> LookupMetadataAsync(MetadataLookupRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new MetadataLookupResult(matches.Select(m => new MetadataMatch
            {
                MetadataProviderId = Id, SourceNamespace = "catalog", Track = Track(m.Id), Confidence = m.Confidence, MatchLevel = "test"
            }).ToArray(), new Dictionary<string, string>()));
        }
    }
}
