using LemonNeo.Core.Music;
using System.Collections.Immutable;

namespace LemonNeo.Core.Playback;

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
    /// The host's audio cache does not use this value: it selects cached
    /// audio by external music IDs and the playback request before URL resolution.
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
