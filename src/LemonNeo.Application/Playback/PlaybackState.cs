using LemonNeo.Core.Playback;
using System.Collections.Immutable;

namespace LemonNeo.Application.Playback;

public sealed record PlaybackQueueState
{
    public int FormatVersion { get; init; } = 1;
    public ImmutableArray<PlaybackQueueItem> Items { get; init; } = [];
    public Guid? CurrentItemId { get; init; }
    public PlaybackMode Mode { get; init; }
    public ImmutableArray<Guid> ShuffleOrder { get; init; } = [];
    public int ShuffleCursor { get; init; } = -1;
    public PlaybackRequest Request { get; init; } = new();
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record PlaybackResumeState(Guid? ItemId = null, TimeSpan Position = default,
    PlaybackStatus Status = PlaybackStatus.Stopped);

public sealed record PlaybackPreferences(double Volume = 1, AudioQuality Quality = AudioQuality.Automatic);
