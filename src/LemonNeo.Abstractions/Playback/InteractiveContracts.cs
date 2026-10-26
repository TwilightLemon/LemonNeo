using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Abstractions.Playback;

[Flags]
public enum PlaybackControls
{
    None = 0,
    Play = 1,
    Pause = 2,
    Stop = 4,
    Next = 8,
    Previous = 16,
    Seek = 32
}

public enum PlaybackCommand
{
    Play,
    Pause,
    TogglePlayPause,
    Stop,
    Next,
    Previous,
    Seek
}

/// <summary>
///     Uses the original queue track, including metadata and ArtworkUri, rather than
///     a source provider's replacement track. Restored selections remain stopped.
/// </summary>
public sealed record InteractivePlaybackSnapshot(
    PlaybackState State,
    Track? Track,
    TimeSpan Position,
    TimeSpan? Duration,
    PlaybackControls Controls,
    long RequestId = 0,
    long Revision = 0,
    double Volume = 1);

[Flags]
public enum PlaybackUpdateKind
{
    None = 0,
    Metadata = 1,
    Timeline = 2,
    State = 4,
    Queue = 8,
    Volume = 16,
    All = Metadata | Timeline | State | Queue | Volume
}

public sealed class InteractivePlaybackChangedEventArgs(InteractivePlaybackSnapshot snapshot) : EventArgs
{
    public InteractivePlaybackSnapshot Snapshot { get; } = snapshot;
}

/// <summary>
///     Platform-independent access to the host's playback authority. Events can run
///     on background threads; subscribers must return promptly and unsubscribe at
///     shutdown. ExecuteAsync submits commands without waiting for source discovery;
///     request completion and failure are published by the host session.
/// </summary>
public interface IPlaybackInteraction
{
    InteractivePlaybackSnapshot Snapshot { get; }
    event EventHandler<InteractivePlaybackChangedEventArgs>? MetadataChanged;
    event EventHandler<InteractivePlaybackChangedEventArgs>? TimelineChanged;
    event EventHandler<InteractivePlaybackChangedEventArgs>? StateChanged;
    event EventHandler<InteractivePlaybackChangedEventArgs>? QueueChanged;
    event EventHandler<InteractivePlaybackChangedEventArgs>? VolumeChanged;

    Task ExecuteAsync(PlaybackCommand command, TimeSpan? position = null,
        CancellationToken cancellationToken = default);
}
