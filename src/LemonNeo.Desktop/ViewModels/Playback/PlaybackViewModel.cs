using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Application.Playback;
using LemonNeo.Desktop.Controls.Backgrounds;
using LemonNeo.Core.Playback;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Controls.Playback;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.Utils;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Playback;

public sealed class PlaybackViewModel : PageViewModel, IAudioVisualizerSource,
    IBackgroundArtworkSource, ILowFrequencyPulseSource
{
    private readonly IDisposable _appearanceSubscription;
    private readonly ArtworkService _artwork;
    private readonly List<Task> _artworkTasks = [];
    private readonly List<Task> _playbackTasks = [];
    private readonly IPlaybackInteraction _interaction;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PlaybackSessionService _session;
    private readonly IDisposable _visualEffectsSubscription;
    private readonly LowFrequencyPulseAnalyzer _pulseAnalyzer = new();
    private CancellationTokenSource? _artworkCancellation;
    private BackgroundArtworkFrame? _backgroundArtwork;
    private Uri? _artworkUri;
    private bool _closing;
    private bool _isSourceDetailsOpen;
    private bool _isUpdatingVolume;
    private LoadedArtwork? _nowArtwork;
    private float[] _pulseSpectrum = [];
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
        // New playback input replaces the previous request in the session.
        const AsyncRelayCommandOptions commandOptions = AsyncRelayCommandOptions.AllowConcurrentExecutions;
        TogglePlaybackCommand = new AsyncRelayCommand(() => ExecutePlaybackAsync(
            ct => _interaction.ExecuteAsync(PlaybackCommand.TogglePlayPause, cancellationToken: ct),
            Snapshot.State == PlaybackState.Stopped), commandOptions);
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
        _session.MetadataChanged += OnMetadataChanged;
        _session.StateChanged += OnStateChanged;
        _session.TimelineChanged += OnTimelineChanged;
        _session.QueueChanged += OnQueueChanged;
        _session.VolumeChanged += OnVolumeChanged;
        _appearanceSubscription = Context.Settings.Subscribe<AppearancePreferences>(_ =>
        {
            OnPropertyChanged(nameof(CanVisualizeAudio));
            OnPropertyChanged(nameof(CanAnimateBackground));
            OnPropertyChanged(nameof(ReduceMotion));
        });
        _visualEffectsSubscription = Context.Settings.Subscribe<VisualEffectsPreferences>(_ =>
            OnPropertyChanged(nameof(CanVisualizeAudio)));
    }

    public bool IsSeeking { get; set; }
    public PlaybackSessionSnapshot Snapshot { get; private set; }

    public AsyncRelayCommand TogglePlaybackCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand CycleModeCommand { get; }

    public AsyncRelayCommand<string> ModeCommand { get; }

    // Loading does not disable selection of another track.
    public override bool CanInteract => Context.IsInitialized && !_closing;
    private bool CanControlPlayback => CanInteract;
    public string NowTitle => Snapshot.CurrentTrack?.Title ?? _text[LanguageManager.Keys.NoTrack];

    public string NowArtist => Snapshot.CurrentTrack is { } track
        ? track.Artists.Count > 0 ? string.Join(" / ", track.Artists.Select(a => a.Name)) : _text[LanguageManager.Keys.UnknownArtist]
        : _text[LanguageManager.Keys.NoTrackDescription];

    public bool HasCurrentTrack => Snapshot.CurrentTrack is not null;
    public Bitmap? NowArtwork => _nowArtwork?.Bitmap;
    public LoadedArtwork? NowArtworkReference => _nowArtwork;
    public BackgroundArtworkFrame? CurrentArtwork => _backgroundArtwork;
    public bool IsPlaying => Snapshot.State == PlaybackState.Playing;
    public bool IsLoading => Snapshot.State == PlaybackState.Loading;
    public bool ReduceMotion => Context.Appearance.Current.ReduceMotion;
    public string PlayLabel => _text[IsPlaying ? LanguageManager.Keys.Pause : LanguageManager.Keys.Play];

    public string PlaybackLabel =>
        Snapshot.RestoredStatus is not null ? _text[LanguageManager.Keys.Restored] : _text[Snapshot.State switch
        {
            PlaybackState.Loading => "PlaybackLoading",
            PlaybackState.Failed => "PlaybackFailed",
            _ => Snapshot.State.ToString()
        }];

    public string ModeLabel => _text[Snapshot.Mode.ToString()];
    public bool IsSequential => Snapshot.Mode == PlaybackMode.Sequential;
    public bool IsRepeatAll => Snapshot.Mode == PlaybackMode.RepeatAll;
    public bool IsShuffle => Snapshot.Mode == PlaybackMode.Shuffle;
    public bool CanPrevious => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Previous);
    public bool CanNext => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Next);
    public bool CanTogglePlayback => CanControlPlayback && HasCurrentTrack && !IsLoading;

    public bool CanSeek => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Seek) &&
                           Snapshot.Timeline.Duration is { Ticks: > 0 };

    public bool CanAdjustVolume => CanInteract && _session.CanAdjustVolume;
    public bool CanVisualizeAudio => CanInteract && Context.Settings.Get<VisualEffectsPreferences>()
        .AudioVisualizerEnabled && !Context.Appearance.Current.ReduceMotion && _session.CanReadFftData;
    public bool CanAnimateBackground => CanInteract && HasCurrentTrack && !Context.Appearance.Current.ReduceMotion;
    public int FftBinCount => _session.FftBinCount;

    public event EventHandler? ArtworkChanged;

    public bool TryGetFftData(Span<float> destination)
    {
        return _session.TryGetFftData(destination);
    }

    public bool TryGetLowFrequencyPulse(out float pulse)
    {
        pulse = 0f;
        var binCount = _session.FftBinCount;
        if (!IsPlaying || !_session.CanReadFftData || binCount <= 0)
        {
            _pulseAnalyzer.Reset();
            return false;
        }

        if (_pulseSpectrum.Length != binCount) _pulseSpectrum = new float[binCount];
        if (!_session.TryGetFftData(_pulseSpectrum)) return false;
        pulse = _pulseAnalyzer.Process(_pulseSpectrum, Snapshot.AudioInfo?.SampleRateHz ?? 48000);
        return true;
    }

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

    public event EventHandler? MetadataChanged;
    public event EventHandler? TimelineChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? QueueChanged;
    public event EventHandler? VolumeChanged;

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
        if (!CanInteract) return Task.CompletedTask;
        _playbackTasks.RemoveAll(task => task.IsCompleted);
        var task = ExecuteCoreAsync(action);
        _playbackTasks.Add(task);
        return task;
    }

    private async Task ExecuteCoreAsync(Func<CancellationToken, Task> action)
    {
        try { await action(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_closing) SetError(LanguageManager.Keys.OperationFailed, [exception.Message]);
        }
    }

    private void OnMetadataChanged(object? sender, PlaybackSessionChangedEventArgs args) => PostUpdate(PlaybackUpdateKind.Metadata);
    private void OnStateChanged(object? sender, PlaybackSessionChangedEventArgs args) => PostUpdate(PlaybackUpdateKind.State);
    private void OnTimelineChanged(object? sender, PlaybackSessionChangedEventArgs args) => PostUpdate(PlaybackUpdateKind.Timeline);
    private void OnQueueChanged(object? sender, PlaybackSessionChangedEventArgs args) => PostUpdate(PlaybackUpdateKind.Queue);

    private void PostUpdate(PlaybackUpdateKind changes)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Context.IsInitialized && !_closing) ApplySnapshot(changes);
        });
    }

    public void UpdateSnapshot() => ApplySnapshot(PlaybackUpdateKind.All);

    private void ApplySnapshot(PlaybackUpdateKind changes)
    {
        Snapshot = _session.GetCurrentSnapshot();
        if ((changes & PlaybackUpdateKind.Metadata) != 0)
        {
            if (!HasSourceDetails) IsSourceDetailsOpen = false;
            UpdateArtwork();
            RaiseMetadataProperties();
            MetadataChanged?.Invoke(this, EventArgs.Empty);
        }
        if ((changes & PlaybackUpdateKind.State) != 0)
        {
            IsBusy = IsLoading;
            if (Snapshot.Error is { } error) SetError(LanguageManager.Keys.OperationFailed, [error]);
            else SetError(null);
            RaiseStateProperties();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        if ((changes & PlaybackUpdateKind.Queue) != 0)
        {
            RaiseQueueProperties();
            QueueChanged?.Invoke(this, EventArgs.Empty);
        }
        if ((changes & PlaybackUpdateKind.Timeline) != 0)
        {
            RaiseTimelineProperties();
            TimelineChanged?.Invoke(this, EventArgs.Empty);
        }
        if ((changes & PlaybackUpdateKind.Volume) != 0 && !_isUpdatingVolume)
            SetDisplayedVolume(Snapshot.Volume * 100);
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
            if (!_closing)
            {
                ApplySnapshot(PlaybackUpdateKind.Volume);
                VolumeChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void UpdateArtwork()
    {
        var uri = Snapshot.CurrentTrack?.ArtworkUri;
        // Monitor ticks and play/pause changes must not download the same cover again.
        // Tracking the URI separately also loads covers from an already restored session.
        if (_closing || Equals(uri, _artworkUri)) return;
        _artworkUri = uri;
        _pulseAnalyzer.Reset();
        _artworkCancellation?.Cancel();
        _artworkCancellation?.Dispose();
        _artworkCancellation = null;
        if (uri is null)
        {
            SetArtwork(null);
            return;
        }
        // Keep the current cover and palette while loading so the incoming image can
        // transition directly from the previous one instead of flashing a placeholder.
        _artworkCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _artworkTasks.RemoveAll(t => t.IsCompleted);
        _artworkTasks.Add(LoadArtworkAsync(uri, _artworkCancellation.Token));
    }

    private async Task LoadArtworkAsync(Uri uri, CancellationToken cancellationToken)
    {
        LoadedArtwork? loaded = null;
        try
        {
            loaded = await _artwork.LoadWithDataAsync(uri, cancellationToken);
            var bitmap = loaded?.Bitmap;
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
            SetArtwork(loaded, color, loaded is null
                ? null
                : new BackgroundArtworkFrame(uri.AbsoluteUri, loaded.EncodedData));
            loaded = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            loaded?.Dispose();
        }
    }

    private void SetArtwork(LoadedArtwork? bitmap, Color? color = null, BackgroundArtworkFrame? backgroundArtwork = null)
    {
        var previous = _nowArtwork;
        if (SetProperty(ref _nowArtwork, bitmap, nameof(NowArtwork)))
        {
            OnPropertyChanged(nameof(NowArtworkReference));
            previous?.Dispose();
        }
        if (!Equals(_backgroundArtwork, backgroundArtwork))
        {
            _backgroundArtwork = backgroundArtwork;
            OnPropertyChanged(nameof(CurrentArtwork));
            ArtworkChanged?.Invoke(this, EventArgs.Empty);
        }
        Context.Appearance.SetArtworkColor(color);
    }

    private void RaiseMetadataProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(HasSourceDetails), nameof(SourceType), nameof(SourceLocation), nameof(SourceProvider),
                     nameof(SourceNamespace), nameof(SourceKind), nameof(SourceId), nameof(SourceBitrate),
                     nameof(SourcePlayer),
                     nameof(BitrateOrigin), nameof(SourceCodec), nameof(SourceSampleRate), nameof(SourceBitDepth),
                     nameof(SourceChannels), nameof(AudioFormatLabel), nameof(QualityLabel), nameof(NowTitle),
                     nameof(NowArtist), nameof(HasCurrentTrack), nameof(CanAnimateBackground), nameof(CanTogglePlayback)
                 })
            OnPropertyChanged(name);
    }

    private void RaiseStateProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(IsPlaying), nameof(IsLoading), nameof(PlayLabel), nameof(PlaybackLabel),
                     nameof(CanTogglePlayback), nameof(CanSeek), nameof(CanAdjustVolume), nameof(CanVisualizeAudio),
                     nameof(CanAnimateBackground)
                 }) OnPropertyChanged(name);
        RaiseQueueProperties();
    }

    private void RaiseQueueProperties()
    {
        foreach (var name in new[] { nameof(ModeLabel), nameof(IsSequential), nameof(IsRepeatAll), nameof(IsShuffle),
                     nameof(CanPrevious), nameof(CanNext), nameof(PlaybackDetails) }) OnPropertyChanged(name);
    }

    private void RaiseTimelineProperties()
    {
        foreach (var name in new[] { nameof(DurationSeconds), nameof(PositionText), nameof(DurationText), nameof(CanSeek) })
            OnPropertyChanged(name);
        if (!IsSeeking) OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(TimeLabel));
    }

    private void RaisePlaybackProperties()
    {
        RaiseMetadataProperties();
        RaiseStateProperties();
        RaiseTimelineProperties();
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
        _session.MetadataChanged -= OnMetadataChanged;
        _session.StateChanged -= OnStateChanged;
        _session.TimelineChanged -= OnTimelineChanged;
        _session.QueueChanged -= OnQueueChanged;
        _session.VolumeChanged -= OnVolumeChanged;
        _appearanceSubscription.Dispose();
        _visualEffectsSubscription.Dispose();
        await _lifetime.CancelAsync();
        await base.DisposeAsync();
        await Task.WhenAll(_volumeUpdateTask, _volumeReadTask);
        await Task.WhenAll(_artworkTasks);
        await Task.WhenAll(_playbackTasks);
        SetArtwork(null);
        _artworkCancellation?.Dispose();
        _lifetime.Dispose();
    }
}
