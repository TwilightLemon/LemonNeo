namespace LemonNeo.Core;

public readonly record struct MusicItemId(string ProviderId, string RemoteId)
{
    public override string ToString() => $"{ProviderId}:{RemoteId}";
}

public sealed record ExternalMusicId(
    string Namespace,
    string Value,
    string? Authority = null,
    string? Kind = null);

public sealed record MusicIdScheme(
    string Namespace,
    string? Authority,
    string? Kind,
    bool MatchAnyAuthority = false)
{
    public bool Accepts(ExternalMusicId id) =>
        string.Equals(Namespace, id.Namespace, StringComparison.OrdinalIgnoreCase) &&
        (MatchAnyAuthority || string.Equals(Authority, id.Authority, StringComparison.OrdinalIgnoreCase)) &&
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

public sealed record Track
{
    public required MusicItemId Id { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<ArtistRef> Artists { get; init; } = [];
    public AlbumRef? Album { get; init; }
    public TimeSpan? Duration { get; init; }
    public Uri? ArtworkUri { get; init; }
    public IReadOnlyList<ExternalMusicId> ExternalIds { get; init; } = [];

    /// <summary>
    /// Indicates whether the track's originating provider declares a native playback path.
    /// This is a provider-supplied availability hint, not a global playback permission:
    /// tracks marked <see langword="false"/> may still be played through another provider
    /// after metadata matching and playback-source discovery.
    /// </summary>
    public bool IsPlayable { get; init; }

    public TrackReference ToReference() => new()
    {
        Id = Id,
        Title = Title,
        Artists = Artists,
        Album = Album,
        Duration = Duration,
        ExternalIds = ExternalIds
    };
}

public sealed record TrackReference
{
    public required MusicItemId Id { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<ArtistRef> Artists { get; init; } = [];
    public AlbumRef? Album { get; init; }
    public TimeSpan? Duration { get; init; }
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
    public required TrackReference Track { get; init; }
    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class PlaybackQueueState
{
    public int FormatVersion { get; init; } = 1;
    public List<PlaybackQueueItem> Items { get; init; } = [];
    public Guid? CurrentItemId { get; init; }
    public PlaybackMode Mode { get; init; }
    public List<Guid> ShuffleOrder { get; init; } = [];
    public int ShuffleCursor { get; init; } = -1;
    public PlaybackRequest Request { get; init; } = new();
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
    public required TrackReference Track { get; set; }
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
    public List<Track> Tracks { get; init; } = [];
    public List<Playlist> Playlists { get; init; } = [];
    public List<PlaylistMirror> Mirrors { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
