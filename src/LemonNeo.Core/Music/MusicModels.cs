namespace LemonNeo.Core.Music;

public readonly record struct MusicItemId(string ProviderId, string RemoteId)
{
    public override string ToString()
    {
        return $"{ProviderId}:{RemoteId}";
    }
}

public sealed record ExternalMusicId(
    string Namespace,
    string Value,
    string? Kind = null);

public sealed record MusicIdScheme(
    string Namespace,
    string? Kind)
{
    public bool Accepts(ExternalMusicId id)
    {
        return string.Equals(Namespace, id.Namespace, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Kind, id.Kind, StringComparison.OrdinalIgnoreCase);
    }
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
///     A self-contained metadata snapshot used by search results, metadata matches,
///     playlist items, and playback queues. Playback availability is determined at
///     resolution time by playback-source providers.
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