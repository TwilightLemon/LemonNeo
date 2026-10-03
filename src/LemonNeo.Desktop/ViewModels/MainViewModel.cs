using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;

namespace LemonNeo.Desktop.ViewModels;

public enum DesktopPage { Library, Search, Online, Queue, Lyrics, Editor, Tools, Settings, Components }

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DesktopRuntime _runtime;
    private readonly PlaybackSessionService _session;
    private readonly IPlaybackInteraction _interaction;
    private readonly LocalizationService _text;
    private readonly AppearanceService _appearance;
    private readonly ArtworkService _artwork;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _lyricsCancellation;
    private CancellationTokenSource? _artworkCancellation;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _operationCancellation;
    private Task _playbackOperation = Task.CompletedTask;
    private CancellationTokenSource? _playbackCancellation;
    private bool _isPlaybackBusy;
    private readonly List<Task> _lyricsTasks = [];
    private readonly List<Task> _artworkTasks = [];
    private Uri? _artworkUri;
    private Bitmap? _nowArtwork;
    private DesktopPage _page;
    private Playlist? _selectedPlaylist;
    private TrackRow? _selectedTrack;
    private string _filter = "";
    private bool _isBusy;
    private bool _initialized;
    private bool _closing;
    public bool IsSeeking { get; set; }
    private LocalizedNotice? _error;
    private string _statusKey = "Loading";
    private object?[] _statusArguments = [];
    private PlaybackSessionSnapshot _snapshot;
    private Lyrics? _lyrics;
    private Track? _lyricsTrack;
    private string _lyricsStatusKey = "NoLyrics";
    private IReadOnlyList<LyricsLine> _lyricLines = [];

    public MainViewModel(DesktopRuntime runtime, LocalizationService text, AppearanceService appearance, ArtworkService artwork)
    {
        _runtime = runtime;
        _session = runtime.Get<PlaybackSessionService>();
        _interaction = runtime.Get<IPlaybackInteraction>();
        _text = text;
        _appearance = appearance;
        _artwork = artwork;
        _snapshot = _session.Snapshot;
        NavigateCommand = new(p => Navigate(Enum.Parse<DesktopPage>(p!)));
        PlayAllCommand = new(() => PlayAsync());
        PlaySelectedCommand = new(() => PlayAsync(SelectedTrack));
        TogglePlaybackCommand = new(() => ExecutePlaybackAsync(ct => _interaction.ExecuteAsync(PlaybackCommand.TogglePlayPause, cancellationToken: ct)));
        PreviousCommand = new(() => ExecutePlaybackAsync(ct => _interaction.ExecuteAsync(PlaybackCommand.Previous, cancellationToken: ct)));
        NextCommand = new(() => ExecutePlaybackAsync(ct => _interaction.ExecuteAsync(PlaybackCommand.Next, cancellationToken: ct)));
        StopCommand = new(() => ExecutePlaybackAsync(ct => _session.StopAsync(ct)));
        ClearQueueCommand = new(() => ExecutePlaybackAsync(ct => _session.ClearQueueAsync(ct)));
        CycleModeCommand = new(() => ExecutePlaybackAsync(ct => _session.SetModeAsync((PlaybackMode)(((int)_snapshot.Mode + 1) % 3), ct)));
        RefreshCommand = new(() => _initialized ? ReloadLibraryAsync() : InitializeAsync());
        DismissErrorCommand = new(() => SetError(null));
        SampleCommand = new(() => SetStatus("SampleActionDone"));
        ThemeCommand = new(p => SavePreferencesAsync(_appearance.Current with { Theme = Enum.Parse<ThemePreference>(p!) }));
        LanguageCommand = new(p => SavePreferencesAsync(_appearance.Current with { Language = Enum.Parse<LanguagePreference>(p!) }));
        InitializeFeatureCommands();
        _session.StateChanged += OnPlaybackChanged;
        _session.TimelineChanged += OnPlaybackChanged;
        _text.Changed += OnLanguageChanged;
        _appearance.Changed += OnAppearanceChanged;
    }

    public ObservableCollection<Playlist> Playlists { get; } = [];
    public ObservableCollection<TrackRow> Tracks { get; } = [];
    public ObservableCollection<string> PluginDescriptions { get; } = [];
    public RelayCommand<string> NavigateCommand { get; }
    public AsyncRelayCommand PlayAllCommand { get; }
    public AsyncRelayCommand PlaySelectedCommand { get; }
    public AsyncRelayCommand TogglePlaybackCommand { get; }
    public AsyncRelayCommand PreviousCommand { get; }
    public AsyncRelayCommand NextCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand ClearQueueCommand { get; }
    public AsyncRelayCommand CycleModeCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand DismissErrorCommand { get; }
    public RelayCommand SampleCommand { get; }
    public AsyncRelayCommand<string> ThemeCommand { get; }
    public AsyncRelayCommand<string> LanguageCommand { get; }

    public DesktopPage Page { get => _page; private set { SetProperty(ref _page, value); RaiseViewProperties(); } }
    public bool IsLibrary => Page == DesktopPage.Library;
    public bool IsQueue => Page == DesktopPage.Queue;
    public bool IsLyrics => Page == DesktopPage.Lyrics;
    public bool IsSettings => Page == DesktopPage.Settings;
    public bool IsComponents => Page == DesktopPage.Components;
    public bool IsTrackPage => IsLibrary || IsQueue || IsSearch;
    public bool IsLibraryOrQueue => IsLibrary || IsQueue;
    public bool IsBusy { get => _isBusy || _isPlaybackBusy; private set { SetProperty(ref _isBusy, value); RaiseBusyProperties(); } }
    private bool CanControlPlayback => _initialized && !_closing && !_isPlaybackBusy;
    public bool CanInteract => !IsBusy && _initialized && !_closing;
    public bool CanRetryStartup => !_initialized && !_closing;
    public bool CanPlay => CanInteract && Tracks.Count > 0;
    public bool HasTracks => Tracks.Count > 0;
    public string? Error => _error is { } error ? _text.Format(error.Key, error.Arguments) +
        (error.Details is null ? "" : Environment.NewLine + error.Details) : null;
    public bool HasError => _error is not null;
    public string Status => _text.Format(_statusKey, _statusArguments);
    public string PageTitle => IsLibrary && SelectedPlaylist is { } playlist ? playlist.Name : _text[Page.ToString()];
    public string PageDescription => IsLibrary
        ? string.IsNullOrWhiteSpace(SelectedPlaylist?.Description) ? "" : SelectedPlaylist.Description
        : _text[$"{Page}Description"];
    public string? PageSummary => IsLibrary ? TrackSummary : null;
    public string LibrarySummary => _text.Format("LibraryStatistics", Playlists.Count,
        _runtime.Get<LibraryService>().Current.Playlists.SelectMany(p => p.Items).DistinctBy(i => i.Track.Id).Count(),
        _runtime.Get<LibraryService>().Current.Playlists.Sum(p => p.Items.Count));
    public string TrackSummary => _text.Format("TrackSummary", Tracks.Count,
        TrackRow.FormatTime(TimeSpan.FromSeconds(Tracks.Sum(t => t.Track.Duration?.TotalSeconds ?? 0))));
    public string EmptyTitle => _text[!string.IsNullOrWhiteSpace(Filter) ? "NoResults" : IsQueue ? "EmptyQueue" : SelectedPlaylist is null ? "EmptyLibrary" : "PlaylistEmpty"];
    public string EmptyDescription => _text[!string.IsNullOrWhiteSpace(Filter) ? "NoResultsDescription" : IsQueue ? "EmptyQueueDescription" : SelectedPlaylist is null ? "EmptyLibraryDescription" : "PlaylistEmptyDescription"];
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value)) RefreshTracks(); } }
    public Playlist? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            if (!SetProperty(ref _selectedPlaylist, value)) return;
            if (value is not null) Page = DesktopPage.Library;
            Filter = "";
            RefreshTracks();
            RaiseSelectionProperties();
            RaiseViewProperties();
        }
    }
    public TrackRow? SelectedTrack { get => _selectedTrack; set { if (SetProperty(ref _selectedTrack, value)) RaiseSelectionProperties(); } }
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
    public bool CanPrevious => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Previous);
    public bool CanNext => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Next);
    public bool CanTogglePlayback => CanControlPlayback && HasCurrentTrack;
    public bool CanSeek => CanControlPlayback && _interaction.Snapshot.Controls.HasFlag(PlaybackControls.Seek) && _snapshot.Timeline.Duration is { Ticks: > 0 };
    public double PositionSeconds => _snapshot.Timeline.Position.TotalSeconds;
    public double DurationSeconds => Math.Max(1, _snapshot.Timeline.Duration?.TotalSeconds ?? 1);
    public string PositionText => TrackRow.FormatTime(_snapshot.Timeline.Position);
    public string DurationText => _snapshot.Timeline.Duration is { } duration ? TrackRow.FormatTime(duration) : "—";
    public string LyricsText => _lyrics?.Text ?? _text[_lyricsStatusKey];
    public string CurrentLyric { get; private set; } = "";
    public string PreviousLyric { get; private set; } = "";
    public string NextLyric { get; private set; } = "";
    public string Translation { get; private set; } = "";
    public bool HasTimedLyrics => _lyricLines.Any(l => l.EffectiveStartTimeMs is not null);
    public bool IsThemeSystem => _appearance.Current.Theme == ThemePreference.System;
    public bool IsThemeLight => _appearance.Current.Theme == ThemePreference.Light;
    public bool IsThemeDark => _appearance.Current.Theme == ThemePreference.Dark;
    public bool IsLanguageSystem => _appearance.Current.Language == LanguagePreference.System;
    public bool IsLanguageChinese => _appearance.Current.Language == LanguagePreference.Chinese;
    public bool IsLanguageEnglish => _appearance.Current.Language == LanguagePreference.English;
    public bool ReduceMotion
    {
        get => _appearance.Current.ReduceMotion;
        set { if (value != ReduceMotion) _ = SavePreferencesAsync(_appearance.Current with { ReduceMotion = value }); }
    }
    public string PluginSummary => _text.Format("PluginCount", _runtime.Get<PluginLoader>().Loaded.Count);
    public string LibraryPath => _runtime.Get<DesktopOptions>().LibraryDirectory;
    public string ConfigurationPath => _runtime.Paths.ConfigurationDirectory;
    public string CachePath => _runtime.Paths.CacheDirectory;

    public Task InitializeAsync() => RunOperationAsync(async ct =>
    {
        await _appearance.LoadAsync(ct);
        await Task.Run(() => _runtime.InitializeAsync(ct), ct);
        _initialized = true;
        RefreshLibrary();
        var report = _runtime.Get<PluginLoader>().LastReport;
        PluginDescriptions.Clear();
        foreach (var plugin in report.Loaded)
            PluginDescriptions.Add($"{plugin.Manifest.Name}  ·  {plugin.Manifest.Version}");
        if (report.Failures.Count > 0)
            SetError("PluginFailures", [report.Failures.Count], string.Join(Environment.NewLine, report.Failures.Select(f => f.Error)));
        UpdateSnapshot();
        RefreshProviderOptions();
        await LoadStartupScriptCoreAsync(ct);
        RaiseFeatureProperties();
    }, "Loading", allowUninitialized: true);

    public Task ReloadLibraryAsync() => ExecuteAsync(async ct =>
    {
        await _runtime.Get<LibraryService>().LoadAsync(ct);
        RefreshLibrary();
    });

    public Task CreatePlaylistAsync(string name, string? description = null, string? folderPath = null) => ExecuteAsync(async ct =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var playlists = _runtime.Get<PlaylistService>();
        Playlist playlist;
        FolderImportResult? import = null;
        if (folderPath is null) playlist = playlists.Create(name, description);
        else
        {
            import = await Task.Run(() => _runtime.Get<FolderImportService>().ImportAsync(folderPath, name, cancellationToken: ct), ct);
            playlist = playlists.Update(import.Playlist.Id, name, description);
        }
        await _runtime.Get<LibraryService>().SaveAsync(ct);
        RefreshLibrary(playlist.Id);
        if (import is null) SetStatus("PlaylistCreated", playlist.Name);
        else SetStatus("Imported", import.ImportedTracks, import.SkippedFiles.Count);
    });

    public Task PlayAsync(TrackRow? selected = null) => ExecutePlaybackAsync(async ct =>
    {
        if (IsQueue)
        {
            var row = selected ?? Tracks.FirstOrDefault();
            if (row?.QueueItemId is { } id) await _session.PlayQueueItemAsync(id, ct);
            return;
        }
        if (IsSearch)
        {
            if ((selected ?? SelectedTrack ?? Tracks.FirstOrDefault()) is { } result)
                await _session.PlayTrackAsync(result.Track, new PlaybackRequest(), ct);
            return;
        }
        var tracks = Tracks.Select(t => t.Track).ToArray();
        if (tracks.Length == 0) return;
        var index = selected is null ? 0 : Tracks.IndexOf(selected);
        if (index < 0) return;
        await _session.PlayPlaylistAsync(tracks, index, CreatePlaybackRequest(), ct);
    });

    public Task SeekAsync(double seconds) => CanSeek
        ? ExecutePlaybackAsync(ct => _session.SeekAsync(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds)), ct))
        : Task.CompletedTask;

    public void EndSeek()
    {
        IsSeeking = false;
        OnPropertyChanged(nameof(PositionSeconds));
    }

    public void Navigate(DesktopPage page)
    {
        if (_closing) return;
        if (page == DesktopPage.Library) SelectedPlaylist = null;
        Page = page;
        Filter = "";
        RefreshTracks();
    }

    private Task SavePreferencesAsync(UiPreferences preferences) => RunOperationAsync(async ct =>
    {
        try
        {
            await _appearance.SetAsync(preferences, ct);
            SetStatus("Saved");
        }
        finally { OnAppearanceChanged(this, EventArgs.Empty); }
    }, allowUninitialized: true);

    private Task ExecuteAsync(Func<CancellationToken, Task> action) => RunOperationAsync(action);

    private void RaiseBusyProperties()
    {
        OnPropertyChanged(nameof(CanInteract)); OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(CanRetryStartup));
        RaiseSelectionProperties(); RaisePlaybackProperties();
    }

    private Task ExecutePlaybackAsync(Func<CancellationToken, Task> action)
    {
        if (!CanControlPlayback) return Task.CompletedTask;
        _playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _playbackOperation = RunPlaybackCoreAsync(action, _playbackCancellation);
        return _playbackOperation;
    }

    private async Task RunPlaybackCoreAsync(Func<CancellationToken, Task> action, CancellationTokenSource cancellation)
    {
        SetProperty(ref _isPlaybackBusy, true, nameof(IsBusy));
        RaiseBusyProperties();
        if (!_isBusy) { SetError(null); SetStatus("Working"); }
        try { await action(cancellation.Token); if (!_isBusy) SetStatus("Ready"); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (!_isBusy) SetStatus("Cancelled"); }
        catch (Exception exception) { SetError("OperationFailed", [exception.Message]); }
        finally
        {
            SetProperty(ref _isPlaybackBusy, false, nameof(IsBusy));
            RaiseBusyProperties(); UpdateSnapshot();
            cancellation.Dispose(); _playbackCancellation = null;
        }
    }

    private Task RunOperationAsync(Func<CancellationToken, Task> action, string status = "Working", bool allowUninitialized = false)
    {
        if (IsBusy || _closing || !_initialized && !allowUninitialized) return Task.CompletedTask;
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = RunCoreAsync(action, status, _operationCancellation);
        return _operation;
    }

    private async Task RunCoreAsync(Func<CancellationToken, Task> action, string status, CancellationTokenSource cancellation)
    {
        IsBusy = true;
        SetError(null);
        SetStatus(status);
        try
        {
            await action(cancellation.Token);
            if (_statusKey == status) SetStatus("Ready");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { SetStatus("Cancelled"); }
        catch (Exception exception)
        {
            SetError(_initialized ? "OperationFailed" : "StartupFailed", [exception.Message]);
            SetStatus("Ready");
        }
        finally
        {
            IsBusy = false;
            UpdateSnapshot();
            RaisePlaybackProperties();
            OnPropertyChanged(nameof(AuthenticationStatus)); OnPropertyChanged(nameof(AuthenticationAccount));
            cancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private void RefreshLibrary(Guid? selectId = null)
    {
        var id = selectId ?? SelectedPlaylist?.Id;
        Playlists.Clear();
        foreach (var playlist in _runtime.Get<LibraryService>().Current.Playlists.OrderBy(p => p.Name, StringComparer.Create(_text.Culture, true)))
            Playlists.Add(playlist);
        TargetPlaylist = Playlists.FirstOrDefault(p => p.Id == TargetPlaylist?.Id) ?? Playlists.FirstOrDefault();
        var selection = Playlists.FirstOrDefault(p => p.Id == id);
        if (selectId is not null)
        {
            SelectedPlaylist = selection;
            Page = DesktopPage.Library;
            Filter = "";
        }
        else SetProperty(ref _selectedPlaylist, selection, nameof(SelectedPlaylist));
        RefreshTracks();
        OnPropertyChanged(nameof(LibrarySummary));
        RaiseViewProperties();
        RaiseFeatureProperties();
    }

    private void RefreshTracks()
    {
        if (!IsTrackPage) return;
        var selected = SelectedTrack;
        IEnumerable<TrackRow> tracks = IsQueue
            ? _session.Queue.Items.Select((i, index) => new TrackRow(i.Track, index + 1, _text, QueueItemId: i.Id))
            : IsSearch ? _searchTracks.Select((t, index) => new TrackRow(t, index + 1, _text))
            : SelectedPlaylist is { } playlist
                ? playlist.Items.OrderBy(i => FractionalPlaylistOrderingService.Parse(i.SortKey)).ThenBy(i => i.Id)
                    .Select((i, index) => new TrackRow(i.Track, index + 1, _text, PlaylistId: playlist.Id, PlaylistItemId: i.Id))
                : Playlists.SelectMany(p => p.Items).Select(i => i.Track).DistinctBy(t => t.Id).OrderBy(t => t.Title)
                    .Select((t, index) => new TrackRow(t, index + 1, _text));
        if (!string.IsNullOrWhiteSpace(Filter))
            tracks = tracks.Where(t => $"{t.Track.Title} {string.Join(' ', t.Track.Artists.Select(a => a.Name))} {t.Track.Album?.Title}"
                .Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase));
        Tracks.Clear();
        foreach (var track in tracks) Tracks.Add(track);
        SelectedTrack = Tracks.FirstOrDefault(t => selected?.PlaylistItemId is { } itemId ? t.PlaylistItemId == itemId
            : selected?.QueueItemId is { } queueId ? t.QueueItemId == queueId : t.Track.Id == selected?.Track.Id);
        OnPropertyChanged(nameof(HasTracks)); OnPropertyChanged(nameof(CanPlay)); OnPropertyChanged(nameof(TrackSummary));
        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(EmptyDescription));
        RaiseSelectionProperties();
    }

    private void OnPlaybackChanged(object? sender, PlaybackSessionChangedEventArgs args) =>
        Dispatcher.UIThread.Post(() => { if (_initialized && !_closing) UpdateSnapshot(); });

    private void UpdateSnapshot()
    {
        var previous = _snapshot;
        _snapshot = _session.GetCurrentSnapshot();
        UpdateArtwork();
        RaisePlaybackProperties();
        UpdateLyricsPosition();
        if (!Equals(_lyricsTrack, _snapshot.CurrentTrack))
        {
            _lyricsTrack = _snapshot.CurrentTrack;
            _lyricsCancellation?.Cancel();
            _lyricsCancellation?.Dispose();
            _lyricsCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _lyricsTasks.RemoveAll(t => t.IsCompleted);
            _lyricsTasks.Add(LoadLyricsAsync(_snapshot.CurrentTrack, _lyricsCancellation.Token));
        }
        if (IsQueue && (previous.QueueCount != _snapshot.QueueCount || previous.CurrentTrack?.Id != _snapshot.CurrentTrack?.Id)) RefreshTracks();
    }

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
            if (_closing || cancellationToken.IsCancellationRequested || !Equals(uri, _artworkUri)) return;
            SetArtwork(bitmap);
            bitmap = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { bitmap?.Dispose(); }
    }

    private void SetArtwork(Bitmap? bitmap)
    {
        var previous = _nowArtwork;
        if (SetProperty(ref _nowArtwork, bitmap, nameof(NowArtwork))) previous?.Dispose();
    }

    private async Task LoadLyricsAsync(Track? track, CancellationToken cancellationToken)
    {
        _lyrics = null;
        _lyricLines = [];
        _lyricsStatusKey = track is null ? "NoLyrics" : "LyricsLoading";
        RaiseLyricsProperties();
        if (track is null) return;
        try
        {
            var result = await Task.Run(() => _runtime.Get<LyricsService>().GetLyricsAsync(track, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!Equals(_snapshot.CurrentTrack, track)) return;
            _lyrics = result.Lyrics;
            _lyricsStatusKey = "NoLyrics";
            _lyricLines = _lyrics?.Lines?.OrderBy(l => l.EffectiveStartTimeMs ?? int.MaxValue).ToArray() ?? [];
            UpdateLyricsPosition();
            RaiseLyricsProperties();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception) { if (!cancellationToken.IsCancellationRequested) { _lyricsStatusKey = "LyricsError"; RaiseLyricsProperties(); } }
    }

    private void UpdateLyricsPosition()
    {
        var milliseconds = _snapshot.Timeline.Position.TotalMilliseconds;
        var index = -1;
        for (var i = 0; i < _lyricLines.Count; i++)
            if (_lyricLines[i].EffectiveStartTimeMs is { } start && start <= milliseconds) index = i;
        var frame = _lyrics is not null && _snapshot.State != PlayerState.Stopped
            ? LyricsTimeline.CreateFrame(_lyrics, milliseconds) : new LyricsDisplayFrame(LyricsDisplayRow.Empty, null, null);
        CurrentLyric = frame.Main.Text;
        PreviousLyric = _snapshot.State != PlayerState.Stopped && index > 0 ? _lyricLines[index - 1].Text : "";
        NextLyric = _snapshot.State != PlayerState.Stopped && index + 1 < _lyricLines.Count ? _lyricLines[index + 1].Text : "";
        Translation = frame.Translation?.Text ?? "";
        SetLyricSegments(frame);
        OnPropertyChanged(nameof(CurrentLyric)); OnPropertyChanged(nameof(PreviousLyric)); OnPropertyChanged(nameof(NextLyric)); OnPropertyChanged(nameof(Translation));
    }

    private void SetStatus(string key, params object?[] arguments) { _statusKey = key; _statusArguments = arguments; OnPropertyChanged(nameof(Status)); }
    private void SetError(string? key, object?[]? arguments = null, string? details = null)
    {
        _error = key is null ? null : new(key, arguments ?? [], details);
        OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(HasError));
    }
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Error)); OnPropertyChanged(nameof(LibrarySummary)); OnPropertyChanged(nameof(PluginSummary));
        RaiseViewProperties(); RaisePlaybackProperties(); RefreshTracks(); UpdateLyricsPosition(); RaiseLyricsProperties();
        RefreshProviderOptions(); RefreshLocalizedResults(); RaiseFeatureProperties();
    }
    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        foreach (var name in new[] { nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark), nameof(IsLanguageSystem), nameof(IsLanguageChinese), nameof(IsLanguageEnglish), nameof(ReduceMotion) }) OnPropertyChanged(name);
    }
    private void RaiseViewProperties()
    {
        foreach (var name in new[] { nameof(IsLibrary), nameof(IsQueue), nameof(IsLyrics), nameof(IsSettings), nameof(IsComponents), nameof(IsSearch), nameof(IsOnline), nameof(IsTools), nameof(IsEditor), nameof(IsTrackPage), nameof(IsLibraryOrQueue), nameof(HasSelectedPlaylist), nameof(PageTitle), nameof(PageDescription), nameof(PageSummary) }) OnPropertyChanged(name);
    }
    private void RaisePlaybackProperties()
    {
        foreach (var name in new[] { nameof(NowTitle), nameof(NowArtist), nameof(HasCurrentTrack), nameof(IsPlaying), nameof(PlayLabel), nameof(PlaybackLabel), nameof(ModeLabel), nameof(CanPrevious), nameof(CanNext), nameof(CanTogglePlayback), nameof(CanSeek), nameof(DurationSeconds), nameof(PositionText), nameof(DurationText) }) OnPropertyChanged(name);
        if (!IsSeeking) OnPropertyChanged(nameof(PositionSeconds));
        OnPropertyChanged(nameof(PlaybackDetails));
    }
    private void RaiseLyricsProperties() { OnPropertyChanged(nameof(LyricsText)); OnPropertyChanged(nameof(HasTimedLyrics)); }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        await _lifetime.CancelAsync();
        _lyricsCancellation?.Cancel();
        _artworkCancellation?.Cancel();
        _session.StateChanged -= OnPlaybackChanged;
        _session.TimelineChanged -= OnPlaybackChanged;
        _text.Changed -= OnLanguageChanged;
        _appearance.Changed -= OnAppearanceChanged;
        await Task.WhenAll(_lyricsTasks.Concat(_artworkTasks).Append(_operation).Append(_playbackOperation));
        SetArtwork(null);
        _lyricsCancellation?.Dispose();
        _artworkCancellation?.Dispose();
        try
        {
            if (_initialized)
            {
                await _runtime.Get<LibraryService>().SaveAsync();
                await _session.SaveAsync();
            }
        }
        finally { _lifetime.Dispose(); }
    }

    private sealed record LocalizedNotice(string Key, object?[] Arguments, string? Details);
}
