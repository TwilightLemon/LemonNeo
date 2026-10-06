using LemonNeo.Core.Library;

namespace LemonNeo.Abstractions.Sync;

public interface ILibrarySyncProvider
{
    string Id { get; }
    Task<SyncResult> PullAsync(CancellationToken cancellationToken = default);
    Task<SyncResult> PushAsync(CancellationToken cancellationToken = default);
    Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record SyncResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public string? LocalRevision { get; init; }
    public string? RemoteRevision { get; init; }
    public MusicLibrary? BaseLibrary { get; init; }
    public MusicLibrary? RemoteLibrary { get; init; }
}

public enum SyncState
{
    Synchronized,
    LocalChanges,
    RemoteChanges,
    Diverged,
    Unavailable
}

public sealed record SyncStatus(
    SyncState State,
    string? LocalRevision = null,
    string? RemoteRevision = null,
    string? Message = null);

public enum SyncConflictType
{
    PlaylistNameConflict,
    PlaylistDescriptionConflict,
    PlaylistItemConflict,
    DeleteModifyConflict,
    OrderConflict
}

public sealed record SyncConflict
{
    public required string Path { get; init; }
    public required SyncConflictType Type { get; init; }
    public object? BaseValue { get; init; }
    public object? LocalValue { get; init; }
    public object? RemoteValue { get; init; }
}

public sealed record MergeResult(
    MusicLibrary Merged,
    IReadOnlyList<SyncConflict> Conflicts,
    int AddedPlaylistItems,
    int ChangedPlaylists);
