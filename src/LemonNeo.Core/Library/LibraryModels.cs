using LemonNeo.Core.Music;

namespace LemonNeo.Core.Library;

public sealed class Playlist
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<PlaylistItem> Items { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlaylistItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Track Track { get; set; }
    public required string SortKey { get; set; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PlaylistMirror
{
    public required Guid PlaylistId { get; init; }
    public required string ProviderId { get; init; }
    public required string RemotePlaylistId { get; init; }
    public DateTimeOffset? LastSynchronizedAt { get; init; }
}

public sealed class MusicLibrary
{
    public int FormatVersion { get; init; } = 1;
    public List<Playlist> Playlists { get; init; } = [];
    public List<PlaylistMirror> Mirrors { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}