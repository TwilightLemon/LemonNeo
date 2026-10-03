using System.Runtime.CompilerServices;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Desktop.Tests;

internal sealed class FeatureProvider : IMusicProvider, ISearchProvider, ISearchNamespaceProvider, ITrackLookupProvider,
    IAuthenticationProvider, IPlaylistProvider, IPlaylistCollectionProvider, IMetadataProvider, ILyricsProvider, IPlaybackProvider, ITrackIdResolverProvider
{
    private readonly string _audio;
    public string Id => "test-service";
    public ProviderDescriptor Descriptor => new("Test service", "Deterministic desktop integration fixture");
    public IReadOnlyCollection<string> SearchNamespaces => ["test-net", "other-net", "unavailable-net"];
    public IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes => [new("test-net", "song-id"), new("other-net", "song-id")];
    public IReadOnlyCollection<string> MetadataNamespaces => ["test-net"];
    public AuthenticationState State { get; private set; } = new(false);
    public SearchRequest? LastSearch { get; private set; }
    public List<SearchRequest> Searches { get; } = [];
    public IReadOnlyList<string> LastMetadataTargets { get; private set; } = [];
    public bool FailSearch { get; set; }
    public TaskCompletionSource SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Track First { get; } = new()
    {
        Id = new("test-service", "one"), Title = "One", Duration = TimeSpan.FromSeconds(10),
        Artists = [new() { Name = "Fixture artist" }], ExternalIds = [new("test-net", "one", "song-id")]
    };
    public Track Second { get; } = new()
    {
        Id = new("test-service", "two"), Title = "Two", Duration = TimeSpan.FromSeconds(10), ExternalIds = [new("other-net", "two", "song-id")]
    };
    public FeatureProvider(TestPaths paths)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        _audio = Path.Combine(paths.DataDirectory, "fixture.wav");
        File.WriteAllBytes(_audio, [0]);
    }
    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        LastSearch = request;
        Searches.Add(request);
        if (request.Keyword == "wait")
        {
            SearchStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        if (FailSearch) throw new InvalidOperationException("Fixture service unavailable");
        return new(Id, request.Keyword == "one" ? [First] : [First, Second]);
    }
    public Task<Track?> GetTrackAsync(string trackId, CancellationToken cancellationToken = default) =>
        Task.FromResult(trackId == "one" ? First : trackId == "two" ? Second : null);
    public Task<IReadOnlyList<AuthMethod>> GetAvailableMethodsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuthMethod>>([AuthMethod.Cookie]);
    public Task<AuthenticationState> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Parameters["cookie"] == "reject") throw new InvalidOperationException("Fixture sign-in failed");
        State = new(true, "Fixture account", "user-42");
        return Task.FromResult(State);
    }
    public Task SignOutAsync(CancellationToken cancellationToken = default) { State = new(false); return Task.CompletedTask; }
    public Task<RemotePlaylist?> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default) =>
        Task.FromResult<RemotePlaylist?>(playlistId != "favorites" ? null : new()
        {
            ProviderId = Id, Id = playlistId, Name = "Online favorites", Description = "Remote description", Tracks = [First, Second]
        });
    public async IAsyncEnumerable<RemotePlaylistSummary> GetUserPlaylistsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new("favorites", "Online favorites", 2);
    }
    public async IAsyncEnumerable<RemotePlaylistCollection> GetUserPlaylistCollectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new("owned", "Owned", [new("favorites", "Online favorites", 2)]);
        yield return new("followed", "Followed", []);
    }
    public Task<MetadataLookupResult> LookupMetadataAsync(MetadataLookupRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastMetadataTargets = request.TargetNamespaces;
        if (request.TargetNamespaces.Count > 0 && !request.TargetNamespaces.Contains("test-net"))
            return Task.FromResult(new MetadataLookupResult([], new Dictionary<string, string>()));
        return Task.FromResult(new MetadataLookupResult([new()
        {
            MetadataProviderId = Id, SourceNamespace = "test-net", Confidence = .97, MatchLevel = "Exact",
            Metadata = new() { Title = $"Matched {request.Track.Title}", Artists = ["Matched artist"], Album = "Matched album",
                Duration = TimeSpan.FromSeconds(42), ExternalIds = [new("isrc", "TEST00000001"), new("test-net", "alternate-one", "song-id")] }
        }], new Dictionary<string, string> { ["other-net"] = "Fixture metadata warning" }));
    }
    public Task<Lyrics?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default) => Task.FromResult<Lyrics?>(new(
        "hello world\nDone", "en", true,
        Lines: [new("hello world", 0, 3000, Words: [new("hello ", 0, 1000), new("world", 1000, 2500)]), new("Done", 4000, 5000)],
        Source: "Fixture", TranslationLines: [new("你好", 500, 1500), new("世界", 1500, 3000)],
        RomanizationLines: [new([new("he ", 0, 1000), new("lo", 1000, 3000)])]));
    public Task<PlaybackSource?> ResolvePlaybackAsync(Track track, PlaybackRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<PlaybackSource?>(new FilePlaybackSource(_audio));
    public Task<Track?> ResolveTrackIdAsync(ExternalMusicId identifier, TrackMetadata metadata, CancellationToken cancellationToken = default) =>
        Task.FromResult<Track?>(!AcceptedIdSchemes.Any(scheme => scheme.Accepts(identifier)) ? null : new()
        {
            Id = new(Id, identifier.Value), Title = metadata.Title,
            Artists = metadata.Artists.Select(name => new ArtistRef { Name = name }).ToArray(),
            ExternalIds = metadata.ExternalIds, Duration = metadata.Duration
        });
}

internal sealed class SearchOnlyProvider : IMusicProvider, ISearchProvider
{
    public string Id => "search-only";
    public ProviderDescriptor Descriptor => new("Search only", "Does not implement lookup, authentication, playlists or playback");
    public IReadOnlyList<Track> Tracks { get; set; } = [];
    public SearchRequest? LastSearch { get; private set; }
    public Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastSearch = request;
        return Task.FromResult(new SearchResult(Id, Tracks));
    }
}
