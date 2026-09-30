using System.Runtime.CompilerServices;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Plugin.MockMusic;

public sealed class MockMusicPlugin :
    ILemonPlugin,
    IMusicProvider,
    ISearchProvider,
    IPlaylistProvider,
    IPlaybackProvider,
    ILyricsProvider
{
    private static readonly IReadOnlyList<Track> Tracks =
    [
        new Track
        {
            Id = new MusicItemId("mock", "track-001"),
            Title = "Neo Sunrise",
            Artists = [new ArtistRef { Name = "Lemon Band" }],
            Album = new AlbumRef { Title = "Freshly Squeezed" },
            Duration = TimeSpan.FromMinutes(3.42),
            ExternalIds = [new ExternalMusicId("isrc", "MOCK00000001")],
            IsPlayable = true
        },
        new Track
        {
            Id = new MusicItemId("mock", "track-002"),
            Title = "Glass Horizon",
            Artists = [new ArtistRef { Name = "Lemon Band" }],
            Album = new AlbumRef { Title = "Freshly Squeezed" },
            Duration = TimeSpan.FromMinutes(4.05),
            ExternalIds = [new ExternalMusicId("isrc", "MOCK00000002")],
            IsPlayable = true
        },
        new Track
        {
            Id = new MusicItemId("mock", "track-003"),
            Title = "Midnight Compile",
            Artists = [new ArtistRef { Name = "Digital Cat" }],
            Album = new AlbumRef { Title = "Nightly Builds" },
            Duration = TimeSpan.FromMinutes(2.95),
            ExternalIds = [new ExternalMusicId("isrc", "MOCK00000003")],
            IsPlayable = true
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
                Tracks = [Tracks[0], Tracks[2]]
            },
            ["coding"] = new()
            {
                Id = "coding",
                ProviderId = "mock",
                Name = "Coding Mix",
                Description = "Music for compiling ideas.",
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
        TrackReference track,
        PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Tracks.Any(candidate => candidate.Id == track.Id))
        {
            return Task.FromResult<PlaybackSource?>(null);
        }

        return Task.FromResult<PlaybackSource?>(new HttpPlaybackSource
        {
            Uri = new Uri($"https://example.invalid/audio/{track.Id.RemoteId}.flac"),
            Headers = new Dictionary<string, string> { ["X-Lemon-Demo"] = "true" },
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15)
        });
    }

    public Task<Lyrics?> GetLyricsAsync(
        TrackReference track,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = Tracks.Any(candidate => candidate.Id == track.Id);
        return Task.FromResult<Lyrics?>(exists
            ? new Lyrics($"Mock lyrics for {track.Title ?? track.Id.RemoteId}.", "en")
            : null);
    }
}
