using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Abstractions.Playback;

public enum PlayerState
{
    Stopped,
    Playing,
    Paused
}

public sealed record PlayerTimeline(
    TimeSpan Position,
    TimeSpan? Duration,
    bool HasEnded = false);

public interface IPlayer
{
    PlayerState State { get; }
    Track? CurrentTrack { get; }
    PlayerTimeline Timeline { get; }

    Task PlayAsync(
        Track track,
        PlaybackSource source,
        CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Receives the original encoded audio bytes from the player's existing network
/// request. The host owns this sink and its lifetime; players never persist audio.
/// </summary>
public interface IPlaybackDownloadSink : IDisposable
{
    /// <summary>
    /// Reports the full response size. Null or non-positive lengths disable
    /// caching of live/unbounded streams. May arrive after Write or Complete.
    /// </summary>
    void SetExpectedLength(long? length);

    /// <summary>
    /// Consumes all bytes in the chunk synchronously without retaining its memory.
    /// The player owns the underlying buffer and may reuse it after this returns.
    /// Chunks must be ordered, contiguous bytes of the same playback download.
    /// </summary>
    void Write(ReadOnlySpan<byte> chunk);

    /// <summary>
    /// Signals the end of the download. The host publishes only when the received
    /// byte count matches a positive expected length; disconnects are incomplete.
    /// </summary>
    void Complete();

    /// <summary>Discards an interrupted or failed download.</summary>
    void Abort();
}

/// <summary>
/// Optional capability to forward chunks of the same HTTP download being played
/// to a host-owned sink. Playback must start while that download is in progress;
/// this must not issue a separate request or download the whole file first.
/// </summary>
public interface IStreamingPlayer : IPlayer
{
    Task PlayAsync(
        Track track,
        HttpPlaybackSource source,
        IPlaybackDownloadSink downloadSink,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional capability for restoring a saved playback position.</summary>
public interface ISeekablePlayer : IPlayer
{
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
}

/// <summary>Optional capability for application volume, independent of the system volume.</summary>
public interface IVolumePlayer : IPlayer
{
    /// <summary>Returns volume from 0 (muted) to 1 (full volume), including while stopped.</summary>
    Task<double> GetVolumeAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets a finite volume between 0 and 1 and retains it for subsequent streams.</summary>
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);
}
