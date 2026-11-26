using LemonNeo.Core.Library;
using LemonNeo.Core.Lyrics;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Abstractions.Providers;

public sealed record SearchRequest(
    string Keyword,
    int Limit = 50,
    string? SourceNamespace = null);

public sealed record SearchResult(string ProviderId, IReadOnlyList<Track> Tracks);

public sealed record RemotePlaylistSummary(string Id, string Name, int? TrackCount = null)
{
    public Uri? ArtworkUri { get; init; }
}

public sealed record RemotePlaylistCollection(
    string Id,
    string Name,
    IReadOnlyList<RemotePlaylistSummary> Playlists);

public sealed record RemotePlaylist
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public Uri? ArtworkUri { get; init; }
    public IReadOnlyList<Track> Tracks { get; init; } = [];
    /// <summary>True only when membership, order and track details were fully retrieved. Required for synchronization.</summary>
    public bool IsComplete { get; init; }
}

public enum AuthMethod
{
    Cookie,
    Browser,
    DeviceCode,
    Password,
    QrCode
}

public sealed record AuthenticationState(
    bool IsAuthenticated,
    string? AccountName = null,
    string? UserId = null,
    Uri? AvatarUri = null);

public sealed record AuthenticationRequest(
    AuthMethod Method,
    IReadOnlyDictionary<string, string> Parameters);

public interface ISearchProvider
{
    /// <summary>Catalog namespaces that can be selected through SearchRequest.SourceNamespace.</summary>
    IReadOnlyCollection<string> SearchNamespaces { get; }

    Task<SearchResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Retrieves a track by the provider's native identifier without approximating
///     the operation through text search.
/// </summary>
public interface ITrackLookupProvider
{
    Task<Track?> GetTrackAsync(
        string trackId,
        CancellationToken cancellationToken = default);
}

public interface IPlaylistProvider
{
    Task<RemotePlaylist?> GetPlaylistAsync(
        string playlistId,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<RemotePlaylistSummary> GetUserPlaylistsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Provides named groups of user playlists when a service distinguishes between
///     owned, followed, purchased, or other playlist-like collections.
/// </summary>
public interface IPlaylistCollectionProvider
{
    IAsyncEnumerable<RemotePlaylistCollection> GetUserPlaylistCollectionsAsync(
        CancellationToken cancellationToken = default);
}

public interface IPlaylistExportProvider
{
    Task ExportPlaylistAsync(
        Playlist playlist,
        string? remotePlaylistId = null,
        CancellationToken cancellationToken = default);
}

public interface IPlaybackProvider
{
    /// <summary>External ID schemes whose values this provider accepts as RemoteId.</summary>
    IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; }

    /// <summary>
    ///     Resolves only the requested quality using an ID scoped to this provider.
    ///     Automatic means the highest supported quality. Return null when unavailable;
    ///     do not retry at lower qualities. If the remote service silently downgrades,
    ///     return its actual quality in PlaybackSource.Quality for host arbitration.
    /// </summary>
    Task<PlaybackSource?> ResolvePlaybackAsync(
        MusicItemId trackId,
        PlaybackRequest request,
        CancellationToken cancellationToken = default);
}

public interface ILyricsProvider
{
    /// <summary>External ID schemes accepted by this lyrics source.</summary>
    IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; }

    /// <summary>Fetches one known platform ID without searching for other recordings or sources.</summary>
    Task<Lyrics?> GetLyricsAsync(
        ExternalMusicId musicId,
        CancellationToken cancellationToken = default);
}

public sealed record PlaybackSourceProviderRegistration(
    string ProviderId,
    IMusicProvider MusicProvider,
    IPlaybackProvider PlaybackProvider,
    IReadOnlyList<MusicIdScheme> AcceptedIdSchemes);

/// <summary>
///     Tracks the playback-source providers currently registered by loaded plugins.
/// </summary>
public interface IPlaybackSourceProviderRegistry
{
    IReadOnlyCollection<PlaybackSourceProviderRegistration> Sources { get; }

    PlaybackSourceProviderRegistration? Get(string providerId);
}

public interface IAuthenticationProvider
{
    AuthenticationState State { get; }

    Task<IReadOnlyList<AuthMethod>> GetAvailableMethodsAsync(
        CancellationToken cancellationToken = default);

    Task<AuthenticationState> AuthenticateAsync(
        AuthenticationRequest request,
        CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);
}
