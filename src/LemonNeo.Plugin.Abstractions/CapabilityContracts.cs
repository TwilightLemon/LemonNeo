using LemonNeo.Core;

namespace LemonNeo.Plugin.Abstractions;

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

public sealed record FolderScanRequest(string DirectoryPath, bool Recursive = true);

public sealed record FolderScanResult(
    string DirectoryPath,
    string SuggestedPlaylistName,
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<string> SkippedFiles);

public sealed record TrackMetadata
{
    public required string Title { get; init; }
    public IReadOnlyList<string> Artists { get; init; } = [];
    public string? Album { get; init; }
    public TimeSpan? Duration { get; init; }
    public Uri? ArtworkUri { get; init; }
    public IReadOnlyList<ExternalMusicId> ExternalIds { get; init; } = [];
}

public sealed record MetadataLookupRequest
{
    public required Track Track { get; init; }
    public IReadOnlyList<string> TargetNamespaces { get; init; } = [];
    public int LimitPerSource { get; init; } = 3;
    public double MinimumConfidence { get; init; } = 0.7;
}

public sealed record MetadataMatch
{
    public required string MetadataProviderId { get; init; }
    public required string SourceNamespace { get; init; }
    public required TrackMetadata Metadata { get; init; }
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
/// Advertises the source namespaces accepted by a search provider when it
/// supports narrowing a search to one source.
/// </summary>
public interface ISearchNamespaceProvider
{
    IReadOnlyCollection<string> SearchNamespaces { get; }
}

/// <summary>
/// Retrieves a track by the provider's native identifier without approximating
/// the operation through text search.
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
/// Provides named groups of user playlists when a service distinguishes between
/// owned, followed, purchased, or other playlist-like collections.
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

public interface IFolderImportProvider
{
    Task<FolderScanResult> ScanFolderAsync(
        FolderScanRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPlaybackProvider
{
    Task<PlaybackSource?> ResolvePlaybackAsync(
        Track track,
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

public interface ITrackIdResolverProvider
{
    IReadOnlyCollection<MusicIdScheme> AcceptedIdSchemes { get; }

    Task<Track?> ResolveTrackIdAsync(
        ExternalMusicId identifier,
        TrackMetadata metadata,
        CancellationToken cancellationToken = default);
}

public sealed record PlaybackSourceProviderRegistration(
    string ProviderId,
    IMusicProvider MusicProvider,
    IPlaybackProvider PlaybackProvider,
    IReadOnlyList<MusicIdScheme> AcceptedIdSchemes);

/// <summary>
/// Tracks the playback-source providers currently registered by loaded plugins.
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

public enum PlayerState
{
    Stopped,
    Playing,
    Paused
}

public sealed record PlayerTimeline(
    TimeSpan Position,
    TimeSpan? Duration,
    bool HasEnded = false);

public interface IPlayer
{
    PlayerState State { get; }
    Track? CurrentTrack { get; }
    PlayerTimeline Timeline { get; }

    Task PlayAsync(
        Track track,
        PlaybackSource source,
        CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Receives the original encoded audio bytes from the player's existing network
/// request. The host owns this sink and its lifetime; players never persist audio.
/// </summary>
public interface IPlaybackDownloadSink : IDisposable
{
    /// <summary>
    /// Reports the full response size. Null or non-positive lengths disable
    /// caching of live/unbounded streams. May arrive after Write or Complete.
    /// </summary>
    void SetExpectedLength(long? length);

    /// <summary>
    /// Consumes all bytes in the chunk synchronously without retaining its memory.
    /// The player owns the underlying buffer and may reuse it after this returns.
    /// Chunks must be ordered, contiguous bytes of the same playback download.
    /// </summary>
    void Write(ReadOnlySpan<byte> chunk);

    /// <summary>
    /// Signals the end of the download. The host publishes only when the received
    /// byte count matches a positive expected length; disconnects are incomplete.
    /// </summary>
    void Complete();

    /// <summary>Discards an interrupted or failed download.</summary>
    void Abort();
}

/// <summary>
/// Optional capability to forward chunks of the same HTTP download being played
/// to a host-owned sink. Playback must start while that download is in progress;
/// this must not issue a separate request or download the whole file first.
/// </summary>
public interface IStreamingPlayer : IPlayer
{
    Task PlayAsync(
        Track track,
        HttpPlaybackSource source,
        IPlaybackDownloadSink downloadSink,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional capability for restoring a saved playback position.</summary>
public interface ISeekablePlayer : IPlayer
{
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
}
