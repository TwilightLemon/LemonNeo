using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class PlaybackSourceDiscoveryTests
{
    private const string Catalog = "catalog";
    private static readonly Track Original = new()
    {
        Id = new("original", "track"), Title = "Original title",
        Artists = [new ArtistRef { Name = "Artist" }], Duration = TimeSpan.FromMinutes(3)
    };

    [Fact]
    public async Task HighestProviderConfidenceWinsEvenWhenItsTitleWouldFailTheOldScore()
    {
        var playback = new PlaybackProvider("playback", Catalog);
        var metadata = new MetadataProvider(
            Match("lower", 0.88),
            Match("highest", 0.97, title: "Localized title", duration: TimeSpan.FromMinutes(5)),
            Match("middle", 0.92));
        var discovery = Service(playback, metadata);

        var result = await discovery.FindAsync(Original, cancellationToken: TestContext.Current.CancellationToken);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("highest", candidate.Track.Id.RemoteId);
        Assert.Equal("Localized title", candidate.Track.Title);
        Assert.Equal(0.97, candidate.Confidence);
        Assert.Equal("metadata-match:matcher/catalog", candidate.ResolutionMethod);
        Assert.Equal("highest", Assert.Single(playback.ResolvedIds).Value);
        Assert.Equal(0, playback.SearchCalls);
    }

    [Theory]
    [InlineData(0.849, false)]
    [InlineData(0.85, true)]
    [InlineData(0.9, true)]
    public async Task DiscoveryEnforcesConfidenceThresholdEvenWhenMetadataProviderDoesNotFilter(
        double confidence, bool accepted)
    {
        var playback = new PlaybackProvider("playback", Catalog);
        var metadata = new MetadataProvider(Match("candidate", confidence));
        var result = await Service(playback, metadata).FindAsync(Original,
            cancellationToken: TestContext.Current.CancellationToken);

        if (accepted)
            Assert.Equal(confidence, Assert.Single(result.Candidates).Confidence);
        else
        {
            Assert.Empty(result.Candidates);
            Assert.Empty(playback.ResolvedIds);
        }
        Assert.Equal(0.85, Assert.Single(metadata.Requests).MinimumConfidence);
        Assert.Equal(0, playback.SearchCalls);
    }

    [Fact]
    public async Task HighestMatchMustUseAnIdentifierSchemeAcceptedByThePlaybackProvider()
    {
        var playback = new PlaybackProvider("playback", Catalog);
        var metadata = new MetadataProvider(
            Match("wrong-kind", 0.99, kind: "song-mid"),
            Match("too-low", 0.8),
            Match("lower", 0.9),
            Match("highest-compatible", 0.95));

        var result = await Service(playback, metadata).FindAsync(Original,
            cancellationToken: TestContext.Current.CancellationToken);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("highest-compatible", candidate.Track.Id.RemoteId);
        Assert.Equal(0.95, candidate.Confidence);
        Assert.Equal("highest-compatible", Assert.Single(playback.ResolvedIds).Value);
    }

    [Fact]
    public async Task CandidatesFromDifferentSourcesRetainTheirMetadataConfidenceAndSortByIt()
    {
        var lower = new PlaybackProvider("alpha", "lower-catalog");
        var higher = new PlaybackProvider("zeta", "higher-catalog");
        var metadata = new MetadataProvider(
            Match("lower", 0.88, ns: "lower-catalog"),
            Match("higher", 0.97, ns: "higher-catalog"));
        var registry = new ProviderRegistry([lower, higher, metadata]);
        var service = new PlaybackSourceDiscoveryService(registry, new MetadataService(registry));

        var result = await service.FindAsync(Original, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "zeta", "alpha" }, result.Candidates.Select(candidate => candidate.ProviderId));
        Assert.Equal(new[] { 0.97, 0.88 }, result.Candidates.Select(candidate => candidate.Confidence));
    }

    [Fact]
    public async Task KnownExternalIdsStillResolveWithoutMetadataLookup()
    {
        var playback = new PlaybackProvider("playback", Catalog);
        var metadata = new MetadataProvider(Match("other", 0.99));
        var track = Original with { ExternalIds = [new(Catalog, "known", "song-id")] };

        var result = await Service(playback, metadata).FindKnownAsync(track,
            cancellationToken: TestContext.Current.CancellationToken);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("known", candidate.Track.Id.RemoteId);
        Assert.Equal(0.98, candidate.Confidence);
        Assert.Empty(metadata.Requests);
        Assert.Equal(0, playback.SearchCalls);
    }

    [Fact]
    public async Task UnscoredSearchResultsCannotCreatePlaybackCandidates()
    {
        var playback = new PlaybackProvider("playback", Catalog);
        var result = await Service(playback, new MetadataProvider()).FindAsync(Original,
            cancellationToken: TestContext.Current.CancellationToken);

        // The raw search fixture would return an exact title/artist/duration match.
        Assert.Empty(result.Candidates);
        Assert.Empty(playback.ResolvedIds);
        Assert.Equal(0, playback.SearchCalls);
    }

    [Fact]
    public async Task RequiredSourceRestrictsMetadataNamespacesAndCandidateSelection()
    {
        var other = new PlaybackProvider("other", "other-catalog");
        var required = new PlaybackProvider("required", Catalog);
        var metadata = new MetadataProvider(
            Match("other", 0.99, ns: "other-catalog"),
            Match("required", 0.9));
        var registry = new ProviderRegistry([other, required, metadata]);
        var service = new PlaybackSourceDiscoveryService(registry, new MetadataService(registry));

        var result = await service.FindAsync(Original, new PlaybackRequest(RequiredSourceProviderId: "required"),
            TestContext.Current.CancellationToken);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("required", candidate.ProviderId);
        Assert.Equal(0.9, candidate.Confidence);
        Assert.Equal(Catalog, Assert.Single(Assert.Single(metadata.Requests).TargetNamespaces));
        Assert.Empty(other.ResolvedIds);
    }

    private static PlaybackSourceDiscoveryService Service(PlaybackProvider playback, MetadataProvider metadata)
    {
        var registry = new ProviderRegistry([playback, metadata]);
        return new(registry, new MetadataService(registry));
    }

    private static MetadataMatch Match(string id, double confidence, string ns = Catalog,
        string kind = "song-id", string? title = null, TimeSpan? duration = null) => new()
    {
        MetadataProviderId = "matcher", SourceNamespace = ns, Confidence = confidence, MatchLevel = "fixture",
        Metadata = new TrackMetadata
        {
            Title = title ?? Original.Title, Artists = Original.Artists.Select(artist => artist.Name).ToArray(),
            Duration = duration ?? Original.Duration, ExternalIds = [new(ns, id, kind)]
        }
    };

    private sealed class MetadataProvider(params MetadataMatch[] matches) : IMusicProvider, IMetadataProvider
    {
        public string Id => "matcher";
        public ProviderDescriptor Descriptor { get; } = new("Metadata Fixture", "Returns provider-scored matches");
        public IReadOnlyCollection<string> MetadataNamespaces { get; } =
            matches.Select(match => match.SourceNamespace).Append(Catalog).Distinct().ToArray();
        public List<MetadataLookupRequest> Requests { get; } = [];
        public Task<MetadataLookupResult> LookupMetadataAsync(MetadataLookupRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            // Deliberately return unsorted, unfiltered results to exercise host selection.
            return Task.FromResult(new MetadataLookupResult(matches, new Dictionary<string, string>()));
        }
    }

    private sealed class PlaybackProvider(string providerId, string ns) :
        IMusicProvider, IPlaybackProvider, ITrackIdResolverProvider, ISearchProvider
    {
        public string Id => providerId;
        public ProviderDescriptor Descriptor { get; } = new("Playback Fixture", "Resolves accepted catalogue IDs");
        public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; } = [new(ns, "song-id")];
        public List<ExternalMusicId> ResolvedIds { get; } = [];
        public int SearchCalls { get; private set; }
        public Task<Track?> ResolveTrackIdAsync(ExternalMusicId identifier, TrackMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedIds.Add(identifier);
            return Task.FromResult<Track?>(new Track
            {
                Id = new(Id, identifier.Value), Title = metadata.Title,
                Artists = metadata.Artists.Select(name => new ArtistRef { Name = name }).ToArray(),
                Duration = metadata.Duration, ExternalIds = metadata.ExternalIds
            });
        }
        public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<PlaybackSource?>(new HttpPlaybackSource { Uri = new("https://audio.test/song.mp3") });
        }
        public Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            return Task.FromResult(new SearchResult(Id, [Original with { Id = new(Id, "unscored-search") }]));
        }
    }
}
