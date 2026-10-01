using System.Runtime.CompilerServices;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Plugin.MockMusic;

public sealed class MockMusicPlugin :
    ILemonPlugin,
    IMusicProvider,
    ISearchProvider,
    ITrackLookupProvider,
    IPlaylistProvider,
    IPlaybackProvider,
    ILyricsProvider
{
    private static readonly IReadOnlyList<Track> Tracks =
    [
        new Track
        {
            Id = new MusicItemId("mock", "track-001"),
            Title = "Blinding Lights",
            Artists = [new ArtistRef { Name = "The Weeknd" }],
            Album = new AlbumRef { Title = "After Hours" }
        },
        new Track
        {
            Id = new MusicItemId("mock", "track-002"),
            Title = "Save Your Tears",
            Artists = [new ArtistRef { Name = "The Weeknd" }],
            Album = new AlbumRef { Title = "After Hours" }
        },
        new Track
        {
            Id = new MusicItemId("mock", "track-003"),
            Title = "Starboy",
            Artists = [new ArtistRef { Name = "The Weeknd" }],
            Album = new AlbumRef { Title = "Starboy" }
        },
        new Track
        {
            Id = new MusicItemId("mock", "track-004"),
            Title = "The Hills",
            Artists = [new ArtistRef { Name = "The Weeknd" }],
            Album = new AlbumRef { Title = "Beauty Behind the Madness" }
        },
        new Track
        {
            Id = new MusicItemId("mock", "track-005"),
            Title = "Can't Feel My Face",
            Artists = [new ArtistRef { Name = "The Weeknd" }],
            Album = new AlbumRef { Title = "Beauty Behind the Madness" }
        },
        new Track
        {
            Id = new MusicItemId("mock", "track-006"),
            Title = "Die For You",
            Artists = [new ArtistRef { Name = "The Weeknd" }],
            Album = new AlbumRef { Title = "Starboy" }
        }
    ];

    private static readonly IReadOnlyDictionary<string, RemotePlaylist> Playlists =
        new Dictionary<string, RemotePlaylist>(StringComparer.OrdinalIgnoreCase)
        {
            ["favorites"] = new()
            {
                Id = "favorites",
                ProviderId = "mock",
                Name = "Mock Favorites",
                Description = "A small remote playlist exposed by the mock provider.",
                Tracks = [Tracks[0], Tracks[1], Tracks[2]]
            },
            ["coding"] = new()
            {
                Id = "coding",
                ProviderId = "mock",
                Name = "Coding Mix",
                Description = "Music for compiling ideas.",
                Tracks = Tracks
            },
            ["weeknd"] = new()
            {
                Id = "weeknd",
                ProviderId = "mock",
                Name = "The Weeknd Essentials",
                Description = "Popular The Weeknd tracks with metadata only and no mock playback source.",
                Tracks = Tracks
            }
        };

    public PluginDescriptor Descriptor { get; } = new(
        "org.lemonneo.mockmusic",
        "Mock Music",
        "1.0.0");

    public string Id => "mock";

    ProviderDescriptor IMusicProvider.Descriptor { get; } = new(
        "Mock Music",
        "Deterministic music data for the Lemon Neo prototype.");

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.RegisterProvider(this);
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public Task<SearchResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matches = Tracks
            .Where(track => string.IsNullOrWhiteSpace(request.Keyword) ||
                            track.Title.Contains(request.Keyword, StringComparison.OrdinalIgnoreCase) ||
                            track.Id.RemoteId.Contains(request.Keyword, StringComparison.OrdinalIgnoreCase) ||
                            track.Artists.Any(artist =>
                                artist.Name.Contains(request.Keyword, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(0, request.Limit))
            .ToArray();
        return Task.FromResult(new SearchResult(Id, matches));
    }

    public Task<Track?> GetTrackAsync(
        string trackId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Tracks.FirstOrDefault(track =>
            string.Equals(track.Id.RemoteId, trackId, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<RemotePlaylist?> GetPlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Playlists.GetValueOrDefault(playlistId));
    }

    public async IAsyncEnumerable<RemotePlaylistSummary> GetUserPlaylistsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var playlist in Playlists.Values.OrderBy(playlist => playlist.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new RemotePlaylistSummary(playlist.Id, playlist.Name, playlist.Tracks.Count);
        }
    }

    public Task<PlaybackSource?> ResolvePlaybackAsync(
        Track track,
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<PlaybackSource?>(null);
    }

    public Task<Lyrics?> GetLyricsAsync(
        Track track,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = Tracks.Any(candidate => candidate.Id == track.Id);
        return Task.FromResult<Lyrics?>(exists
            ? new Lyrics($"Mock lyrics for {track.Title}.", "en")
            : null);
    }
}
