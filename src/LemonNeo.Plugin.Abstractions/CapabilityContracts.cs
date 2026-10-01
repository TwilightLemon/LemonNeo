using LemonNeo.Core;

namespace LemonNeo.Plugin.Abstractions;

public sealed record SearchRequest(string Keyword, int Limit = 50);

public sealed record SearchResult(string ProviderId, IReadOnlyList<Track> Tracks);

public sealed record RemotePlaylistSummary(string Id, string Name, int? TrackCount = null);

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

public sealed record LyricsLine(
    string Text,
    int? StartTimeMs = null,
    int? EndTimeMs = null,
    IReadOnlyDictionary<string, string>? Translations = null,
    string? Romanization = null,
    string? BackgroundText = null);

public sealed record Lyrics(
    string Text,
    string? Language = null,
    bool IsSynchronized = false,
    IReadOnlyList<LyricsLine>? Lines = null,
    string? Source = null,
    ExternalMusicId? SourceId = null,
    string? Translation = null);

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
    public required TrackReference Track { get; init; }
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
    Browser,
    DeviceCode,
    Password,
    QrCode
}

public sealed record AuthenticationState(bool IsAuthenticated, string? AccountName = null);

public interface ISearchProvider
{
    Task<SearchResult> SearchAsync(
        SearchRequest request,
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
        TrackReference track,
        PlaybackRequest request,
        CancellationToken cancellationToken = default);
}

public interface ILyricsProvider
{
    Task<Lyrics?> GetLyricsAsync(
        TrackReference track,
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

    Task<TrackReference?> ResolveTrackIdAsync(
        ExternalMusicId identifier,
        TrackMetadata metadata,
        CancellationToken cancellationToken = default);
}

public interface IAuthenticationProvider
{
    AuthenticationState State { get; }

    Task<IReadOnlyList<AuthMethod>> GetAvailableMethodsAsync(
        CancellationToken cancellationToken = default);
}

public enum PlayerState
{
    Stopped,
    Playing,
    Paused
}

public interface IPlayer
{
    PlayerState State { get; }
    TrackReference? CurrentTrack { get; }

    Task PlayAsync(
        TrackReference track,
        PlaybackSource source,
        CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
