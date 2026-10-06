using LemonNeo.Abstractions.Playback;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Playback;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using LemonNeo.Desktop.Infrastructure;

namespace LemonNeo.Desktop.ViewModels;

public sealed class PlaybackViewModel : PageViewModel
{
    private readonly PlaybackSessionService _session;
    private readonly IPlaybackInteraction _interaction;
    private readonly ArtworkService _artwork;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _artworkCancellation;
    private readonly List<Task> _artworkTasks = [];
    private Uri? _artworkUri;
    private Bitmap? _nowArtwork;
    private PlaybackSessionSnapshot _snapshot;
    private double _volumePercent;
    private bool _isUpdatingVolume;
    private bool _volumeUpdatePending;
    private Task _volumeUpdateTask = Task.CompletedTask;
    private Task _volumeReadTask = Task.CompletedTask;
    private bool _closing;
    public bool IsSeeking { get; set; }
    public PlaybackSessionSnapshot Snapshot => _snapshot;
    public event EventHandler? Changed;

    public PlaybackViewModel(DesktopContext context, ArtworkService artwork) : base(context, "Playback")
    {
        _session = _host.Get<PlaybackSessionService>();
        _interaction = _host.Get<IPlaybackInteraction>();
        _artwork = artwork;
        _snapshot = _session.Snapshot;
        _volumePercent = _session.Volume * 100;
        // The shared operation gate rejects duplicate input without dimming command buttons.
        const AsyncRelayCommandOptions commandOptions = AsyncRelayCommandOptions.AllowConcurrentExecutions;
        TogglePlaybackCommand = new(() => ExecutePlaybackAsync(ct => _interaction.ExecuteAsync(PlaybackCommand.TogglePlayPause, cancellationToken: ct),
            showBusy: _snapshot.State == PlayerState.Stopped), commandOptions);
        PreviousCommand = new(() => ExecutePlaybackAsync(ct => _interaction.ExecuteAsync(PlaybackCommand.Previous, cancellationToken: ct)), commandOptions);
        NextCommand = new(() => ExecutePlaybackAsync(ct => _interaction.ExecuteAsync(PlaybackCommand.Next, cancellationToken: ct)), commandOptions);
        StopCommand = new(() => ExecutePlaybackAsync(ct => _session.StopAsync(ct), showBusy: false), commandOptions);
        ClearQueueCommand = new(() => ExecutePlaybackAsync(ct => _session.ClearQueueAsync(ct), showBusy: false), commandOptions);
        CycleModeCommand = new(() => ExecutePlaybackAsync(ct => _session.SetModeAsync((PlaybackMode)(((int)_snapshot.Mode + 1) % 3), ct), showBusy: false), commandOptions);
        ModeCommand = new(mode => ExecutePlaybackAsync(ct => _session.SetModeAsync(Enum.Parse<PlaybackMode>(mode!), ct), showBusy: false), commandOptions);
        _session.StateChanged += OnPlaybackChanged;
        _session.TimelineChanged += OnPlaybackChanged;
        _session.VolumeChanged += OnVolumeChanged;
    }
    public AsyncRelayCommand TogglePlaybackCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ClearQueueCommand { get; }
    public AsyncRelayCommand CycleModeCommand { get; }
    public AsyncRelayCommand<string> ModeCommand { get; }
    // Playback execution is exclusive, but its progress must not disable the whole control bar.
    public override bool CanInteract => Context.IsInitialized && !_closing;
    private bool CanControlPlayback => CanInteract;
    public string NowTitle => _snapshot.CurrentTrack?.Title ?? _text["NoTrack"];
    public string NowArtist => _snapshot.CurrentTrack is { } track
        ? track.Artists.Count > 0 ? string.Join(" / ", track.Artists.Select(a => a.Name)) : _text["UnknownArtist"]
        : _text["NoTrackDescription"];
    public bool HasCurrentTrack => _snapshot.CurrentTrack is not null;
    public Bitmap? NowArtwork => _nowArtwork;
    public bool IsPlaying => _snapshot.State == PlayerState.Playing;
    public string PlayLabel => _text[IsPlaying ? "Pause" : "Play"];
    public string PlaybackLabel => _snapshot.RestoredStatus is not null ? _text["Restored"] : _text[_snapshot.State.ToString()];
    public string ModeLabel => _text[_snapshot.Mode.ToString()];
    public bool IsSequential => _snapshot.Mode == PlaybackMode.Sequential;
    public bool IsRepeatAll => _snapshot.Mode == PlaybackMode.RepeatAll;
    public bool IsShuffle => _snapshot.Mode == PlaybackMode.Shuffle;
    public bool CanPrevious => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Previous);
    public bool CanNext => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Next);
    public bool CanTogglePlayback => CanControlPlayback && HasCurrentTrack;
    public bool CanSeek => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Seek) && _snapshot.Timeline.Duration is { Ticks: > 0 };
    public bool CanAdjustVolume => CanInteract && _session.CanAdjustVolume;
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (!CanAdjustVolume || !double.IsFinite(value)) return;
            if (!SetDisplayedVolume(Math.Clamp(value, 0, 100))) return;
            _volumeUpdatePending = true;
            if (!_isUpdatingVolume) _volumeUpdateTask = ApplyVolumeAsync();
        }
    }
    public string VolumeLabel => $"{Math.Round(VolumePercent)}%";
    public bool IsMuted => VolumePercent <= 0;
    public double PositionSeconds => _snapshot.Timeline.Position.TotalSeconds;
    public double DurationSeconds => Math.Max(1, _snapshot.Timeline.Duration?.TotalSeconds ?? 1);
    public string PositionText => TrackRow.FormatTime(_snapshot.Timeline.Position);
    public string DurationText => _snapshot.Timeline.Duration is { } duration ? TrackRow.FormatTime(duration) : "—";
    public string TimeLabel => $"{PositionText} / {DurationText}";
    public string PlaybackDetails => _text.Format("PlaybackDetailsFormat", _snapshot.CurrentTrack?.Id.ToString() ?? "—",
        _snapshot.CurrentIndex + 1, _snapshot.QueueCount, _snapshot.PlayerId ?? "—", _snapshot.RequiredSourceProviderId ?? _text["Automatic"]);
    public Task SeekAsync(double seconds) => CanSeek
        ? ExecutePlaybackAsync(ct => _session.SeekAsync(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds)), ct), showBusy: false)
        : Task.CompletedTask;

    public void EndSeek()
    {
        IsSeeking = false;
        OnPropertyChanged(nameof(PositionSeconds));
    }

    public Task ExecutePlaybackAsync(Func<CancellationToken, Task> action, bool showBusy = true) => RunOperationAsync(async ct =>
    {
        try { await Task.Run(() => action(ct), ct); }
        finally { UpdateSnapshot(); }
    }, showBusy: showBusy, busyDelay: TimeSpan.FromMilliseconds(250));
    private void OnPlaybackChanged(object? sender, PlaybackSessionChangedEventArgs args) =>
        Dispatcher.UIThread.Post(() => { if (Context.IsInitialized && !_closing) UpdateSnapshot(); });
    public void UpdateSnapshot()
    {
        _snapshot = _session.GetCurrentSnapshot();
        if (!_isUpdatingVolume) SetDisplayedVolume(_session.Volume * 100);
        UpdateArtwork(); RaisePlaybackProperties(); Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshVolumeAsync()
    {
        if (!CanAdjustVolume) return Task.CompletedTask;
        if (_volumeReadTask.IsCompleted) _volumeReadTask = ReadVolumeAsync();
        return _volumeReadTask;
    }

    private async Task ReadVolumeAsync()
    {
        try
        {
            await Task.Run(() => _session.GetVolumeAsync(_lifetime.Token), _lifetime.Token);
            if (!_closing && !_isUpdatingVolume) SetDisplayedVolume(_session.Volume * 100);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { SetError("OperationFailed", [exception.Message]); }
    }

    private async Task ApplyVolumeAsync()
    {
        _isUpdatingVolume = true;
        SetError(null);
        try
        {
            // Coalesce slider input while an asynchronous decoder call is still processing it.
            // Accepted changes finish during shutdown without entering the page's busy state.
            while (_volumeUpdatePending)
            {
                _volumeUpdatePending = false;
                var volume = _volumePercent / 100;
                try { await Task.Run(() => _session.SetVolumeAsync(volume)); }
                catch (Exception exception) { SetError("OperationFailed", [exception.Message]); }
            }
        }
        finally
        {
            _isUpdatingVolume = false;
            SetDisplayedVolume(_session.Volume * 100);
        }
    }

    private bool SetDisplayedVolume(double percent)
    {
        if (!SetProperty(ref _volumePercent, percent, nameof(VolumePercent))) return false;
        OnPropertyChanged(nameof(VolumeLabel));
        OnPropertyChanged(nameof(IsMuted));
        return true;
    }

    private void OnVolumeChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (!_closing && !_isUpdatingVolume) SetDisplayedVolume(_session.Volume * 100);
    });
    private void UpdateArtwork()
    {
        var uri = _snapshot.CurrentTrack?.ArtworkUri;
        // Monitor ticks and play/pause changes must not download the same cover again.
        // Tracking the URI separately also loads covers from an already restored session.
        if (_closing || Equals(uri, _artworkUri)) return;
        _artworkUri = uri;
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        _artworkCancellation = null;
        SetArtwork(null);
        if (uri is null) return;
        _artworkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _artworkTasks.RemoveAll(t => t.IsCompleted);
        _artworkTasks.Add(LoadArtworkAsync(uri, _artworkCancellation.Token));
    }

    private async Task LoadArtworkAsync(Uri uri, CancellationToken cancellationToken)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await _artwork.LoadAsync(uri, cancellationToken);
            Color? color = null;
            if (bitmap is not null)
            {
                try { color = await Task.Run(() => ArtworkColorExtractor.Extract(bitmap, cancellationToken), cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { System.Diagnostics.Trace.WriteLine($"Artwork color extraction failed: {exception}"); }
            }
            if (_closing || cancellationToken.IsCancellationRequested || !Equals(uri, _artworkUri)) return;
            SetArtwork(bitmap, color);
            bitmap = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { bitmap?.Dispose(); }
    }

    private void SetArtwork(Bitmap? bitmap, Color? color = null)
    {
        var previous = _nowArtwork;
        if (SetProperty(ref _nowArtwork, bitmap, nameof(NowArtwork))) previous?.Dispose();
        Context.Appearance.SetArtworkColor(color);
    }

    private void RaisePlaybackProperties()
    {
        foreach (var name in new[] { nameof(NowTitle), nameof(NowArtist), nameof(HasCurrentTrack), nameof(IsPlaying), nameof(PlayLabel), nameof(PlaybackLabel), nameof(ModeLabel), nameof(IsSequential), nameof(IsRepeatAll), nameof(IsShuffle), nameof(CanPrevious), nameof(CanNext), nameof(CanTogglePlayback), nameof(CanSeek), nameof(CanAdjustVolume), nameof(DurationSeconds), nameof(PositionText), nameof(DurationText) }) OnPropertyChanged(name);
        if (!IsSeeking) OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(PlaybackDetails)); OnPropertyChanged(nameof(TimeLabel));
    }

    protected override void RaiseInteractionProperties() { base.RaiseInteractionProperties(); RaisePlaybackProperties(); }
    protected override void RefreshLocalizedProperties() { base.RefreshLocalizedProperties(); RaisePlaybackProperties(); }
    public override async ValueTask DisposeAsync()
    {
        _closing = true;
        _session.StateChanged -= OnPlaybackChanged; _session.TimelineChanged -= OnPlaybackChanged;
        _session.VolumeChanged -= OnVolumeChanged;
        await _lifetime.CancelAsync();
        await base.DisposeAsync();
        await Task.WhenAll(_volumeUpdateTask, _volumeReadTask);
        await Task.WhenAll(_artworkTasks);
        SetArtwork(null); _artworkCancellation?.Dispose(); _lifetime.Dispose();
    }
}
