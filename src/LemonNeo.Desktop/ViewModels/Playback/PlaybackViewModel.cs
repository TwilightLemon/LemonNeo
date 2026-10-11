using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.Utils;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Playback;

public sealed class PlaybackViewModel : PageViewModel
{
    private readonly ArtworkService _artwork;
    private readonly List<Task> _artworkTasks = [];
    private readonly IPlaybackInteraction _interaction;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PlaybackSessionService _session;
    private CancellationTokenSource? _artworkCancellation;
    private Uri? _artworkUri;
    private bool _closing;
    private bool _isSourceDetailsOpen;
    private bool _isUpdatingVolume;
    private Bitmap? _nowArtwork;
    private double _volumePercent;
    private Task _volumeReadTask = Task.CompletedTask;
    private bool _volumeUpdatePending;
    private Task _volumeUpdateTask = Task.CompletedTask;

    public PlaybackViewModel(DesktopContext context, ArtworkService artwork) : base(context, "Playback")
    {
        _session = _host.Get<PlaybackSessionService>();
        _interaction = _host.Get<IPlaybackInteraction>();
        _artwork = artwork;
        Snapshot = _session.Snapshot;
        _volumePercent = _session.Volume * 100;
        // The shared operation gate rejects duplicate input without dimming command buttons.
        const AsyncRelayCommandOptions commandOptions = AsyncRelayCommandOptions.AllowConcurrentExecutions;
        TogglePlaybackCommand = new AsyncRelayCommand(() => ExecutePlaybackAsync(
            ct => _interaction.ExecuteAsync(PlaybackCommand.TogglePlayPause, cancellationToken: ct),
            Snapshot.State == PlayerState.Stopped), commandOptions);
        PreviousCommand =
            new AsyncRelayCommand(
                () => ExecutePlaybackAsync(ct =>
                    _interaction.ExecuteAsync(PlaybackCommand.Previous, cancellationToken: ct)), commandOptions);
        NextCommand =
            new AsyncRelayCommand(
                () => ExecutePlaybackAsync(ct =>
                    _interaction.ExecuteAsync(PlaybackCommand.Next, cancellationToken: ct)), commandOptions);
        StopCommand = new AsyncRelayCommand(() => ExecutePlaybackAsync(ct => _session.StopAsync(ct), false),
            commandOptions);
        CycleModeCommand =
            new AsyncRelayCommand(
                () => ExecutePlaybackAsync(
                    ct => _session.SetModeAsync((PlaybackMode)(((int)Snapshot.Mode + 1) % 3), ct), false),
                commandOptions);
        ModeCommand = new AsyncRelayCommand<string>(
            mode => ExecutePlaybackAsync(ct => _session.SetModeAsync(Enum.Parse<PlaybackMode>(mode!), ct), false),
            commandOptions);
        _session.StateChanged += OnPlaybackChanged;
        _session.TimelineChanged += OnPlaybackChanged;
        _session.VolumeChanged += OnVolumeChanged;
    }

    public bool IsSeeking { get; set; }
    public PlaybackSessionSnapshot Snapshot { get; private set; }

    public AsyncRelayCommand TogglePlaybackCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand CycleModeCommand { get; }

    public AsyncRelayCommand<string> ModeCommand { get; }

    // Playback execution is exclusive, but its progress must not disable the whole control bar.
    public override bool CanInteract => Context.IsInitialized && !_closing;
    private bool CanControlPlayback => CanInteract;
    public string NowTitle => Snapshot.CurrentTrack?.Title ?? _text[LanguageManager.Keys.NoTrack];

    public string NowArtist => Snapshot.CurrentTrack is { } track
        ? track.Artists.Count > 0 ? string.Join(" / ", track.Artists.Select(a => a.Name)) : _text[LanguageManager.Keys.UnknownArtist]
        : _text[LanguageManager.Keys.NoTrackDescription];

    public bool HasCurrentTrack => Snapshot.CurrentTrack is not null;
    public Bitmap? NowArtwork => _nowArtwork;
    public bool IsPlaying => Snapshot.State == PlayerState.Playing;
    public string PlayLabel => _text[IsPlaying ? LanguageManager.Keys.Pause : LanguageManager.Keys.Play];

    public string PlaybackLabel =>
        Snapshot.RestoredStatus is not null ? _text[LanguageManager.Keys.Restored] : _text[Snapshot.State.ToString()];

    public string ModeLabel => _text[Snapshot.Mode.ToString()];
    public bool IsSequential => Snapshot.Mode == PlaybackMode.Sequential;
    public bool IsRepeatAll => Snapshot.Mode == PlaybackMode.RepeatAll;
    public bool IsShuffle => Snapshot.Mode == PlaybackMode.Shuffle;
    public bool CanPrevious => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Previous);
    public bool CanNext => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Next);
    public bool CanTogglePlayback => CanControlPlayback && HasCurrentTrack;

    public bool CanSeek => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Seek) &&
                           Snapshot.Timeline.Duration is { Ticks: > 0 };

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
    public double PositionSeconds => Snapshot.Timeline.Position.TotalSeconds;
    public double DurationSeconds => Math.Max(1, Snapshot.Timeline.Duration?.TotalSeconds ?? 1);
    public string PositionText => TrackRow.FormatTime(Snapshot.Timeline.Position);
    public string DurationText => Snapshot.Timeline.Duration is { } duration ? TrackRow.FormatTime(duration) : "—";

    public string QualityLabel => HasCurrentTrack
        ? _text[Snapshot.Quality switch
        {
            AudioQuality.Low => "PlaybackQualityLow",
            AudioQuality.Medium => "PlaybackQualityMedium",
            AudioQuality.High => "PlaybackQualityHigh",
            _ => "AudioQuality" + (Snapshot.Quality?.ToString() ?? "Unknown")
        }]
        : "";

    public bool HasSourceDetails => Snapshot.SourceDetails is not null;

    public bool IsSourceDetailsOpen
    {
        get => _isSourceDetailsOpen;
        set => SetProperty(ref _isSourceDetailsOpen, value && HasSourceDetails);
    }

    public string SourceType => Snapshot.SourceDetails is { } details
        ? details.IsCached
            ? _text[LanguageManager.Keys.PlaybackSourceCached]
            : details.Source is HttpPlaybackSource
                ? _text[LanguageManager.Keys.PlaybackSourceOnline]
                : _text[LanguageManager.Keys.PlaybackSourceLocal]
        : _text[LanguageManager.Keys.PlaybackSourceUnknown];

    public string SourceLocation => Snapshot.SourceDetails?.Source switch
    {
        HttpPlaybackSource http => http.Uri.AbsoluteUri,
        FilePlaybackSource file => file.Path,
        _ => _text[LanguageManager.Keys.PlaybackSourceUnknown]
    };

    public string SourceProvider => Snapshot.SourceDetails?.ProviderId ?? _text[LanguageManager.Keys.PlaybackSourceUnknown];
    public string SourceNamespace => Snapshot.SourceDetails?.SourceId.Namespace ?? _text[LanguageManager.Keys.PlaybackSourceUnknown];
    public string SourceKind => Snapshot.SourceDetails?.SourceId.Kind ?? "—";
    public string SourceId => Snapshot.SourceDetails?.SourceId.Value ?? _text[LanguageManager.Keys.PlaybackSourceUnknown];

    private int? CurrentBitrate => Snapshot.AudioInfo?.BitrateKbps is > 0 and var measured
        ? measured
        : Snapshot.SourceDetails?.Source.BitrateKbps;

    public string SourceBitrate => CurrentBitrate is > 0 and var bitrate
        ? $"{bitrate} kbps"
        : _text[LanguageManager.Keys.PlaybackSourceUnknown];

    public string BitrateOrigin => _text[Snapshot.AudioInfo?.BitrateKbps is > 0
        ? LanguageManager.Keys.PlaybackBitrateDecoder
        : LanguageManager.Keys.PlaybackBitrateSource];

    public string SourceCodec => Snapshot.AudioInfo?.Codec ?? _text[LanguageManager.Keys.PlaybackSourceUnknown];

    public string SourceSampleRate => Snapshot.AudioInfo?.SampleRateHz is > 0 and var rate
        ? $"{rate / 1000d:0.###} kHz"
        : _text[LanguageManager.Keys.PlaybackSourceUnknown];

    public string SourceBitDepth => Snapshot.AudioInfo?.BitsPerSample is > 0 and var bits
        ? $"{bits} bit"
        : _text[LanguageManager.Keys.PlaybackSourceUnknown];

    public string SourceChannels => Snapshot.AudioInfo?.Channels is > 0 and var channels
        ? channels.ToString()
        : _text[LanguageManager.Keys.PlaybackSourceUnknown];

    public string AudioFormatLabel => HasSourceDetails
        ? string.Join(" · ", new[]
        {
            QualityLabel, Snapshot.AudioInfo?.Codec,
            CurrentBitrate is > 0 ? SourceBitrate : null,
            Snapshot.AudioInfo?.SampleRateHz is > 0 ? SourceSampleRate : null,
            Snapshot.AudioInfo?.BitsPerSample is > 0 ? SourceBitDepth : null
        }.Where(value => !string.IsNullOrEmpty(value)))
        : "";

    public string SourcePlayer => Snapshot.PlayerId ?? _text[LanguageManager.Keys.PlaybackSourceUnknown];
    public string TimeLabel => $"{PositionText} / {DurationText}";

    public string PlaybackDetails => _text.Format(LanguageManager.Keys.PlaybackDetailsFormat, Snapshot.CurrentTrack?.Id.ToString() ?? "—",
        Snapshot.CurrentIndex + 1, Snapshot.QueueCount, Snapshot.PlayerId ?? "—",
        Snapshot.RequiredSourceProviderId ?? _text[LanguageManager.Keys.Automatic]);

    public event EventHandler? Changed;

    public Task SeekAsync(double seconds)
    {
        return CanSeek
            ? ExecutePlaybackAsync(
                ct => _session.SeekAsync(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds)), ct),
                false)
            : Task.CompletedTask;
    }

    public void EndSeek()
    {
        IsSeeking = false;
        OnPropertyChanged(nameof(PositionSeconds));
    }

    public Task ExecutePlaybackAsync(Func<CancellationToken, Task> action, bool showBusy = true)
    {
        return RunOperationAsync(async ct =>
        {
            try
            {
                await Task.Run(() => action(ct), ct);
            }
            finally
            {
                UpdateSnapshot();
            }
        }, showBusy: showBusy, busyDelay: TimeSpan.FromMilliseconds(250));
    }

    private void OnPlaybackChanged(object? sender, PlaybackSessionChangedEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Context.IsInitialized && !_closing) UpdateSnapshot();
        });
    }

    public void UpdateSnapshot()
    {
        Snapshot = _session.GetCurrentSnapshot();
        if (!HasSourceDetails) IsSourceDetailsOpen = false;
        if (!_isUpdatingVolume) SetDisplayedVolume(_session.Volume * 100);
        UpdateArtwork();
        RaisePlaybackProperties();
        Changed?.Invoke(this, EventArgs.Empty);
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
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(LanguageManager.Keys.OperationFailed, [exception.Message]);
        }
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
                try
                {
                    await Task.Run(() => _session.SetVolumeAsync(volume));
                }
                catch (Exception exception)
                {
                    SetError(LanguageManager.Keys.OperationFailed, [exception.Message]);
                }
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

    private void OnVolumeChanged(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closing && !_isUpdatingVolume) SetDisplayedVolume(_session.Volume * 100);
        });
    }

    private void UpdateArtwork()
    {
        var uri = Snapshot.CurrentTrack?.ArtworkUri;
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
                try
                {
                    color = await Task.Run(() => ArtworkColorExtractor.Extract(bitmap, cancellationToken),
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Trace.WriteLine($"Artwork color extraction failed: {exception}");
                }

            if (_closing || cancellationToken.IsCancellationRequested || !Equals(uri, _artworkUri)) return;
            SetArtwork(bitmap, color);
            bitmap = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private void SetArtwork(Bitmap? bitmap, Color? color = null)
    {
        var previous = _nowArtwork;
        if (SetProperty(ref _nowArtwork, bitmap, nameof(NowArtwork))) previous?.Dispose();
        Context.Appearance.SetArtworkColor(color);
    }

    private void RaisePlaybackProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(HasSourceDetails), nameof(SourceType), nameof(SourceLocation), nameof(SourceProvider),
                     nameof(SourceNamespace), nameof(SourceKind), nameof(SourceId), nameof(SourceBitrate),
                     nameof(SourcePlayer),
                     nameof(BitrateOrigin), nameof(SourceCodec), nameof(SourceSampleRate), nameof(SourceBitDepth),
                     nameof(SourceChannels), nameof(AudioFormatLabel)
                 })
            OnPropertyChanged(name);
        foreach (var name in new[]
                 {
                     nameof(QualityLabel), nameof(NowTitle), nameof(NowArtist), nameof(HasCurrentTrack),
                     nameof(IsPlaying), nameof(PlayLabel), nameof(PlaybackLabel), nameof(ModeLabel),
                     nameof(IsSequential), nameof(IsRepeatAll), nameof(IsShuffle), nameof(CanPrevious), nameof(CanNext),
                     nameof(CanTogglePlayback), nameof(CanSeek), nameof(CanAdjustVolume), nameof(DurationSeconds),
                     nameof(PositionText), nameof(DurationText)
                 }) OnPropertyChanged(name);
        if (!IsSeeking) OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(PlaybackDetails));
        OnPropertyChanged(nameof(TimeLabel));
    }

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        RaisePlaybackProperties();
    }

    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        RaisePlaybackProperties();
    }

    public override async ValueTask DisposeAsync()
    {
        _closing = true;
        _session.StateChanged -= OnPlaybackChanged;
        _session.TimelineChanged -= OnPlaybackChanged;
        _session.VolumeChanged -= OnVolumeChanged;
        await _lifetime.CancelAsync();
        await base.DisposeAsync();
        await Task.WhenAll(_volumeUpdateTask, _volumeReadTask);
        await Task.WhenAll(_artworkTasks);
        SetArtwork(null);
        _artworkCancellation?.Dispose();
        _lifetime.Dispose();
    }
}
