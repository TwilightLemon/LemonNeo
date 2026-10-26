using LemonNeo.Abstractions.Playback;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application.Playback;

public sealed class PlaybackInteractionService : IPlaybackInteraction, IDisposable
{
    private readonly ILogger<PlaybackInteractionService> _logger;
    private readonly PlaybackSessionService _session;
    private bool _disposed;

    public PlaybackInteractionService(PlaybackSessionService session, ILogger<PlaybackInteractionService> logger)
    {
        _session = session;
        _logger = logger;
        session.MetadataChanged += OnMetadata;
        session.TimelineChanged += OnTimeline;
        session.StateChanged += OnState;
        session.QueueChanged += OnQueue;
        session.VolumeChanged += OnVolume;
    }

    public event EventHandler<InteractivePlaybackChangedEventArgs>? MetadataChanged;
    public event EventHandler<InteractivePlaybackChangedEventArgs>? TimelineChanged;
    public event EventHandler<InteractivePlaybackChangedEventArgs>? StateChanged;
    public event EventHandler<InteractivePlaybackChangedEventArgs>? QueueChanged;
    public event EventHandler<InteractivePlaybackChangedEventArgs>? VolumeChanged;
    public InteractivePlaybackSnapshot Snapshot => Convert(_session.GetCurrentSnapshot());

    public Task ExecuteAsync(PlaybackCommand command, TimeSpan? position = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Snapshot;
        var completion = command switch
        {
            PlaybackCommand.Play when snapshot.State != PlaybackState.Playing => _session.ResumeAsync(cancellationToken),
            PlaybackCommand.Play => Task.CompletedTask,
            PlaybackCommand.Pause => _session.PauseAsync(cancellationToken),
            PlaybackCommand.TogglePlayPause => snapshot.State == PlaybackState.Playing
                ? _session.PauseAsync(cancellationToken) : _session.ResumeAsync(cancellationToken),
            PlaybackCommand.Stop => _session.StopAsync(cancellationToken),
            PlaybackCommand.Next when snapshot.Controls.HasFlag(PlaybackControls.Next) => _session.NextAsync(cancellationToken),
            PlaybackCommand.Previous when snapshot.Controls.HasFlag(PlaybackControls.Previous) => _session.PreviousAsync(cancellationToken),
            PlaybackCommand.Next or PlaybackCommand.Previous => Task.CompletedTask,
            PlaybackCommand.Seek => _session.SeekAsync(position ?? throw new ArgumentException("Seek requires a position.", nameof(position)), cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        _ = ObserveAsync(completion);
        return Task.CompletedTask;
    }

    private async Task ObserveAsync(Task completion)
    {
        try { await completion; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _logger.LogWarning(exception, "A media control command failed."); }
    }

    private InteractivePlaybackSnapshot Convert(PlaybackSessionSnapshot snapshot)
    {
        var controls = PlaybackControls.None;
        if (snapshot.CurrentTrack is not null)
        {
            if (snapshot.State != PlaybackState.Loading) controls |= PlaybackControls.Play;
            if (snapshot.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Loading)
                controls |= PlaybackControls.Stop;
            if (snapshot.State == PlaybackState.Playing) controls |= PlaybackControls.Pause;
            if (snapshot.State is PlaybackState.Playing or PlaybackState.Paused) controls |= PlaybackControls.Seek;
            if (snapshot.QueueCount > 0 && (snapshot.Mode != PlaybackMode.Sequential ||
                                            snapshot.CurrentIndex < snapshot.QueueCount - 1)) controls |= PlaybackControls.Next;
            var canGoBack = snapshot.Mode switch
            {
                PlaybackMode.RepeatAll => true,
                PlaybackMode.Shuffle => _session.Queue.Snapshot().ShuffleCursor > 0,
                _ => snapshot.CurrentIndex > 0
            };
            if (snapshot.QueueCount > 0 && canGoBack) controls |= PlaybackControls.Previous;
        }
        var duration = snapshot.Timeline.Duration is { Ticks: >= 0 } end ? end : (TimeSpan?)null;
        var position = snapshot.Timeline.Position < TimeSpan.Zero ? TimeSpan.Zero : snapshot.Timeline.Position;
        if (duration is { } maximum && position > maximum) position = maximum;
        return new InteractivePlaybackSnapshot(snapshot.State, snapshot.CurrentTrack, position, duration, controls,
            snapshot.RequestId, snapshot.Revision, snapshot.Volume);
    }

    private void OnMetadata(object? sender, PlaybackSessionChangedEventArgs args) => Publish(MetadataChanged, args);
    private void OnTimeline(object? sender, PlaybackSessionChangedEventArgs args) => Publish(TimelineChanged, args);
    private void OnState(object? sender, PlaybackSessionChangedEventArgs args) => Publish(StateChanged, args);
    private void OnQueue(object? sender, PlaybackSessionChangedEventArgs args) => Publish(QueueChanged, args);
    private void OnVolume(object? sender, PlaybackSessionChangedEventArgs args) => Publish(VolumeChanged, args);

    private void Publish(EventHandler<InteractivePlaybackChangedEventArgs>? handlers, PlaybackSessionChangedEventArgs args)
    {
        if (_disposed || handlers is null) return;
        var update = new InteractivePlaybackChangedEventArgs(Convert(args.Snapshot));
        foreach (EventHandler<InteractivePlaybackChangedEventArgs> handler in handlers.GetInvocationList())
            try { handler(this, update); }
            catch (Exception exception) { _logger.LogWarning(exception, "An interactive plugin update failed."); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.MetadataChanged -= OnMetadata;
        _session.TimelineChanged -= OnTimeline;
        _session.StateChanged -= OnState;
        _session.QueueChanged -= OnQueue;
        _session.VolumeChanged -= OnVolume;
    }
}
