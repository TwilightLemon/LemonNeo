using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Core.Library;

public sealed class Playlist
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DecodeProfile? DecodeProfile { get; set; }
    public List<PlaylistItem> Items { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlaylistItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DecodeProfile? DecodeProfile { get; set; }
    public required Track Track { get; set; }
    public required string SortKey { get; set; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool HasMetadataOverride { get; set; }
    /// <summary>Locally edited schemes, including removals, that source refreshes must preserve.</summary>
    public IReadOnlyList<MusicIdScheme> ExternalIdOverrides { get; set; } = [];
}

public enum PlaylistSourceKind { Provider, Folder }

public sealed record PlaylistMirror
{
    public required Guid PlaylistId { get; init; }
    public PlaylistSourceKind SourceKind { get; init; }
    public string ProviderId { get; init; } = "";
    public string RemotePlaylistId { get; init; } = "";
    public string? AccountId { get; init; }
    public string? DirectoryPath { get; init; }
    public bool Recursive { get; init; } = true;
    public bool IsPaused { get; init; }
    public DateTimeOffset? LastSynchronizedAt { get; init; }
    public string? LastError { get; init; }
    public string? ContentFingerprint { get; init; }
}

public sealed class MusicLibrary
{
    public int FormatVersion { get; init; } = 1;
    public List<Playlist> Playlists { get; init; } = [];
    public List<PlaylistMirror> Mirrors { get; init; } = [];
    public bool IsPlaylistSyncPaused { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
