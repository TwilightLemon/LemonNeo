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

    /// <summary>
    ///     Opens a request-owned stream without changing the active stream or starting audio.
    ///     Slow I/O must not hold the active player's lock. A cancelled preparation must
    ///     release only its own resources, even when another request has already started.
    /// </summary>
    Task<IPreparedPlayback> PrepareAsync(
        Track track,
        PlaybackSource source,
        IPlaybackDownloadSink? downloadSink = null,
        CancellationToken cancellationToken = default);

    Task PlayAsync(
        Track track,
        PlaybackSource source,
        CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Moves playback to the requested position in the loaded stream.</summary>
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);

    /// <summary>Returns application volume from 0 (muted) to 1 (full volume), including while stopped.</summary>
    Task<double> GetVolumeAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets application volume independently of the system volume and retains it for subsequent streams.</summary>
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);
}

/// <summary>A request-owned stream. Disposal releases this stream, including after activation.</summary>
public interface IPreparedPlayback : IDisposable
{
    AudioStreamInfo? AudioInfo { get; }

    /// <summary>Starts the prepared stream with volume. Must be brief and perform no network I/O.</summary>
    void Activate(double volume);
}

/// <summary>Optional decoder capability reporting the currently loaded audio's original format.</summary>
public interface IAudioInfoPlayer : IPlayer
{
    /// <summary>Null when no stream is loaded. Individual fields may be unknown; reads must not perform network I/O.</summary>
    AudioStreamInfo? AudioInfo { get; }
}

/// <summary>Optional decoder capability providing frequency-domain data for visualizations.</summary>
public interface IFftPlayer : IPlayer
{
    /// <summary>Number of frequency bins written by <see cref="TryGetFftData"/>.</summary>
    int FftBinCount { get; }

    /// <summary>
    ///     Tries to synchronously write non-negative FFT magnitudes ordered from low to high frequency.
    ///     The read must not perform network I/O. Returns false when no frame is available or
    ///     <paramref name="destination"/> is too small.
    /// </summary>
    bool TryGetFftData(Span<float> destination);
}

/// <summary>
///     Receives the original encoded audio bytes from the player's existing network
///     request. The host owns this sink and its lifetime; players never persist audio.
/// </summary>
public interface IPlaybackDownloadSink : IDisposable
{
    /// <summary>
    ///     Reports the full response size. Null or non-positive lengths disable
    ///     caching of live/unbounded streams. May arrive after Write or Complete.
    /// </summary>
    void SetExpectedLength(long? length);

    /// <summary>
    ///     Consumes all bytes in the chunk synchronously without retaining its memory.
    ///     The player owns the underlying buffer and may reuse it after this returns.
    ///     Chunks must be ordered, contiguous bytes of the same playback download.
    /// </summary>
    void Write(ReadOnlySpan<byte> chunk);

    /// <summary>
    ///     Signals the end of the download. The host publishes only when the received
    ///     byte count matches a positive expected length; disconnects are incomplete.
    /// </summary>
    void Complete();

    /// <summary>Discards an interrupted or failed download.</summary>
    void Abort();
}

/// <summary>
///     Optional capability to forward chunks of the same HTTP download being played
///     to a host-owned sink. Playback must start while that download is in progress;
///     this must not issue a separate request or download the whole file first.
/// </summary>
public interface IStreamingPlayer : IPlayer
{
    Task PlayAsync(
        Track track,
        HttpPlaybackSource source,
        IPlaybackDownloadSink downloadSink,
        CancellationToken cancellationToken = default);
}
