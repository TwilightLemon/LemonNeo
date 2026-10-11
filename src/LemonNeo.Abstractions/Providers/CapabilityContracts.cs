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

public sealed record RemotePlaylistSummary(string Id, string Name, int? TrackCount = null);

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
    public IReadOnlyList<Track> Tracks { get; init; } = [];
}

public sealed record MetadataLookupRequest
{
    public required Track Track { get; init; }

    /// <summary>Optional search text; candidate confidence is still measured against Track.</summary>
    public string? SearchKeyword { get; init; }

    public IReadOnlyList<string> TargetNamespaces { get; init; } = [];
    public int LimitPerSource { get; init; } = 3;
    public double MinimumConfidence { get; init; } = 0.7;
}

public sealed record MetadataMatch
{
    public required string MetadataProviderId { get; init; }
    public required string SourceNamespace { get; init; }
    public required Track Track { get; init; }
    public required double Confidence { get; init; }
    public required string MatchLevel { get; init; }
}

public sealed record MetadataLookupResult(
    IReadOnlyList<MetadataMatch> Matches,
    IReadOnlyDictionary<string, string> SourceErrors);

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
    Task<SearchResult> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Advertises the source namespaces accepted by a search provider when it
///     supports narrowing a search to one source.
/// </summary>
public interface ISearchNamespaceProvider
{
    IReadOnlyCollection<string> SearchNamespaces { get; }
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
    Task<Lyrics?> GetLyricsAsync(
        Track track,
        CancellationToken cancellationToken = default);
}

public interface IMetadataProvider
{
    IReadOnlyCollection<string> MetadataNamespaces { get; }

    Task<MetadataLookupResult> LookupMetadataAsync(
        MetadataLookupRequest request,
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