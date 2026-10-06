using LemonNeo.Abstractions.Playback;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application.Playback;

public sealed class PlaybackInteractionService : IPlaybackInteraction, IDisposable
{
    private readonly PlaybackSessionService _session;
    private readonly ILogger<PlaybackInteractionService> _logger;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private bool _disposed;

    public PlaybackInteractionService(PlaybackSessionService session, ILogger<PlaybackInteractionService> logger)
    {
        _session = session;
        _logger = logger;
        session.StateChanged += OnChanged;
        session.TimelineChanged += OnChanged;
    }

    public event EventHandler<InteractivePlaybackChangedEventArgs>? Changed;

    public InteractivePlaybackSnapshot Snapshot => Convert(_session.GetCurrentSnapshot());

    private InteractivePlaybackSnapshot Convert(PlaybackSessionSnapshot snapshot)
    {
        var controls = PlaybackControls.None;
        if (snapshot.CurrentTrack is not null)
        {
            controls = PlaybackControls.Play;
            if (snapshot.State != PlayerState.Stopped) controls |= PlaybackControls.Stop;
            if (snapshot.State == PlayerState.Playing) controls |= PlaybackControls.Pause;
            if (_session.CanSeek && snapshot.State != PlayerState.Stopped) controls |= PlaybackControls.Seek;
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
        return new(snapshot.State, snapshot.CurrentTrack, position, duration, controls);
    }

    private void OnChanged(object? sender, PlaybackSessionChangedEventArgs args)
    {
        if (_disposed || Changed is not { } handlers) return;
        // Read the latest session: state notifications can arrive after another
        // command has already advanced the queue.
        var update = new InteractivePlaybackChangedEventArgs(Snapshot);
        foreach (EventHandler<InteractivePlaybackChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, update); }
            catch (Exception exception) { _logger.LogWarning(exception, "An Interactive plugin update failed."); }
        }
    }

    public async Task ExecuteAsync(PlaybackCommand command, TimeSpan? position = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _commands.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            switch (command)
            {
                case PlaybackCommand.Play:
                    if (Snapshot.State != PlayerState.Playing) await _session.ResumeAsync(cancellationToken);
                    break;
                case PlaybackCommand.Pause:
                    await _session.PauseAsync(cancellationToken);
                    break;
                case PlaybackCommand.TogglePlayPause:
                    if (Snapshot.State == PlayerState.Playing) await _session.PauseAsync(cancellationToken);
                    else await _session.ResumeAsync(cancellationToken);
                    break;
                case PlaybackCommand.Stop:
                    await _session.StopAsync(cancellationToken);
                    break;
                case PlaybackCommand.Next:
                    if (Snapshot.Controls.HasFlag(PlaybackControls.Next)) await _session.NextAsync(cancellationToken);
                    break;
                case PlaybackCommand.Previous:
                    if (Snapshot.Controls.HasFlag(PlaybackControls.Previous)) await _session.PreviousAsync(cancellationToken);
                    break;
                case PlaybackCommand.Seek:
                    if (position is null) throw new ArgumentException("Seek requires a position.", nameof(position));
                    await _session.SeekAsync(position.Value, cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command));
            }
        }
        finally { _commands.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.StateChanged -= OnChanged;
        _session.TimelineChanged -= OnChanged;
        Changed = null;
        // A command may still be leaving the gate during host shutdown.
    }
}
