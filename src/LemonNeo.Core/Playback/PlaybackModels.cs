using LemonNeo.Core.Music;

namespace LemonNeo.Core.Playback;

/// <summary>Host session state; a selected track can exist before a decoder is ready.</summary>
public enum PlaybackState
{
    // The first three values are also used by existing playback resume records.
    Stopped = 0,
    Playing = 1,
    Paused = 2,
    Loading = 3,
    Failed = 4
}

public abstract record PlaybackSource
{
    /// <summary>Actual resource quality; null means the provider cannot determine it.</summary>
    public AudioQuality? Quality { get; init; }

    public int? BitrateKbps { get; init; }
}

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
    Lossless,
    HiRes
}

public static class AudioQualities
{
    public static IReadOnlyList<AudioQuality> Descending { get; } =
        [AudioQuality.HiRes, AudioQuality.Lossless, AudioQuality.High, AudioQuality.Medium, AudioQuality.Low];

    /// <summary>Prefers measurements, retaining a provider's declaration only when it is not contradicted.</summary>
    public static AudioQuality? Resolve(AudioStreamInfo? info, AudioQuality? reported)
    {
        if (Classify(info) is { } measured) return measured;
        if (info is { IsLossless: false } && reported is AudioQuality.Lossless or AudioQuality.HiRes) return null;
        return reported is { } quality && quality != AudioQuality.Automatic && Enum.IsDefined(quality) ? quality : null;
    }

    /// <summary>Classifies measured audio. Bitrate alone cannot establish lossless or HiRes quality.</summary>
    public static AudioQuality? Classify(AudioStreamInfo? info)
    {
        return info switch
        {
            { IsLossless: true, BitsPerSample: > 16 } or { IsLossless: true, SampleRateHz: > 48000 } => AudioQuality
                .HiRes,
            { IsLossless: true } => AudioQuality.Lossless,
            { IsLossless: false, BitrateKbps: > 192 } => AudioQuality.High,
            { IsLossless: false, BitrateKbps: > 128 } => AudioQuality.Medium,
            { IsLossless: false, BitrateKbps: > 0 } => AudioQuality.Low,
            _ => null
        };
    }

    public static IEnumerable<AudioQuality> SearchOrder(AudioQuality preferred)
    {
        return preferred == AudioQuality.Automatic
            ? Descending
            : new[] { preferred }.Concat(Descending.Where(q => q != preferred));
    }
}

public sealed record PlaybackRequest(
    AudioQuality Quality = AudioQuality.Automatic,
    string? RequiredSourceProviderId = null,
    ExternalMusicId? RequiredSourceId = null,
    // Host-only: resolve saved playback preferences at each queue playback; explicit Auto still means highest.
    bool UseQualityPreference = false,
    string? PreferredPlayerId = null);

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
