using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Playback;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Plugin.LocalMusic;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class PlaybackSourceDiscoveryTests
{
    [Fact]
    public async Task NativePlaybackUsesTheTrackIdAndKeepsItsMetadataSnapshot()
    {
        using var paths = new TemporaryPaths();
        var provider = new RecordingPlaybackProvider();
        await using var host = CreateHost(paths, new TestPlayer(), provider);
        var track = HostFixture.Track() with { Id = new(provider.Id, "12345") };
        var request = new PlaybackRequest(AudioQuality.Lossless, provider.Id);

        var result = await host.Get<PlaybackSourceDiscoveryService>()
            .FindKnownAsync(track, request, TestContext.Current.CancellationToken);

        var call = Assert.Single(provider.Calls);
        Assert.Equal(track.Id, call.Id);
        Assert.Same(request, call.Request);
        var candidate = Assert.Single(result.Candidates);
        Assert.Same(track, candidate.Track);
        Assert.Equal("provider-native-id", candidate.ResolutionMethod);
    }

    [Fact]
    public async Task DeclaredIdsAreFilteredBySchemeAndTriedUntilAudioIsAvailable()
    {
        using var paths = new TemporaryPaths();
        var provider = new RecordingPlaybackProvider();
        await using var host = CreateHost(paths, new TestPlayer(), provider);
        var track = HostFixture.Track() with
        {
            Title = "Saved title",
            Artists = [new ArtistRef { Id = new("catalog", "artist-1"), Name = "Saved artist" }],
            Album = new AlbumRef { Id = new("catalog", "album-1"), Title = "Saved album" },
            ExternalIds =
            [
                new("spotify", "unsupported", "song-id"),
                new("netease", "wrong-kind", "album-id"),
                new("netease", "missing", "song-id"),
                new("NETEASE", "12345", "SONG-ID")
            ]
        };
        var request = new PlaybackRequest(AudioQuality.High, provider.Id);

        var result = await host.Get<PlaybackSourceDiscoveryService>()
            .FindKnownAsync(track, request, TestContext.Current.CancellationToken);

        Assert.Equal([new MusicItemId(provider.Id, "missing"), new(provider.Id, "12345")],
            provider.Calls.Where(call => call.Request.Quality == AudioQuality.High).Select(call => call.Id));
        Assert.Same(request, provider.Calls.Last().Request);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(new(provider.Id, "12345"), candidate.Track.Id);
        Assert.Equal(track.Title, candidate.Track.Title);
        Assert.Equal(track.Artists, candidate.Track.Artists);
        Assert.Equal(track.Album, candidate.Track.Album);
        Assert.Equal(track.Duration, candidate.Track.Duration);
        Assert.Equal(track.ExternalIds, candidate.Track.ExternalIds);
        Assert.Equal("declared-id:NETEASE/SONG-ID", candidate.ResolutionMethod);
        Assert.Empty(result.ProviderErrors);
    }

    [Fact]
    public async Task MetadataDiscoveryPassesTheMatchedIdToThePlaybackProvider()
    {
        using var paths = new TemporaryPaths();
        var playback = new RecordingPlaybackProvider();
        var metadata = new MatchingMetadataProvider();
        await using var host = CreateHost(paths, new TestPlayer(), playback, metadata);
        var request = new PlaybackRequest(AudioQuality.Lossless, playback.Id);

        var result = await host.Get<PlaybackSourceDiscoveryService>()
            .FindAsync(HostFixture.Track(), request, TestContext.Current.CancellationToken);

        Assert.Equal(["netease"], Assert.Single(metadata.Requests).TargetNamespaces);
        var call = Assert.Single(playback.Calls);
        Assert.Equal(new(playback.Id, "12345"), call.Id);
        Assert.Same(request, call.Request);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("Matched title", candidate.Track.Title);
        Assert.Equal(new MusicItemId("netease", "artist-1"), Assert.Single(candidate.Track.Artists).Id);
        Assert.Equal(new MusicItemId("netease", "album-1"), candidate.Track.Album!.Id);
        Assert.Equal(new("netease", "12345", "song-id"), Assert.Single(candidate.Track.ExternalIds));
        Assert.Equal("metadata-match:metadata-broker/netease", candidate.ResolutionMethod);
        Assert.Empty(result.ProviderErrors);
    }

    [Fact]
    public async Task ApplyingMatchedTrackKeepsTheOriginalIdentityMetadataAndExistingExternalIdSchemes()
    {
        using var paths = new TemporaryPaths();
        var metadata = new MatchingMetadataProvider();
        await using var host = CreateHost(paths, new TestPlayer(), metadata);
        var original = HostFixture.Track() with
        {
            ArtworkUri = new("https://example.com/saved.png"),
            ExternalIds = [new("isrc", "ABC000000001"), new("NETEASE", "saved-id", "SONG-ID")]
        };

        var (updated, lookup) = await host.Get<MetadataService>()
            .MatchAndReplaceAsync(original, ["netease"], TestContext.Current.CancellationToken);

        var matched = lookup.Matches[0].Track;
        Assert.NotEqual(original.Id, matched.Id);
        Assert.Equal(original.Id, updated.Id);
        Assert.Equal(matched.Title, updated.Title);
        Assert.Same(matched.Artists, updated.Artists);
        Assert.Same(matched.Album, updated.Album);
        Assert.Equal(original.Duration, updated.Duration);
        Assert.Equal(original.ArtworkUri, updated.ArtworkUri);
        Assert.Equal(2, updated.ExternalIds.Count);
        Assert.Contains(new("isrc", "ABC000000001"), updated.ExternalIds);
        Assert.Contains(new("NETEASE", "saved-id", "SONG-ID"), updated.ExternalIds);
        Assert.DoesNotContain(updated.ExternalIds, identifier => identifier.Value is "12345" or "below-threshold");
        Assert.Equal(1, Assert.Single(metadata.Requests).LimitPerSource);
        Assert.Equal("one", original.Title);
        Assert.Equal(2, original.ExternalIds.Count);
    }

    [Fact]
    public async Task ApplyingMatchedTrackAddsOnlyOneExternalIdForAMissingScheme()
    {
        using var paths = new TemporaryPaths();
        await using var host = CreateHost(paths, new TestPlayer(), new MatchingMetadataProvider());
        var original = HostFixture.Track() with
        {
            ExternalIds = [new("isrc", "ABC000000001")]
        };

        var (updated, _) = await host.Get<MetadataService>()
            .MatchAndReplaceAsync(original, ["netease"], TestContext.Current.CancellationToken);

        Assert.Equal(2, updated.ExternalIds.Count);
        Assert.Contains(new("isrc", "ABC000000001"), updated.ExternalIds);
        Assert.Contains(new("netease", "12345", "song-id"), updated.ExternalIds);
        Assert.DoesNotContain(updated.ExternalIds, identifier => identifier.Value == "below-threshold");
        Assert.Single(updated.ExternalIds, identifier =>
            string.Equals(identifier.Namespace, "netease", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(identifier.Kind, "song-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalPlaybackAcceptsCatalogIdsAndFilePaths()
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "Song - Artist.mp3");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        var local = new LocalMusicPlugin();
        var scan = await local.ScanFolderAsync(new(paths.Root), TestContext.Current.CancellationToken);
        var track = Assert.Single(scan.Tracks);
        Assert.NotEqual(path, track.Id.RemoteId);
        var request = new PlaybackRequest();

        Assert.Equal(new FilePlaybackSource(path),
            await local.ResolvePlaybackAsync(track.Id, request, TestContext.Current.CancellationToken));
        var fresh = new LocalMusicPlugin();
        Assert.Null(await fresh.ResolvePlaybackAsync(track.Id, request, TestContext.Current.CancellationToken));
        Assert.Equal(new FilePlaybackSource(path),
            await fresh.ResolvePlaybackAsync(new("local", path), request, TestContext.Current.CancellationToken));
        Assert.Null(await fresh.ResolvePlaybackAsync(new("other", path), request, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local")]
    public async Task SavedLocalQueuePlaysWithoutRescanningTheCatalog(string? requiredSourceProviderId)
    {
        using var paths = new TemporaryPaths();
        var path = Path.Combine(paths.Root, "Song - Artist.mp3");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        Track track;
        await using (var host = CreateHost(paths, new TestPlayer(), new LocalMusicPlugin()))
        {
            await host.InitializeAsync(TestContext.Current.CancellationToken);
            var local = Assert.IsType<LocalMusicPlugin>(host.Get<IProviderRegistry>().Get("local"));
            var scan = await local.ScanFolderAsync(new(paths.Root), TestContext.Current.CancellationToken);
            track = Assert.Single(scan.Tracks) with { Title = "Saved title" };
            await host.Get<PlaybackSessionService>().PlayTrackAsync(track,
                new(RequiredSourceProviderId: requiredSourceProviderId), TestContext.Current.CancellationToken);
        }

        var fresh = new LocalMusicPlugin();
        var player = new TestPlayer();
        await using var restored = CreateHost(paths, player, fresh);
        await restored.InitializeAsync(TestContext.Current.CancellationToken);
        Assert.Null(await fresh.GetTrackAsync(track.Id.RemoteId, TestContext.Current.CancellationToken));
        var session = restored.Get<PlaybackSessionService>();
        Assert.Equal(track.Id, session.Queue.CurrentItem!.Track.Id);
        await session.PlayCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, player.PlayCalls);
        Assert.Equal("Saved title", player.CurrentTrack!.Title);
        Assert.Equal(PlayerState.Playing, session.Snapshot.State);
        Assert.Equal(requiredSourceProviderId, session.Snapshot.RequiredSourceProviderId);
        Assert.Equal(track.Id, session.Queue.CurrentItem.Track.Id);
    }

    private static AppHost CreateHost(TemporaryPaths paths, TestPlayer player, params IMusicProvider[] providers) =>
        new(paths, new(paths.PluginDirectory, paths.MusicLibraryDirectory), services =>
        {
            services.AddSingleton<IPlayerProvider>(player);
            foreach (var provider in providers) services.AddSingleton(provider);
        });

    private sealed class RecordingPlaybackProvider : IMusicProvider, IPlaybackProvider
    {
        public string Id => "alias-playback";
        public ProviderDescriptor Descriptor { get; } = new("Playback", "Accepts Netease song IDs.");
        public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes => [new("netease", "song-id")];
        public List<(MusicItemId Id, PlaybackRequest Request)> Calls { get; } = [];

        public Task<PlaybackSource?> ResolvePlaybackAsync(
            MusicItemId trackId, PlaybackRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((trackId, request));
            return Task.FromResult<PlaybackSource?>(trackId.ProviderId == Id && trackId.RemoteId != "missing"
                ? new FilePlaybackSource("acceptance.audio")
                : null);
        }
    }

    private sealed class MatchingMetadataProvider : IMusicProvider, IMetadataProvider
    {
        public string Id => "metadata-broker";
        public ProviderDescriptor Descriptor { get; } = new("Metadata", "Supplies song IDs for playback.");
        public IReadOnlyCollection<string> MetadataNamespaces => ["netease"];
        public List<MetadataLookupRequest> Requests { get; } = [];

        public Task<MetadataLookupResult> LookupMetadataAsync(
            MetadataLookupRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new MetadataLookupResult(
                [Match("below-threshold", .84), Match("12345", .96)], new Dictionary<string, string>()));
        }

        private MetadataMatch Match(string id, double confidence) => new()
        {
            MetadataProviderId = Id,
            SourceNamespace = "netease",
            Track = new()
            {
                Id = new(Id, id),
                Title = "Matched title",
                Artists = [new ArtistRef { Id = new("netease", "artist-1"), Name = "Matched artist" }],
                Album = new AlbumRef { Id = new("netease", "album-1"), Title = "Matched album" },
                ExternalIds = [new("netease", id, "song-id")]
            },
            Confidence = confidence,
            MatchLevel = "test"
        };
    }
}
