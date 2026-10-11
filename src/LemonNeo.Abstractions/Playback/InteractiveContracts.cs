using LemonNeo.Core.Music;

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
    PlayerState State,
    Track? Track,
    TimeSpan Position,
    TimeSpan? Duration,
    PlaybackControls Controls);

public sealed class InteractivePlaybackChangedEventArgs(InteractivePlaybackSnapshot snapshot) : EventArgs
{
    public InteractivePlaybackSnapshot Snapshot { get; } = snapshot;
}

/// <summary>
///     Platform-independent access to the host's playback authority. Events can run
///     on background threads; subscribers must return promptly and unsubscribe at
///     shutdown. ExecuteAsync serializes external commands and propagates failures.
/// </summary>
public interface IPlaybackInteraction
{
    InteractivePlaybackSnapshot Snapshot { get; }
    event EventHandler<InteractivePlaybackChangedEventArgs>? Changed;

    Task ExecuteAsync(PlaybackCommand command, TimeSpan? position = null,
        CancellationToken cancellationToken = default);
}