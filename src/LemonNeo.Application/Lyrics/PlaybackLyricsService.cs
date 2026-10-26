using LemonNeo.Application.Playback;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application.Lyrics;

public enum PlaybackLyricsStatus { Empty, Loading, Loaded, Failed }

public sealed record PlaybackLyricsSnapshot(long RequestId, PlaybackLyricsStatus Status, LyricsLookupResult? Result = null)
{
    public Core.Lyrics.Lyrics? Document => Result?.Lyrics;
}

/// <summary>Loads once per successful playback request, independently of view lifetime.</summary>
public sealed class PlaybackLyricsService : IAsyncDisposable
{
    private readonly PlaybackSessionService _playback;
    private readonly LyricsService _lyrics;
    private readonly ILogger<PlaybackLyricsService>? _logger;
    private readonly object _gate = new();
    private readonly List<Load> _loads = [];
    private Load? _load;
    private bool _disposed;
    private PlaybackLyricsSnapshot _snapshot = new(0, PlaybackLyricsStatus.Empty);

    public PlaybackLyricsService(PlaybackSessionService playback, LyricsService lyrics,
        ILogger<PlaybackLyricsService>? logger = null)
    {
        _playback = playback;
        _lyrics = lyrics;
        _logger = logger;
        playback.MetadataChanged += OnPlaybackChanged;
        playback.StateChanged += OnPlaybackChanged;
        Refresh();
    }

    public PlaybackLyricsSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event EventHandler? Changed;
    private void OnPlaybackChanged(object? sender, PlaybackSessionChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        var playback = _playback.Snapshot;
        var changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            if (_snapshot.RequestId != playback.RequestId || playback.State is PlaybackState.Loading or PlaybackState.Failed or PlaybackState.Stopped)
            {
                _load?.Cancel();
                _load = null;
                var empty = new PlaybackLyricsSnapshot(playback.RequestId, PlaybackLyricsStatus.Empty);
                changed = _snapshot != empty;
                _snapshot = empty;
            }
            if (playback.State is PlaybackState.Playing or PlaybackState.Paused && playback.CurrentTrack is { } track &&
                _load is null && _snapshot.Status == PlaybackLyricsStatus.Empty)
            {
                for (var i = _loads.Count - 1; i >= 0; i--)
                    if (_loads[i].Task.IsCompleted) { _loads[i].Dispose(); _loads.RemoveAt(i); }
                var load = new Load(playback.RequestId);
                _load = load;
                _loads.Add(load);
                _snapshot = new PlaybackLyricsSnapshot(playback.RequestId, PlaybackLyricsStatus.Loading);
                load.Task = Task.Run(() => LoadAsync(load, track));
                changed = true;
            }
        }
        if (changed) RaiseChanged();
    }

    private async Task LoadAsync(Load load, Core.Music.Track track)
    {
        LyricsLookupResult? result = null;
        var status = PlaybackLyricsStatus.Loaded;
        try { result = await _lyrics.GetLyricsAsync(track, load.Cancellation.Token); }
        catch (OperationCanceledException) when (load.Cancellation.IsCancellationRequested) { return; }
        catch (Exception) { status = PlaybackLyricsStatus.Failed; }
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_load, load) || load.Cancellation.IsCancellationRequested ||
                _playback.Snapshot.RequestId != load.RequestId) return;
            _snapshot = new PlaybackLyricsSnapshot(load.RequestId, status, result);
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { _logger?.LogWarning(exception, "A lyrics subscriber failed."); }
    }

    public async ValueTask DisposeAsync()
    {
        Load[] loads;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _playback.MetadataChanged -= OnPlaybackChanged;
            _playback.StateChanged -= OnPlaybackChanged;
            loads = _loads.ToArray();
            foreach (var load in loads) load.Cancel();
        }
        await Task.WhenAll(loads.Select(load => load.Task));
        foreach (var load in loads) load.Dispose();
    }

    private sealed class Load(long requestId) : IDisposable
    {
        public long RequestId { get; } = requestId;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        private Task _cancellation = Task.CompletedTask;
        public void Cancel() => _cancellation = Cancellation.CancelAsync();
        public void Dispose()
        {
            if (_cancellation.IsCompleted) Cancellation.Dispose();
            else _ = _cancellation.ContinueWith(_ => Cancellation.Dispose(), TaskScheduler.Default);
        }
    }
}
