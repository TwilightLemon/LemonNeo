using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application.Playback;

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
    PlaybackStatus? RestoredStatus = null,
    AudioQuality? Quality = null,
    PlaybackSourceDetails? SourceDetails = null);

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
    IAppSettingsService settings,
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
    private readonly ISettingsScope _settings = settings.ForPackage(PackageNames.Application);
    private bool _preparedForShutdown;

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
            return snapshot with { State = PlayerState.Stopped, Quality = null, SourceDetails = null };

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
            ObjectDisposedException.ThrowIf(_disposed || _preparedForShutdown, this);
            if (_initialized)
            {
                return;
            }

            await _settings.RegisterAsync(nameof(PlaybackQueueState), new PlaybackQueueState(), cancellationToken: cancellationToken);
            await _settings.RegisterAsync(nameof(PlaybackResumeState), new PlaybackResumeState(), value => value with
            {
                Position = value.Position < TimeSpan.Zero ? TimeSpan.Zero : value.Position,
                Status = Enum.IsDefined(value.Status) ? value.Status : PlaybackStatus.Stopped
            }, cancellationToken);
            await _settings.RegisterAsync(nameof(PlaybackPreferences), new PlaybackPreferences(), value => value with
            {
                Volume = double.IsFinite(value.Volume) ? Math.Clamp(value.Volume, 0, 1) : 1,
                Quality = Enum.IsDefined(value.Quality) ? value.Quality : AudioQuality.Automatic
            }, cancellationToken);
            var saved = _settings.Get<PlaybackQueueState>();
            var resume = _settings.Get<PlaybackResumeState>();
            queue.Restore(saved);
            await resolver.SetVolumeAsync(_settings.Get<PlaybackPreferences>().Volume, cancellationToken);
            if (queue.CurrentItem is { } restoredItem && restoredItem.Id == resume.ItemId)
            {
                _restoredItemId = resume.ItemId;
                _restoredPosition = resume.Position < TimeSpan.Zero ? TimeSpan.Zero : resume.Position;
                if (restoredItem.Track.Duration is { } duration && _restoredPosition > duration)
                {
                    _restoredPosition = duration;
                }
                _restoredStatus = Enum.IsDefined(resume.Status) ? resume.Status : PlaybackStatus.Stopped;
            }
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
            queue.SetRequest(request ?? new PlaybackRequest(UseQualityPreference: true));
            PublishSelectedTrack();
            await resolver.PlayAsync(track, EffectiveRequest(), cancellationToken);
            UpdateSettings();
            _completionHandled = false;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        catch
        {
            await StopAfterFailureAsync();
            UpdateSettings();
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
            queue.SetRequest(request ?? new PlaybackRequest(UseQualityPreference: true));
            PublishSelectedTrack();
            await resolver.PlayAsync(queue.CurrentItem!.Track, EffectiveRequest(), cancellationToken);
            UpdateSettings();
            _completionHandled = false;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        catch
        {
            await StopAfterFailureAsync();
            UpdateSettings();
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
            await resolver.PlayAsync(item.Track, EffectiveRequest(), cancellationToken);
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
            UpdateSettings();
            _completionHandled = false;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        catch
        {
            await StopAfterFailureAsync();
            UpdateSettings();
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
            UpdateSettings(captureQueue: false);
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
            UpdateSettings(captureQueue: false);
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
            UpdateSettings();
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
            UpdateSettings();
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

    public async Task CaptureStateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureReady();
            UpdateSettings();
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
            }
            if (item is null && stopWhenUnavailable)
            {
                await resolver.StopAsync(cancellationToken);
            }
            else if (item is not null)
            {
                await resolver.PlayAsync(item.Track, EffectiveRequest(), cancellationToken);
            }

            UpdateSettings();

            _completionHandled = false;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
        }
        catch
        {
            await StopAfterFailureAsync();
            UpdateSettings();
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
            UpdateSettings(captureQueue: false);
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
                        await resolver.PlayAsync(next.Track, EffectiveRequest(), cancellationToken);
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

                UpdateSettings();
            }

            previous = Snapshot;
            snapshot = CaptureSnapshot();
            SetSnapshot(snapshot);
            UpdateSettings(captureQueue: false);
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
            _restoredStatus,
            resolver.CurrentSource?.Quality,
            resolver.CurrentSourceDetails);
    }

    private PlaybackRequest EffectiveRequest() => queue.Request.UseQualityPreference
        ? queue.Request with { Quality = _settings.Get<PlaybackPreferences>().Quality, UseQualityPreference = false }
        : queue.Request;

    private void ResetRestoredState()
    {
        _restoredItemId = null;
        _restoredPosition = TimeSpan.Zero;
        _restoredStatus = null;
    }

    private void UpdateSettings(bool captureQueue = true)
    {
        if (captureQueue && queue.CurrentItem is { } item && resolver.CurrentQueueTrack is { } resolved &&
            item.Track.Id == resolved.Id)
        {
            var identifiers = MusicCacheIdentity.GetIdentifiers(item.Track.ExternalIds.Concat(resolved.ExternalIds));
            if (!item.Track.ExternalIds.SequenceEqual(identifiers))
                queue.UpdateTrack(item.Id, item.Track with { ExternalIds = identifiers });
        }
        if (captureQueue) UpdateQueueSettings();
        var snapshot = CaptureSnapshot();
        var status = snapshot.RestoredStatus ?? snapshot.State switch
        {
            PlayerState.Playing => PlaybackStatus.Playing,
            PlayerState.Paused => PlaybackStatus.Paused,
            _ => PlaybackStatus.Stopped
        };
        _settings.Apply<PlaybackResumeState>(_ => new(queue.CurrentItem?.Id, snapshot.Timeline.Position, status));
        _settings.Apply<PlaybackPreferences>(current => current with { Volume = Volume });
    }

    private void UpdateQueueSettings()
    {
        var state = queue.Snapshot();
        _settings.Apply<PlaybackQueueState>(current =>
            current.CurrentItemId == state.CurrentItemId && current.Mode == state.Mode &&
            current.ShuffleCursor == state.ShuffleCursor && current.Request == state.Request &&
            current.Items.SequenceEqual(state.Items) && current.ShuffleOrder.SequenceEqual(state.ShuffleOrder)
                ? current : state);
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
        UpdateQueueSettings();
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
        previous.RequiredSourceProviderId != current.RequiredSourceProviderId ||
        previous.Quality != current.Quality ||
        previous.SourceDetails != current.SourceDetails;

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
        ObjectDisposedException.ThrowIf(_disposed || _preparedForShutdown, this);
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

    /// <summary>Stops state producers and captures the final state before plugins or the player close.</summary>
    public async Task PrepareForShutdownAsync()
    {
        if (_preparedForShutdown) return;
        await _monitorCancellation.CancelAsync();
        if (_monitorTask is not null) await _monitorTask;
        await _gate.WaitAsync();
        try
        {
            if (_preparedForShutdown) return;
            if (_initialized)
            {
                UpdateSettings();
                _preparedForShutdown = true;
                await resolver.StopAsync();
                SetSnapshot(Snapshot, useActivePlayer: false);
            }
            _preparedForShutdown = true;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await PrepareForShutdownAsync();
        _disposed = true;
        await _gate.WaitAsync();
        _gate.Release();
        _gate.Dispose();
        _monitorCancellation.Dispose();
    }
}
