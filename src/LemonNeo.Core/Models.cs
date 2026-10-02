namespace LemonNeo.Core;

public readonly record struct MusicItemId(string ProviderId, string RemoteId)
{
    public override string ToString() => $"{ProviderId}:{RemoteId}";
}

public sealed record ExternalMusicId(
    string Namespace,
    string Value,
    string? Kind = null);

public sealed record MusicIdScheme(
    string Namespace,
    string? Kind)
{
    public bool Accepts(ExternalMusicId id) =>
        string.Equals(Namespace, id.Namespace, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Kind, id.Kind, StringComparison.OrdinalIgnoreCase);
}

public static class MusicIdNamespaces
{
    public const string Isrc = "isrc";
    public const string File = "file";
    public const string QQMusic = "qqmusic";
    public const string NeteaseMusic = "netease";
    public const string KugouMusic = "kugou";
    public const string AppleMusic = "applemusic";
    public const string Spotify = "spotify";
    public const string Musixmatch = "musixmatch";
    public const string SodaMusic = "sodamusic";
    public const string LrcLib = "lrclib";
}

public sealed record ArtistRef
{
    public required string Name { get; init; }
    public MusicItemId? Id { get; init; }
}

public sealed record AlbumRef
{
    public required string Title { get; init; }
    public MusicItemId? Id { get; init; }
}

public sealed record Artist
{
    public required MusicItemId Id { get; init; }
    public required string Name { get; init; }
}

public sealed record Album
{
    public required MusicItemId Id { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<ArtistRef> Artists { get; init; } = [];
    public Uri? ArtworkUri { get; init; }
}

/// <summary>
/// A self-contained metadata snapshot used by search results, playlist items, and
/// playback queues. Playback availability is deliberately not stored here; it is
/// determined at resolution time by playback-source providers.
/// </summary>
public sealed record Track
{
    public required MusicItemId Id { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<ArtistRef> Artists { get; init; } = [];
    public AlbumRef? Album { get; init; }
    public TimeSpan? Duration { get; init; }
    public Uri? ArtworkUri { get; init; }
    public IReadOnlyList<ExternalMusicId> ExternalIds { get; init; } = [];
}

public abstract record PlaybackSource;

public sealed record HttpPlaybackSource : PlaybackSource
{
    public required Uri Uri { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>();
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsDirectAudio { get; init; } = true;
    /// <summary>
    /// Optional provider-qualified identity for this exact audio variant (track,
    /// quality, and format). It stays stable when a signed playback URL changes.
    /// </summary>
    public string? CacheKey { get; init; }
}

public sealed record FilePlaybackSource(string Path) : PlaybackSource;

public enum AudioQuality
{
    Automatic,
    Low,
    Medium,
    High,
    Lossless
}

public sealed record PlaybackRequest(
    AudioQuality Quality = AudioQuality.Automatic,
    string? RequiredSourceProviderId = null);

public enum PlaybackMode
{
    Sequential,
    RepeatAll,
    Shuffle
}

public sealed record PlaybackQueueItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Track Track { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum PlaybackStatus
{
    Stopped,
    Playing,
    Paused
}

public sealed record PlaybackQueueState
{
    public int FormatVersion { get; init; } = 1;
    public List<PlaybackQueueItem> Items { get; init; } = [];
    public Guid? CurrentItemId { get; init; }
    public PlaybackMode Mode { get; init; }
    public List<Guid> ShuffleOrder { get; init; } = [];
    public int ShuffleCursor { get; init; } = -1;
    public PlaybackRequest Request { get; init; } = new();
    public TimeSpan Position { get; init; }
    public PlaybackStatus Status { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

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
