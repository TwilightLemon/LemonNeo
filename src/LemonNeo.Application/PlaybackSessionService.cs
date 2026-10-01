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
    TrackReference? CurrentTrack,
    PlaybackTimelineInfo Timeline,
    PlaybackMode Mode,
    int CurrentIndex,
    int QueueCount,
    string? PlayerId,
    string? RequiredSourceProviderId);

public sealed class PlaybackSessionChangedEventArgs(PlaybackSessionSnapshot snapshot) : EventArgs
{
    public PlaybackSessionSnapshot Snapshot { get; } = snapshot;
}

public sealed class PlaybackCompletedEventArgs(
    TrackReference completedTrack,
    TrackReference? nextTrack) : EventArgs
{
    public TrackReference CompletedTrack { get; } = completedTrack;
    public TrackReference? NextTrack { get; } = nextTrack;
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
    private bool _completionHandled;
    private bool _initialized;
    private bool _disposed;

    public event EventHandler<PlaybackSessionChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackSessionChangedEventArgs>? TimelineChanged;
    public event EventHandler<PlaybackCompletedEventArgs>? PlaybackCompleted;

    public IPlaybackQueue Queue => queue;

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

            queue.Restore(await repository.LoadAsync(cancellationToken));
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
        TrackReference track,
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
            queue.SetRequest(request ?? new PlaybackRequest());
            await repository.SaveAsync(queue.Snapshot(), cancellationToken);
            await resolver.PlayAsync(track, queue.Request, cancellationToken);
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
        IEnumerable<TrackReference> tracks,
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
            queue.SetRequest(request ?? new PlaybackRequest());
            await repository.SaveAsync(queue.Snapshot(), cancellationToken);
            await resolver.PlayAsync(queue.CurrentItem!.Track, queue.Request, cancellationToken);
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

    public async Task<TrackReference?> NextAsync(CancellationToken cancellationToken = default) =>
        await MoveAsync(queue.MoveNext, stopWhenUnavailable: true, cancellationToken);

    public async Task<TrackReference?> PreviousAsync(CancellationToken cancellationToken = default) =>
        await MoveAsync(queue.MovePrevious, stopWhenUnavailable: false, cancellationToken);

    public async Task<TrackReference?> PlayCurrentAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        PlaybackQueueItem? item;
        try
        {
            EnsureReady();
            item = queue.CurrentItem;
            if (item is null)
            {
                return null;
            }

            await resolver.PlayAsync(item.Track, queue.Request, cancellationToken);
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
        await ChangePlayerStateAsync(resolver.ResumeAsync, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await ChangePlayerStateAsync(resolver.StopAsync, cancellationToken);
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
            await repository.SaveAsync(queue.Snapshot(), cancellationToken);
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
            await repository.SaveAsync(queue.Snapshot(), cancellationToken);
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
            await repository.SaveAsync(queue.Snapshot(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TrackReference?> MoveAsync(
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
            await repository.SaveAsync(queue.Snapshot(), cancellationToken);
            if (item is null && stopWhenUnavailable)
            {
                await resolver.StopAsync(cancellationToken);
            }
            else if (item is not null)
            {
                await resolver.PlayAsync(item.Track, queue.Request, cancellationToken);
            }

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
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        PlaybackSessionSnapshot snapshot;
        try
        {
            EnsureReady();
            await change(cancellationToken);
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
        TrackReference? completedTrack = null;
        TrackReference? nextTrack = null;

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
                nextTrack = next?.Track;
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

                await repository.SaveAsync(queue.Snapshot(), cancellationToken);
            }

            previous = Snapshot;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
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
            new PlaybackTimelineInfo(playerTimeline?.Position ?? TimeSpan.Zero, duration),
            queue.Mode,
            queue.CurrentIndex,
            queue.Items.Count,
            activePlayer?.Id,
            queue.Request.RequiredSourceProviderId);
    }

    private void SetSnapshot(PlaybackSessionSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _snapshot = snapshot;
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
                await repository.SaveAsync(queue.Snapshot());
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
