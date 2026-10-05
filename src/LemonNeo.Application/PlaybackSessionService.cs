using LemonNeo.Core;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application;

public sealed record PlaybackTimelineInfo(TimeSpan Position, TimeSpan? Duration)
{
    public double? Progress => Duration is { Ticks: > 0 }
        ? Math.Clamp(Position.TotalMilliseconds / Duration.Value.TotalMilliseconds, 0, 1)
        : null;
}

public sealed record PlaybackSessionSnapshot(
    PlayerState State,
    Track? CurrentTrack,
    PlaybackTimelineInfo Timeline,
    PlaybackMode Mode,
    int CurrentIndex,
    int QueueCount,
    string? PlayerId,
    string? RequiredSourceProviderId,
    PlaybackStatus? RestoredStatus = null);

public sealed class PlaybackSessionChangedEventArgs(PlaybackSessionSnapshot snapshot) : EventArgs
{
    public PlaybackSessionSnapshot Snapshot { get; } = snapshot;
}

public sealed class PlaybackCompletedEventArgs(
    Track completedTrack,
    Track? nextTrack) : EventArgs
{
    public Track CompletedTrack { get; } = completedTrack;
    public Track? NextTrack { get; } = nextTrack;
}

public sealed class PlaybackSessionService(
    PlaybackResolver resolver,
    IPlaybackQueue queue,
    IPlaybackStateRepository repository,
    ILogger<PlaybackSessionService> logger) : IAsyncDisposable
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _snapshotGate = new();
    private readonly CancellationTokenSource _monitorCancellation = new();
    private PlaybackSessionSnapshot _snapshot = new(
        PlayerState.Stopped,
        null,
        new PlaybackTimelineInfo(TimeSpan.Zero, null),
        PlaybackMode.Sequential,
        -1,
        0,
        null,
        null);
    private Task? _monitorTask;
    private IPlayer? _snapshotPlayer;
    private MusicItemId? _snapshotPlayerTrackId;
    private bool _completionHandled;
    private bool _initialized;
    private bool _disposed;
    private Guid? _restoredItemId;
    private TimeSpan _restoredPosition;
    private PlaybackStatus? _restoredStatus;
    private DateTimeOffset _lastSavedAt;

    public event EventHandler<PlaybackSessionChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackSessionChangedEventArgs>? TimelineChanged;
    public event EventHandler<EventArgs>? VolumeChanged;
    public event EventHandler<PlaybackCompletedEventArgs>? PlaybackCompleted;

    public IPlaybackQueue Queue => queue;
    public double Volume => resolver.Volume;
    public bool CanAdjustVolume => resolver.CanAdjustVolume;

    public PlaybackSessionSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>
    /// Reads the player's current position for synchronized displays. The cached
    /// Snapshot and monitor events remain suitable for session state notifications.
    /// </summary>
    public PlaybackSessionSnapshot GetCurrentSnapshot()
    {
        PlaybackSessionSnapshot snapshot;
        IPlayer? player;
        MusicItemId? playerTrackId;
        lock (_snapshotGate)
        {
            snapshot = _snapshot;
            player = _snapshotPlayer;
            playerTrackId = _snapshotPlayerTrackId;
        }
        if (player is null) return snapshot;

        // Do not wait for the monitor or its persistence/network operations, and
        // do not extrapolate a wall clock: audio may be paused, stalled, or sought.
        var before = player.CurrentTrack?.Id;
        var state = player.State;
        var timeline = player.Timeline;
        var after = player.CurrentTrack?.Id;
        lock (_snapshotGate)
        {
            if (!ReferenceEquals(snapshot, _snapshot)) return _snapshot;
        }

        // A provider can reuse its player while advancing to a different source
        // track. Never combine that track's position with the previous lyrics.
        if (before != playerTrackId || after != playerTrackId)
            return snapshot with { State = PlayerState.Stopped };

        return snapshot with
        {
            State = state,
            Timeline = new PlaybackTimelineInfo(timeline.Position, timeline.Duration ?? snapshot.Timeline.Duration)
        };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
            {
                return;
            }

            var saved = await repository.LoadAsync(cancellationToken);
            queue.Restore(saved);
            await resolver.SetVolumeAsync(double.IsFinite(saved.Volume) ? Math.Clamp(saved.Volume, 0, 1) : 1, cancellationToken);
            if (queue.CurrentItem is { } restoredItem && restoredItem.Id == saved.CurrentItemId)
            {
                _restoredItemId = saved.CurrentItemId;
                _restoredPosition = saved.Position < TimeSpan.Zero ? TimeSpan.Zero : saved.Position;
                if (restoredItem.Track.Duration is { } duration && _restoredPosition > duration)
                {
                    _restoredPosition = duration;
                }
                _restoredStatus = Enum.IsDefined(saved.Status) ? saved.Status : PlaybackStatus.Stopped;
            }
            _lastSavedAt = DateTimeOffset.UtcNow;
            _initialized = true;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
            _monitorTask = MonitorAsync(_monitorCancellation.Token);
        }
        finally
        {
            _gate.Release();
        }

        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
    }

    public async Task PlayTrackAsync(
        Track track,
        PlaybackRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        try
        {
            EnsureReady();
            queue.Replace([track]);
            ResetRestoredState();
            queue.SetRequest(request ?? new PlaybackRequest());
            PublishSelectedTrack();
            await repository.SaveAsync(queue.Snapshot() with { Volume = Volume }, cancellationToken);
            await resolver.PlayAsync(track, queue.Request, cancellationToken);
            await SaveSessionStateAsync(cancellationToken);
            _completionHandled = false;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        catch
        {
            await StopAfterFailureAsync();
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
    }

    public async Task PlayPlaylistAsync(
        IEnumerable<Track> tracks,
        int startIndex = 0,
        PlaybackRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var values = tracks.ToArray();
        if (values.Length == 0)
        {
            throw new InvalidOperationException("Cannot play an empty playlist.");
        }

        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        try
        {
            EnsureReady();
            queue.Replace(values, startIndex);
            ResetRestoredState();
            queue.SetRequest(request ?? new PlaybackRequest());
            PublishSelectedTrack();
            await repository.SaveAsync(queue.Snapshot() with { Volume = Volume }, cancellationToken);
            await resolver.PlayAsync(queue.CurrentItem!.Track, queue.Request, cancellationToken);
            await SaveSessionStateAsync(cancellationToken);
            _completionHandled = false;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        catch
        {
            await StopAfterFailureAsync();
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
    }

    public async Task<Track?> NextAsync(CancellationToken cancellationToken = default) =>
        await MoveAsync(queue.MoveNext, stopWhenUnavailable: true, cancellationToken);

    public async Task<Track?> PreviousAsync(CancellationToken cancellationToken = default) =>
        await MoveAsync(queue.MovePrevious, stopWhenUnavailable: false, cancellationToken);

    public Task<Track?> PlayCurrentAsync(CancellationToken cancellationToken = default) =>
        PlayCurrentCoreAsync(null, cancellationToken);

    /// <summary>Selects and plays an existing queue item without replacing the queue or its request.</summary>
    public Task<Track?> PlayQueueItemAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        PlayCurrentCoreAsync(itemId, cancellationToken);

    private async Task<Track?> PlayCurrentCoreAsync(Guid? itemId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        PlaybackQueueItem? item;
        try
        {
            EnsureReady();
            if (itemId is { } selectedId)
            {
                if (queue.Select(selectedId) is null)
                    throw new ArgumentException($"Queue item '{selectedId}' was not found.", nameof(itemId));
                if (_restoredItemId != selectedId) ResetRestoredState();
            }
            item = queue.CurrentItem;
            if (item is null)
            {
                return null;
            }

            PublishSelectedTrack();
            await resolver.PlayAsync(item.Track, queue.Request, cancellationToken);
            if (_restoredItemId == item.Id && _restoredPosition > TimeSpan.Zero)
            {
                if (resolver.ActivePlayer?.Player is ISeekablePlayer seekable)
                {
                    await seekable.SeekAsync(_restoredPosition, cancellationToken);
                }
                else
                {
                    logger.LogInformation("Player {PlayerId} does not support seeking; starting from the beginning.",
                        resolver.ActivePlayer?.Id);
                }
            }
            ResetRestoredState();
            await SaveSessionStateAsync(cancellationToken);
            _completionHandled = false;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        catch
        {
            await StopAfterFailureAsync();
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
        return item.Track;
    }

    public async Task PauseAsync(CancellationToken cancellationToken = default)
    {
        await ChangePlayerStateAsync(resolver.PauseAsync, cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (resolver.ActivePlayer is null && queue.CurrentItem is not null)
        {
            await PlayCurrentAsync(cancellationToken);
            return;
        }
        await ChangePlayerStateAsync(resolver.ResumeAsync, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await ChangePlayerStateAsync(resolver.StopAsync, cancellationToken, discardRestoredState: true);
    }

    public bool CanSeek => resolver.ActivePlayer?.Player is ISeekablePlayer;

    public async Task<double> GetVolumeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            return await resolver.GetVolumeAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await resolver.SetVolumeAsync(volume, cancellationToken);
            await SaveSessionStateAsync(cancellationToken);
        }
        finally { _gate.Release(); }
        Raise(VolumeChanged, EventArgs.Empty);
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        try
        {
            EnsureReady();
            if (resolver.ActivePlayer?.Player is not ISeekablePlayer player)
                throw new NotSupportedException("The active player does not support seeking.");
            var duration = player.Timeline.Duration ?? queue.CurrentItem?.Track.Duration;
            if (duration is { } end && position > end) position = end;
            await player.SeekAsync(position, cancellationToken);
            _completionHandled = false;
            await SaveSessionStateAsync(cancellationToken);
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        finally
        {
            _gate.Release();
        }

        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
        Raise(TimelineChanged, new PlaybackSessionChangedEventArgs(snapshot));
    }

    public async Task SetModeAsync(
        PlaybackMode mode,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        try
        {
            EnsureReady();
            queue.SetMode(mode);
            await SaveSessionStateAsync(cancellationToken);
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        finally
        {
            _gate.Release();
        }

        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
    }

    public async Task ClearQueueAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        try
        {
            EnsureReady();
            await resolver.StopAsync(cancellationToken);
            queue.Clear();
            ResetRestoredState();
            await SaveSessionStateAsync(cancellationToken);
            _completionHandled = false;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        finally
        {
            _gate.Release();
        }

        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            await SaveSessionStateAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Track?> MoveAsync(
        Func<PlaybackQueueItem?> move,
        bool stopWhenUnavailable,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        PlaybackQueueItem? item;
        try
        {
            EnsureReady();
            item = move();
            if (item is not null || stopWhenUnavailable)
            {
                ResetRestoredState();
                PublishSelectedTrack();
                await repository.SaveAsync(queue.Snapshot() with { Volume = Volume }, cancellationToken);
            }
            if (item is null && stopWhenUnavailable)
            {
                await resolver.StopAsync(cancellationToken);
            }
            else if (item is not null)
            {
                await resolver.PlayAsync(item.Track, queue.Request, cancellationToken);
            }

            await SaveSessionStateAsync(cancellationToken);

            _completionHandled = false;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        catch
        {
            await StopAfterFailureAsync();
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
            throw;
        }
        finally
        {
            _gate.Release();
        }

        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
        return item?.Track;
    }

    private async Task ChangePlayerStateAsync(
        Func<CancellationToken, Task> change,
        CancellationToken cancellationToken,
        bool discardRestoredState = false)
    {
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        try
        {
            EnsureReady();
            await change(cancellationToken);
            if (discardRestoredState) ResetRestoredState();
            await SaveSessionStateAsync(cancellationToken);
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        finally
        {
            _gate.Release();
        }

        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MonitorInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await RefreshTimelineAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not refresh the playback timeline.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshTimelineAsync(CancellationToken cancellationToken)
    {
        PlaybackSessionSnapshot snapshot;
        PlaybackSessionSnapshot previous;
        Track? completedTrack = null;
        Track? nextTrack = null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized || _disposed)
            {
                return;
            }

            var player = resolver.ActivePlayer?.Player;
            var timeline = player?.Timeline;
            if (timeline is { HasEnded: true } && !_completionHandled)
            {
                _completionHandled = true;
                completedTrack = queue.CurrentItem?.Track ?? player?.CurrentTrack;
                var next = queue.MoveNext();
                ResetRestoredState();
                nextTrack = next?.Track;
                PublishSelectedTrack();
                if (next is null)
                {
                    await resolver.StopAsync(cancellationToken);
                }
                else
                {
                    try
                    {
                        await resolver.PlayAsync(next.Track, queue.Request, cancellationToken);
                        _completionHandled = false;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(
                            exception,
                            "Could not advance playback to {TrackId} after completion.",
                            next.Track.Id);
                        await resolver.StopAsync(cancellationToken);
                    }
                }

                await SaveSessionStateAsync(cancellationToken);
            }

            previous = Snapshot;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
            if (snapshot.State == PlayerState.Playing && DateTimeOffset.UtcNow - _lastSavedAt >= TimeSpan.FromSeconds(5))
            {
                await SaveSessionStateAsync(cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (completedTrack is not null)
        {
            Raise(PlaybackCompleted, new PlaybackCompletedEventArgs(completedTrack, nextTrack));
        }

        if (HasStateChanged(previous, snapshot))
        {
            Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
        }

        if (resolver.ActivePlayer is not null)
        {
            Raise(TimelineChanged, new PlaybackSessionChangedEventArgs(snapshot));
        }
    }

    private PlaybackSessionSnapshot CaptureSnapshot()
    {
        var activePlayer = resolver.ActivePlayer;
        var player = activePlayer?.Player;
        var current = queue.CurrentItem?.Track ?? player?.CurrentTrack;
        var playerTimeline = player?.Timeline;
        var duration = playerTimeline?.Duration ?? current?.Duration;
        return new PlaybackSessionSnapshot(
            player?.State ?? PlayerState.Stopped,
            current,
            new PlaybackTimelineInfo(playerTimeline?.Position ??
                (_restoredItemId == queue.CurrentItem?.Id ? _restoredPosition : TimeSpan.Zero), duration),
            queue.Mode,
            queue.CurrentIndex,
            queue.Items.Count,
            activePlayer?.Id,
            queue.Request.RequiredSourceProviderId,
            _restoredStatus);
    }

    private void ResetRestoredState()
    {
        _restoredItemId = null;
        _restoredPosition = TimeSpan.Zero;
        _restoredStatus = null;
    }

    private async Task SaveSessionStateAsync(CancellationToken cancellationToken = default)
    {
        if (queue.CurrentItem is { } item && resolver.CurrentQueueTrack is { } resolved &&
            item.Track.Id == resolved.Id)
        {
            // Retain discovered cache aliases in both the live queue and its saved snapshot.
            var identifiers = MusicCacheIdentity.GetIdentifiers(item.Track.ExternalIds.Concat(resolved.ExternalIds));
            if (!item.Track.ExternalIds.SequenceEqual(identifiers))
                queue.UpdateTrack(item.Id, item.Track with { ExternalIds = identifiers });
        }
        var snapshot = CaptureSnapshot();
        var state = queue.Snapshot() with
        {
            Volume = Volume,
            Position = snapshot.Timeline.Position,
            Status = snapshot.RestoredStatus ?? snapshot.State switch
            {
                PlayerState.Playing => PlaybackStatus.Playing,
                PlayerState.Paused => PlaybackStatus.Paused,
                _ => PlaybackStatus.Stopped
            }
        };
        await repository.SaveAsync(state, cancellationToken);
        _lastSavedAt = DateTimeOffset.UtcNow;
    }

    private void PublishSelectedTrack()
    {
        // Queue metadata is available before persistence, source discovery, or decoder startup.
        // Detach the previous player so UI reads cannot mix its timeline with the new track
        // or wait on the decoder while it opens a network stream.
        var track = queue.CurrentItem?.Track;
        var snapshot = new PlaybackSessionSnapshot(
            PlayerState.Stopped,
            track,
            new PlaybackTimelineInfo(_restoredItemId == queue.CurrentItem?.Id ? _restoredPosition : TimeSpan.Zero, track?.Duration),
            queue.Mode,
            queue.CurrentIndex,
            queue.Items.Count,
            null,
            queue.Request.RequiredSourceProviderId,
            _restoredStatus);
        SetSnapshot(snapshot, useActivePlayer: false);
        Raise(StateChanged, new PlaybackSessionChangedEventArgs(snapshot));
    }

    private void SetSnapshot(PlaybackSessionSnapshot snapshot, bool useActivePlayer = true)
    {
        lock (_snapshotGate)
        {
            _snapshot = snapshot;
            _snapshotPlayer = useActivePlayer ? resolver.ActivePlayer?.Player : null;
            _snapshotPlayerTrackId = _snapshotPlayer?.CurrentTrack?.Id;
        }
    }

    private static bool HasStateChanged(
        PlaybackSessionSnapshot previous,
        PlaybackSessionSnapshot current) =>
        previous.State != current.State ||
        previous.CurrentTrack?.Id != current.CurrentTrack?.Id ||
        previous.Mode != current.Mode ||
        previous.CurrentIndex != current.CurrentIndex ||
        previous.QueueCount != current.QueueCount ||
        previous.PlayerId != current.PlayerId ||
        previous.RequiredSourceProviderId != current.RequiredSourceProviderId;

    private void Raise<TEventArgs>(EventHandler<TEventArgs>? handler, TEventArgs arguments)
        where TEventArgs : EventArgs
    {
        if (handler is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> subscriber in handler.GetInvocationList())
        {
            try
            {
                subscriber(this, arguments);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "A playback session event subscriber failed.");
            }
        }
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("PlaybackSessionService has not been initialized.");
        }
    }

    private async Task StopAfterFailureAsync()
    {
        try
        {
            await resolver.StopAsync();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not stop the active player after a playback failure.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _monitorCancellation.CancelAsync();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await _gate.WaitAsync();
        try
        {
            if (_initialized)
            {
                await SaveSessionStateAsync();
                await resolver.StopAsync();
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _monitorCancellation.Dispose();
        }
    }
}
