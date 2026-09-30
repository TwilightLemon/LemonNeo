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

public sealed record Lyrics(string Text, string? Language = null, bool IsSynchronized = false);

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
