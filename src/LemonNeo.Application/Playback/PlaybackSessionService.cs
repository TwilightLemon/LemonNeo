using System.Threading.Channels;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application.Playback;

public sealed record PlaybackTimelineInfo(TimeSpan Position, TimeSpan? Duration)
{
    public double? Progress => Duration is { Ticks: > 0 }
        ? Math.Clamp(Position.TotalMilliseconds / Duration.Value.TotalMilliseconds, 0, 1) : null;
}

public sealed record PlaybackSessionSnapshot(
    PlaybackState State,
    Track? CurrentTrack,
    PlaybackTimelineInfo Timeline,
    PlaybackMode Mode,
    int CurrentIndex,
    int QueueCount,
    string? PlayerId,
    string? RequiredSourceProviderId,
    PlaybackState? RestoredStatus = null,
    AudioQuality? Quality = null,
    PlaybackSourceDetails? SourceDetails = null,
    AudioStreamInfo? AudioInfo = null,
    long RequestId = 0,
    Guid? CurrentQueueItemId = null,
    long Revision = 0,
    double Volume = 1,
    string? Error = null)
{
    public IReadOnlyList<PlaybackQueueItem> QueueItems { get; init; } = [];
}

public sealed class PlaybackSessionChangedEventArgs(PlaybackSessionSnapshot snapshot) : EventArgs
{
    public PlaybackSessionSnapshot Snapshot { get; } = snapshot;
}

/// <summary>One authority for selection, request replacement, player activation and notifications.</summary>
public sealed class PlaybackSessionService : IAsyncDisposable
{
    private readonly PlaybackResolver _resolver;
    private readonly IPlaybackQueue _queue;
    private readonly ISettingsScope _settings;
    private readonly ILogger<PlaybackSessionService> _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<(PlaybackSessionSnapshot Previous, PlaybackSessionSnapshot Current)> _notifications =
        Channel.CreateUnbounded<(PlaybackSessionSnapshot, PlaybackSessionSnapshot)>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly List<PlayOperation> _operations = [];
    private readonly List<Task> _cleanups = [];
    private readonly Task _notificationTask;
    private Task? _monitorTask;
    private PlayOperation? _operation;
    private PreparedPlayback? _active;
    private long _requestId;
    private bool _initialized;
    private bool _stopping;
    private bool _completionHandled;
    private Task? _shutdown;
    private Guid? _restoredItemId;
    private TimeSpan _restoredPosition;
    private PlaybackState? _restoredStatus;
    private PlaybackSessionSnapshot _snapshot = new(PlaybackState.Stopped, null,
        new PlaybackTimelineInfo(TimeSpan.Zero, null), PlaybackMode.Sequential, -1, 0, null, null);

    public PlaybackSessionService(PlaybackResolver resolver, IPlaybackQueue queue, IAppSettingsService settings,
        ILogger<PlaybackSessionService> logger)
    {
        _resolver = resolver;
        _queue = queue;
        _settings = settings.ForPackage(PackageNames.Application);
        _logger = logger;
        _notificationTask = Task.Run(NotifyAsync);
    }

    public IPlaybackQueue Queue => _queue;
    public PlaybackSessionSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public double Volume => Snapshot.Volume;
    public bool CanAdjustVolume => _resolver.CanAdjustVolume;
    public bool CanSeek => Snapshot.State is PlaybackState.Playing or PlaybackState.Paused;
    public bool CanReadFftData => FftBinCount > 1;
    public int FftBinCount { get { lock (_gate) return (_active?.Player.Player as IFftPlayer)?.FftBinCount ?? 0; } }

    public bool TryGetFftData(Span<float> destination)
    {
        lock (_gate)
            return _active?.Player.Player is IFftPlayer player && player.TryGetFftData(destination);
    }

    public event EventHandler<PlaybackSessionChangedEventArgs>? MetadataChanged;
    public event EventHandler<PlaybackSessionChangedEventArgs>? TimelineChanged;
    public event EventHandler<PlaybackSessionChangedEventArgs>? StateChanged;
    public event EventHandler<PlaybackSessionChangedEventArgs>? VolumeChanged;
    public event EventHandler<PlaybackSessionChangedEventArgs>? QueueChanged;

    public PlaybackSessionSnapshot GetCurrentSnapshot()
    {
        PlaybackSessionSnapshot snapshot;
        PreparedPlayback? active;
        lock (_gate) { snapshot = _snapshot; active = _active; }
        if (active is null || snapshot.State is not (PlaybackState.Playing or PlaybackState.Paused)) return snapshot;
        var player = active.Player.Player;
        var timeline = player.Timeline;
        var info = (player as IAudioInfoPlayer)?.AudioInfo;
        lock (_gate)
        {
            if (!ReferenceEquals(active, _active) || snapshot.Revision != _snapshot.Revision) return _snapshot;
            return snapshot with
            {
                State = ToSessionState(player.State),
                Timeline = new PlaybackTimelineInfo(timeline.Position, timeline.Duration ?? snapshot.Timeline.Duration),
                AudioInfo = info,
                Quality = AudioQualities.Resolve(info, active.SourceDetails.Source.Quality)
            };
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _settings.RegisterAsync(nameof(PlaybackQueueState), new PlaybackQueueState(),
            cancellationToken: cancellationToken);
        await _settings.RegisterAsync(nameof(PlaybackResumeState), new PlaybackResumeState(), value => value with
        {
            Position = value.Position < TimeSpan.Zero ? TimeSpan.Zero : value.Position,
            Status = NormalizeResumeStatus(value.Status)
        }, cancellationToken);
        await _settings.RegisterAsync(nameof(PlaybackPreferences), new PlaybackPreferences(), value => value with
        {
            Volume = double.IsFinite(value.Volume) ? Math.Clamp(value.Volume, 0, 1) : 1,
            Quality = Enum.IsDefined(value.Quality) ? value.Quality : AudioQuality.Automatic,
            PreferredPlayerId = string.IsNullOrWhiteSpace(value.PreferredPlayerId) ? null : value.PreferredPlayerId.Trim()
        }, cancellationToken);
        lock (_gate)
        {
            if (_initialized) return;
            ObjectDisposedException.ThrowIf(_stopping, this);
            _queue.Restore(_settings.Get<PlaybackQueueState>());
            var resume = _settings.Get<PlaybackResumeState>();
            if (_queue.CurrentItem is { } item && item.Id == resume.ItemId)
            {
                _restoredItemId = item.Id;
                _restoredPosition = item.Track.Duration is { } duration && resume.Position > duration
                    ? duration : resume.Position;
                _restoredStatus = resume.Status;
            }
            _initialized = true;
            PublishLocked(WithQueue(_snapshot with
            {
                Volume = _settings.Get<PlaybackPreferences>().Volume,
                RestoredStatus = _restoredStatus,
                Timeline = new PlaybackTimelineInfo(_restoredPosition, _queue.CurrentItem?.Track.Duration)
            }));
            _monitorTask = Task.Run(MonitorAsync);
        }
    }

    public Task PlayTrackAsync(Track track, PlaybackRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_gate)
        {
            EnsureReady();
            cancellationToken.ThrowIfCancellationRequested();
            _queue.Replace([track]);
            _queue.SetRequest(request ?? new PlaybackRequest(UseQualityPreference: true));
            return BeginLocked([_queue.CurrentItem!], PlayOrigin.User, cancellationToken);
        }
    }

    public Task PlayPlaylistAsync(IEnumerable<Track> tracks, int startIndex = 0, PlaybackRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var values = tracks.ToArray();
        if (values.Length == 0) throw new InvalidOperationException("Cannot play an empty playlist.");
        lock (_gate)
        {
            EnsureReady();
            cancellationToken.ThrowIfCancellationRequested();
            _queue.Replace(values, startIndex);
            _queue.SetRequest(request ?? new PlaybackRequest(UseQualityPreference: true));
            return BeginLocked([_queue.CurrentItem!], PlayOrigin.User, cancellationToken);
        }
    }

    public Task<Track?> PlayCurrentAsync(CancellationToken cancellationToken = default) =>
        PlayCurrentCore(null, cancellationToken);

    public Task<Track?> PlayQueueItemAsync(Guid itemId, CancellationToken cancellationToken = default) =>
        PlayCurrentCore(itemId, cancellationToken);

    private Task<Track?> PlayCurrentCore(Guid? itemId, CancellationToken token)
    {
        lock (_gate)
        {
            EnsureReady();
            token.ThrowIfCancellationRequested();
            if (itemId is { } id && _queue.Select(id) is null)
                throw new ArgumentException($"Queue item '{id}' was not found.", nameof(itemId));
            return _queue.CurrentItem is { } item
                ? BeginLocked([item], PlayOrigin.User, token) : Task.FromResult<Track?>(null);
        }
    }

    public Task<Track?> NextAsync(CancellationToken cancellationToken = default) => Move(true, cancellationToken);
    public Task<Track?> PreviousAsync(CancellationToken cancellationToken = default) => Move(false, cancellationToken);

    private Task<Track?> Move(bool next, CancellationToken token)
    {
        lock (_gate)
        {
            EnsureReady();
            token.ThrowIfCancellationRequested();
            var item = next ? _queue.MoveNext() : _queue.MovePrevious();
            if (item is not null) return BeginLocked([item], PlayOrigin.User, token);
            return next ? StopAtEndAsync(StopLocked(false, token)) : Task.FromResult<Track?>(null);
        }
    }

    private static async Task<Track?> StopAtEndAsync(Task stop) { await stop; return null; }

    private Task<Track?> BeginLocked(IReadOnlyList<PlaybackQueueItem> candidates, PlayOrigin origin,
        CancellationToken token)
    {
        _operation?.Cancel();
        for (var i = _operations.Count - 1; i >= 0; i--)
            if (_operations[i].Completion.IsCompleted)
            {
                _operations[i].Dispose();
                _operations.RemoveAt(i);
            }
        var previous = _active;
        _active = null;
        var first = _queue.Select(candidates[0].Id)!;
        var resume = _restoredItemId == first.Id ? _restoredPosition : TimeSpan.Zero;
        if (_restoredItemId != first.Id) ResetRestoredState();
        var operation = new PlayOperation(++_requestId, origin, EffectiveRequest(), resume,
            first.Id, CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token));
        _operation = operation;
        _operations.Add(operation);
        _completionHandled = false;
        SelectLoadingLocked(operation);
        operation.Completion = Task.Run(() => RunPlaybackAsync(operation, candidates, previous));
        // The session owns failures even when an adapter only submits a request.
        _ = operation.Completion.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return operation.Completion;
    }

    private async Task<Track?> RunPlaybackAsync(PlayOperation operation,
        IReadOnlyList<PlaybackQueueItem> candidates, PreparedPlayback? previous)
    {
        DisposePlayback(previous);
        var skipped = 0;
        foreach (var candidate in candidates)
        {
            PreparedPlayback? prepared = null;
            Track original;
            try
            {
                lock (_gate)
                {
                    if (!Owns(operation)) return null;
                    var item = _queue.Select(candidate.Id);
                    if (item is null) continue;
                    operation.ItemId = item.Id;
                    original = item.Track;
                    SelectLoadingLocked(operation);
                }
                prepared = await _resolver.PrepareAsync(original, operation.Request, operation.Token);
                lock (_gate)
                {
                    if (!Owns(operation)) return null;
                    prepared.Activate(_snapshot.Volume);
                    _active = prepared;
                }
                if (operation.ResumePosition > TimeSpan.Zero && skipped == 0)
                    await prepared.Player.Player.SeekAsync(operation.ResumePosition, operation.Token);
                lock (_gate)
                {
                    if (!Owns(operation)) return null;
                    var current = _queue.CurrentItem!;
                    ResetRestoredState();
                    var ids = MusicCacheIdentity.GetIdentifiers(current.Track.ExternalIds.Concat(prepared.Track.ExternalIds));
                    if (!current.Track.ExternalIds.SequenceEqual(ids))
                        _queue.UpdateTrack(current.Id, current.Track with { ExternalIds = ids });
                    PublishLocked(WithQueue(_snapshot with
                    {
                        State = PlaybackState.Playing, Error = null,
                        PlayerId = prepared.Player.Id, SourceDetails = prepared.SourceDetails,
                        Quality = prepared.SourceDetails.Source.Quality,
                        AudioInfo = (prepared.Player.Player as IAudioInfoPlayer)?.AudioInfo,
                        Timeline = new PlaybackTimelineInfo(prepared.Player.Player.Timeline.Position,
                            prepared.Player.Player.Timeline.Duration ?? original.Duration)
                    }));
                    UpdateSettingsLocked();
                }
                var updated = prepared.Track;
                prepared = null; // The session now owns the activated stream.
                try { await _resolver.SaveDiscoveredIdsAsync(original, updated, _lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                catch (Exception exception) { _logger.LogWarning(exception, "Could not save discovered music IDs."); }
                return updated;
            }
            catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
            {
                lock (_gate)
                    if (ReferenceEquals(_operation, operation))
                    {
                        _active = null;
                        PublishLocked(_snapshot with { State = PlaybackState.Stopped, Error = null });
                    }
                return null;
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    if (!Owns(operation)) return null;
                    _active = null;
                }
                if (operation.Origin == PlayOrigin.AutoAdvance && exception is PlaybackSourceUnavailableException)
                {
                    skipped++;
                    _logger.LogInformation("Skipping unavailable track {TrackId}: {Error}", candidate.Track.Id,
                        exception.Message);
                    continue;
                }
                lock (_gate)
                    if (Owns(operation)) PublishLocked(_snapshot with { State = PlaybackState.Failed, Error = exception.Message });
                if (operation.Origin == PlayOrigin.User) throw;
                _logger.LogWarning(exception, "Automatic playback failed.");
                return null;
            }
            finally { DisposePlayback(prepared); }
        }
        lock (_gate)
            if (Owns(operation))
                PublishLocked(_snapshot with { State = PlaybackState.Failed,
                    Error = $"No playable tracks remain ({skipped} unavailable)." });
        return null;
    }

    private void SelectLoadingLocked(PlayOperation operation)
    {
        PublishLocked(WithQueue(_snapshot with
        {
            RequestId = operation.Id, State = PlaybackState.Loading, Error = null, RestoredStatus = null,
            PlayerId = null, SourceDetails = null, Quality = null, AudioInfo = null,
            Timeline = new PlaybackTimelineInfo(_restoredItemId == _queue.CurrentItem?.Id ? _restoredPosition : TimeSpan.Zero,
                _queue.CurrentItem?.Track.Duration)
        }));
        UpdateSettingsLocked();
    }

    private bool Owns(PlayOperation operation) =>
        ReferenceEquals(_operation, operation) && operation.ItemId == _queue.CurrentItem?.Id &&
        !operation.Token.IsCancellationRequested && !_stopping;

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        ChangePlayerAsync((player, token) => player.PauseAsync(token), cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureReady();
            if (_snapshot.State == PlaybackState.Loading) return Task.CompletedTask;
            if (_active is null) return PlayCurrentCore(null, cancellationToken);
        }
        return ChangePlayerAsync((player, token) => player.ResumeAsync(token), cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureReady();
            return StopLocked(false, cancellationToken);
        }
    }

    private Task StopLocked(bool clearQueue, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _operation?.Cancel();
        _operation = null;
        var previous = _active;
        _active = null;
        if (clearQueue) _queue.Clear();
        ResetRestoredState();
        PublishLocked(WithQueue(_snapshot with
        {
            State = PlaybackState.Stopped, Error = null, SourceDetails = null, PlayerId = null,
            Quality = null, AudioInfo = null, RestoredStatus = null,
            Timeline = new PlaybackTimelineInfo(TimeSpan.Zero, _queue.CurrentItem?.Track.Duration)
        }));
        UpdateSettingsLocked();
        _cleanups.RemoveAll(task => task.IsCompleted);
        if (previous is null) return Task.CompletedTask;
        var cleanup = Task.Run(() => DisposePlayback(previous));
        _cleanups.Add(cleanup);
        return cleanup;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));
        return ChangePlayerAsync((player, token) => player.SeekAsync(position, token), cancellationToken);
    }

    private async Task ChangePlayerAsync(Func<IPlayer, CancellationToken, Task> change, CancellationToken token)
    {
        PreparedPlayback? active;
        PlayOperation? operation;
        lock (_gate) { EnsureReady(); active = _active; operation = _operation; }
        if (active is null || operation is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, operation.Token);
        try { await change(active.Player.Player, linked.Token); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            lock (_gate)
                if (Owns(operation)) PublishLocked(_snapshot with { Error = exception.Message });
            throw;
        }
        lock (_gate)
        {
            if (!Owns(operation) || !ReferenceEquals(active, _active)) return;
            _completionHandled = false;
            PublishLocked(SampleLocked());
            UpdateSettingsLocked();
        }
    }

    public async Task<double> GetVolumeAsync(CancellationToken cancellationToken = default)
    {
        PreparedPlayback? active;
        lock (_gate) { EnsureReady(); active = _active; }
        if (active is null) return Volume;
        var volume = await active.Player.Player.GetVolumeAsync(cancellationToken);
        lock (_gate)
            if (ReferenceEquals(active, _active)) PublishLocked(_snapshot with { Volume = volume });
        return Volume;
    }

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(volume));
        PreparedPlayback? active;
        lock (_gate)
        {
            EnsureReady();
            cancellationToken.ThrowIfCancellationRequested();
            PublishLocked(_snapshot with { Volume = volume });
            UpdateSettingsLocked();
            active = _active;
        }
        if (active is not null) await active.Player.Player.SetVolumeAsync(volume, cancellationToken);
    }

    public Task SetModeAsync(PlaybackMode mode, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureReady(); cancellationToken.ThrowIfCancellationRequested();
            _queue.SetMode(mode);
            PublishLocked(WithQueue(_snapshot)); UpdateSettingsLocked();
        }
        return Task.CompletedTask;
    }

    public Task ClearQueueAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { EnsureReady(); return StopLocked(true, cancellationToken); }
    }

    public Task CaptureStateAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { EnsureReady(); cancellationToken.ThrowIfCancellationRequested(); UpdateSettingsLocked(); }
        return Task.CompletedTask;
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                try
                {
                    lock (_gate)
                    {
                        if (_stopping) return;
                        if (_snapshot.CurrentQueueItemId != _queue.CurrentItem?.Id ||
                            _operation is { Token.IsCancellationRequested: true } &&
                            _snapshot.State is PlaybackState.Loading or PlaybackState.Playing or PlaybackState.Paused)
                        {
                            _ = StopLocked(false, CancellationToken.None);
                            continue;
                        }
                        var timeline = _active?.Player.Player.Timeline;
                        if (_snapshot.State == PlaybackState.Playing && timeline is { HasEnded: true } && !_completionHandled)
                        {
                            _completionHandled = true;
                            var candidates = _queue.GetNextCandidates();
                            if (candidates.Count > 0) _ = BeginLocked(candidates, PlayOrigin.AutoAdvance, _lifetime.Token);
                            else _ = StopAsync();
                        }
                        else PublishLocked(WithQueue(SampleLocked()));
                        UpdateSettingsLocked();
                    }
                }
                catch (Exception exception) { _logger.LogWarning(exception, "Could not refresh playback."); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private PlaybackSessionSnapshot SampleLocked()
    {
        if (_active is null || _snapshot.State == PlaybackState.Loading) return _snapshot;
        var player = _active.Player.Player;
        var timeline = player.Timeline;
        var info = (player as IAudioInfoPlayer)?.AudioInfo;
        return _snapshot with
        {
            State = ToSessionState(player.State), AudioInfo = info,
            Quality = AudioQualities.Resolve(info, _active.SourceDetails.Source.Quality),
            Timeline = new PlaybackTimelineInfo(timeline.Position, timeline.Duration ?? _snapshot.CurrentTrack?.Duration)
        };
    }

    private PlaybackSessionSnapshot WithQueue(PlaybackSessionSnapshot snapshot)
    {
        var state = _queue.Snapshot();
        var item = state.Items.FirstOrDefault(item => item.Id == state.CurrentItemId);
        return snapshot with
        {
            CurrentTrack = item?.Track, CurrentQueueItemId = item?.Id,
            CurrentIndex = item is null ? -1 : state.Items.IndexOf(item), QueueCount = state.Items.Length,
            QueueItems = state.Items, Mode = state.Mode, RequiredSourceProviderId = state.Request.RequiredSourceProviderId
        };
    }

    private void PublishLocked(PlaybackSessionSnapshot snapshot)
    {
        var previous = _snapshot;
        if (snapshot == previous && previous.QueueItems.SequenceEqual(snapshot.QueueItems)) return;
        // Queue arrays are snapshots; compare their contents rather than their allocation identity.
        if (snapshot.QueueItems.SequenceEqual(previous.QueueItems)) snapshot = snapshot with { QueueItems = previous.QueueItems };
        if (snapshot == previous) return;
        _snapshot = snapshot with { Revision = previous.Revision + 1 };
        _notifications.Writer.TryWrite((previous, _snapshot));
    }

    private async Task NotifyAsync()
    {
        await foreach (var (previous, current) in _notifications.Reader.ReadAllAsync())
        {
            var args = new PlaybackSessionChangedEventArgs(current);
            if (previous.RequestId != current.RequestId || previous.CurrentTrack != current.CurrentTrack ||
                previous.CurrentQueueItemId != current.CurrentQueueItemId || previous.SourceDetails != current.SourceDetails ||
                previous.AudioInfo != current.AudioInfo || previous.Quality != current.Quality || previous.PlayerId != current.PlayerId)
                Raise(MetadataChanged, args);
            if (previous.Mode != current.Mode || previous.CurrentQueueItemId != current.CurrentQueueItemId ||
                !previous.QueueItems.SequenceEqual(current.QueueItems)) Raise(QueueChanged, args);
            if (previous.State != current.State || previous.Error != current.Error) Raise(StateChanged, args);
            if (previous.RequestId != current.RequestId || previous.Timeline != current.Timeline) Raise(TimelineChanged, args);
            if (previous.Volume != current.Volume) Raise(VolumeChanged, args);
        }
    }

    private void Raise(EventHandler<PlaybackSessionChangedEventArgs>? handlers, PlaybackSessionChangedEventArgs args)
    {
        if (handlers is null) return;
        foreach (EventHandler<PlaybackSessionChangedEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, args); }
            catch (Exception exception) { _logger.LogWarning(exception, "A playback subscriber failed."); }
    }

    private PlaybackRequest EffectiveRequest()
    {
        var request = _queue.Request;
        var preferences = _settings.Get<PlaybackPreferences>();
        return request.UseQualityPreference ? request with
        {
            Quality = preferences.Quality, PreferredPlayerId = preferences.PreferredPlayerId, UseQualityPreference = false
        } : request;
    }

    private void UpdateSettingsLocked()
    {
        if (!_initialized) return;
        var state = _queue.Snapshot();
        _settings.Apply<PlaybackQueueState>(current =>
            current.CurrentItemId == state.CurrentItemId && current.Mode == state.Mode &&
            current.ShuffleCursor == state.ShuffleCursor && current.Request == state.Request &&
            current.Items.SequenceEqual(state.Items) && current.ShuffleOrder.SequenceEqual(state.ShuffleOrder) ? current : state);
        var status = NormalizeResumeStatus(_snapshot.RestoredStatus ?? _snapshot.State);
        var resume = new PlaybackResumeState(_snapshot.CurrentQueueItemId, _snapshot.Timeline.Position, status);
        if (_restoredItemId == _snapshot.CurrentQueueItemId && _restoredStatus is { } restoredStatus)
            resume = new PlaybackResumeState(_restoredItemId, _restoredPosition, restoredStatus);
        _settings.Apply<PlaybackResumeState>(current => current == resume ? current : resume);
        _settings.Apply<PlaybackPreferences>(current => current.Volume == _snapshot.Volume ? current : current with { Volume = _snapshot.Volume });
    }

    private void ResetRestoredState() { _restoredItemId = null; _restoredPosition = TimeSpan.Zero; _restoredStatus = null; }
    private static PlaybackState NormalizeResumeStatus(PlaybackState state) =>
        state is PlaybackState.Playing or PlaybackState.Paused ? state : PlaybackState.Stopped;
    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (!_initialized) throw new InvalidOperationException("PlaybackSessionService has not been initialized.");
    }
    private static PlaybackState ToSessionState(PlayerState state) => state switch
    {
        PlayerState.Playing => PlaybackState.Playing, PlayerState.Paused => PlaybackState.Paused, _ => PlaybackState.Stopped
    };
    private void DisposePlayback(PreparedPlayback? playback)
    {
        try { playback?.Dispose(); }
        catch (Exception exception) { _logger.LogWarning(exception, "Could not release an audio stream."); }
    }

    public Task PrepareForShutdownAsync()
    {
        lock (_gate) return _shutdown ??= ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        PreparedPlayback? active;
        Task[] tasks;
        lock (_gate)
        {
            _stopping = true;
            if (_initialized) UpdateSettingsLocked();
            _operation?.Cancel();
            _operation = null;
            active = _active;
            _active = null;
            tasks = _operations.Select(operation => (Task)operation.Completion).Concat(_cleanups).ToArray();
        }
        await _lifetime.CancelAsync();
        DisposePlayback(active);
        if (_monitorTask is not null) await _monitorTask;
        try { await Task.WhenAll(tasks); }
        catch (Exception) { /* Request failures are already published and observed. */ }
        foreach (var operation in _operations) operation.Dispose();
        _notifications.Writer.TryComplete();
        await _notificationTask;
    }

    public async ValueTask DisposeAsync() { await PrepareForShutdownAsync(); _lifetime.Dispose(); }

    private enum PlayOrigin { User, AutoAdvance }
    private sealed class PlayOperation(long id, PlayOrigin origin, PlaybackRequest request, TimeSpan resumePosition, Guid itemId,
        CancellationTokenSource cancellation) : IDisposable
    {
        public long Id { get; } = id;
        public PlayOrigin Origin { get; } = origin;
        public PlaybackRequest Request { get; } = request;
        public TimeSpan ResumePosition { get; } = resumePosition;
        public Guid ItemId { get; set; } = itemId;
        public CancellationToken Token { get; } = cancellation.Token;
        public Task<Track?> Completion { get; set; } = Task.FromResult<Track?>(null);
        private Task _cancellation = Task.CompletedTask;
        public void Cancel() => _cancellation = cancellation.CancelAsync();
        public void Dispose()
        {
            if (_cancellation.IsCompleted) cancellation.Dispose();
            else _ = _cancellation.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
        }
    }
}
