using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Library;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Playback;

namespace LemonNeo.Desktop.ViewModels.Shared;

public abstract class TrackListViewModel : PageViewModel
{
    protected readonly DesktopNavigation Navigation;
    private readonly IDisposable _preferencesSubscription;
    private readonly DispatcherTimer _filterTimer;
    private readonly TrackCollection<TrackRow> _tracks = [];
    private TrackRow[] _rows = [];
    private string _filter = "";
    private string _appliedFilter = "";
    private bool _hasPlayingTrack;
    private bool _isSelectionMode;
    private bool _needsRefresh;
    private TrackRow? _selectedTrack;
    private Playlist? _targetPlaylist;
    private bool _updatingSelection;

    protected TrackListViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback,
        string route) : base(context, route)
    {
        Navigation = navigation;
        Playback = playback;
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _filterTimer.Tick += OnFilterTick;
        PlayAllCommand = new AsyncRelayCommand(() => PlayAsync(), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PlaySelectedCommand = new AsyncRelayCommand(() => PlayAsync(SelectedTrack),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        InspectTrackCommand = new RelayCommand(() =>
        {
            if (CanInspectTrack && SelectedTrack is { } row) _ = InspectTrackAsync(row);
        });
        OpenCatalogItemCommand = new AsyncRelayCommand<CatalogItem>(item =>
            CanInteract && !IsSelectionMode && item is not null
                ? Navigation.OpenCatalogItemAsync(item) : Task.CompletedTask);
        AddToPlaylistCommand = new AsyncRelayCommand(async () => await AddSelectedTracksAsync());
        ToggleSelectionCommand = new RelayCommand(() => IsSelectionMode = !IsSelectionMode);
        SelectAllCommand = new RelayCommand(() => SetSelection(true));
        ClearSelectionCommand = new RelayCommand(() => SetSelection(false));
        Context.LibraryChanged += OnLibraryChanged;
        Playback.PropertyChanged += OnPlaybackPropertyChanged;
        Playback.MetadataChanged += OnPlaybackChanged;
        _preferencesSubscription =
            Context.Settings.Subscribe<LibraryPreferences>(_ => OnPropertyChanged(nameof(InsertAtStart)));
        _targetPlaylist = EditablePlaylists.FirstOrDefault();
    }

    public PlaybackViewModel Playback { get; }
    public ObservableCollection<Playlist> Playlists => Context.Playlists;
    public ObservableCollection<TrackRow> Tracks => _tracks;
    protected IList<TrackRow> UnfilteredTracks => _rows;

    public TrackRow? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (SetProperty(ref _selectedTrack, value)) RaiseInteractionProperties();
        }
    }

    public Playlist? TargetPlaylist
    {
        get => _targetPlaylist;
        set
        {
            if (SetProperty(ref _targetPlaylist, value)) RaiseInteractionProperties();
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;
            _filterTimer.Stop();
            // Clearing must be immediate for RevealPlayingTrack and a new search result.
            if (string.IsNullOrWhiteSpace(value)) ApplyFilter();
            else _filterTimer.Start();
            RaiseInteractionProperties();
        }
    }

    public bool HasTracks => Tracks.Count > 0;
    internal bool ReduceMotion => Context.Appearance.Current.ReduceMotion;
    public bool CanPlay => CanInteract && Playback.CanInteract && HasPlayableTracks;
    protected virtual bool HasPlayableTracks => HasTracks;
    public bool CanInspectTrack => CanInteract && SelectedTrack is not null && !IsSelectionMode;

    public IReadOnlyList<TrackRow> ActionTracks => IsSelectionMode ? Tracks.Where(t => t.IsMarked).ToArray()
        : SelectedTrack is { } row ? [row] : [];

    public bool HasActionTracks => CanInteract && ActionTracks.Count > 0;
    public bool CanAddToPlaylist => HasActionTracks && TargetPlaylist is not null &&
                                    !_host.Get<PlaylistService>().IsReadOnly(TargetPlaylist.Id);

    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (!SetProperty(ref _isSelectionMode, value)) return;
            _updatingSelection = true;
            try
            {
                foreach (var row in _rows)
                {
                    row.IsSelectionMode = value;
                    row.IsMarked = false;
                }
            }
            finally
            {
                _updatingSelection = false;
            }
            RaiseInteractionProperties();
        }
    }

    public string SelectionSummary => _text.Format(LanguageManager.Keys.SelectedTrackCount, ActionTracks.Count);
    public string SelectionModeLabel => _text[IsSelectionMode
        ? LanguageManager.Keys.FinishSelection
        : LanguageManager.Keys.ManageTracks];

    public bool InsertAtStart
    {
        get => Context.Settings.Get<LibraryPreferences>().InsertAtStart;
        set => Context.Settings.Apply<LibraryPreferences>(preferences => preferences with { InsertAtStart = value });
    }

    public bool HasPlayingTrack => _hasPlayingTrack;

    public string TrackSummary => _text.Format(LanguageManager.Keys.TrackSummary, Tracks.Count,
        TrackRow.FormatTime(TimeSpan.FromSeconds(Tracks.Sum(t => t.Track.Duration?.TotalSeconds ?? 0))));

    public override string? PageSummary => TrackSummary;
    public AsyncRelayCommand PlayAllCommand { get; }
    public AsyncRelayCommand PlaySelectedCommand { get; }
    public RelayCommand InspectTrackCommand { get; }
    public AsyncRelayCommand<CatalogItem> OpenCatalogItemCommand { get; }
    public AsyncRelayCommand AddToPlaylistCommand { get; }
    public RelayCommand ToggleSelectionCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }

    public TrackRow? RevealPlayingTrack()
    {
        if (!HasPlayingTrack) return null;
        if (!Tracks.Any(t => t.IsCurrent)) Filter = "";
        return Tracks.FirstOrDefault(t => t.IsCurrent);
    }

    public abstract Task PlayAsync(TrackRow? selected = null);
    internal Task InspectTrackAsync(TrackRow row) => CanInteract && Tracks.Contains(row)
        ? Navigation.OpenEditorAsync(row)
        : Task.CompletedTask;

    protected abstract IEnumerable<TrackRow> GetTracks();

    internal override object CaptureNavigationState() => new TrackListNavigationState(Filter,
        SelectedTrack is { } selected ? RowKey(selected) : null, IsSelectionMode,
        IsSelectionMode ? _rows.Where(row => row.IsMarked).Select(RowKey).ToArray() : []);

    internal override void RestoreNavigationState(object? state)
    {
        if (state is not TrackListNavigationState saved) return;
        IsSelectionMode = saved.IsSelectionMode;
        Filter = saved.Filter;
        // Restoring a history entry must not wait for the typing debounce timer.
        ApplyFilter();
        if (saved.IsSelectionMode)
        {
            var marked = saved.MarkedKeys.ToHashSet();
            foreach (var row in _rows) row.IsMarked = marked.Contains(RowKey(row));
        }
        SelectedTrack = saved.SelectedKey is { } key ? Tracks.FirstOrDefault(row => RowKey(row) == key) : null;
    }


    protected virtual void RefreshTracks()
    {
        _needsRefresh = false;
        _filterTimer.Stop();
        var existing = _rows.ToDictionary(RowKey);
        var artwork = _host.Get<ArtworkService>();
        var providers = _host.Get<IProviderRegistry>();
        _rows = GetTracks().Select(row =>
        {
            if (existing.Remove(RowKey(row), out var previous))
            {
                previous.Update(row.Track, row.Number);
                return previous;
            }
            row.ArtworkLoader = artwork;
            row.CanBrowseCatalog = id => providers.Get(id.ProviderId) is ICatalogProvider;
            row.FindCatalogTrack = FindCatalogTrack;
            row.IsSelectionMode = IsSelectionMode;
            row.PropertyChanged += OnRowChanged;
            return row;
        }).ToArray();
        foreach (var removed in existing.Values) removed.PropertyChanged -= OnRowChanged;
        ApplyFilter();
    }

    private MusicItemId? FindCatalogTrack(Track track)
    {
        var registry = _host.Get<IProviderRegistry>();
        if (registry.Get(track.Id.ProviderId) is ICatalogProvider and ITrackLookupProvider) return track.Id;
        foreach (var id in track.ExternalIds.OrderBy(id => id.Kind == "song-mid" ? 0 : 1))
        {
            if (id.Kind is not ("song-mid" or "song-id")) continue;
            var provider = registry.Get(id.Namespace);
            if (provider is ICatalogProvider and ITrackLookupProvider) return new MusicItemId(provider.Id, id.Value);
        }
        return null;
    }

    private void OnFilterTick(object? sender, EventArgs args)
    {
        _filterTimer.Stop();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _filterTimer.Stop();
        var selected = SelectedTrack;
        var filter = Filter.Trim();
        var rows = filter.Length == 0 ? _rows : _rows.Where(t =>
            $"{t.Track.Title} {string.Join(' ', t.Track.Artists.Select(a => a.Name))} {t.Track.Album?.Title}"
                .Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var replaceResults = !string.Equals(_appliedFilter, filter, StringComparison.Ordinal);
        _appliedFilter = filter;
        _tracks.Update(rows, replaceResults);
        SelectedTrack = selected is not null && Tracks.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(HasTracks));
        OnPropertyChanged(nameof(TrackSummary));
        OnPropertyChanged(nameof(PageSummary));
        RefreshPlayingTracks();
        RaiseInteractionProperties();
    }

    private static string RowKey(TrackRow row)
    {
        return row.PlaylistItemId?.ToString() ?? row.QueueItemId?.ToString() ?? row.Identity;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_updatingSelection && args.PropertyName == nameof(TrackRow.IsMarked)) RaiseInteractionProperties();
    }

    private void SetSelection(bool marked)
    {
        _updatingSelection = true;
        try
        {
            foreach (var row in Tracks) row.IsMarked = marked;
        }
        finally
        {
            _updatingSelection = false;
        }

        RaiseInteractionProperties();
    }

    private void OnPlaybackChanged(object? sender, EventArgs args)
    {
        if (!IsNavigationActive) return;
        RefreshPlayingTracks();
    }

    protected void RefreshPlayingTracks()
    {
        var snapshot = Playback.Snapshot;
        foreach (var row in _rows)
            row.IsCurrent = row.QueueItemId is { } queueId
                ? queueId == snapshot.CurrentQueueItemId
                : row.PlaybackEntry is { } entry && snapshot.CurrentPlaylistEntry is { } playingEntry
                    ? entry == playingEntry
                    : PlayingTrackMatcher.Matches(row.Track, snapshot.CurrentTrack, snapshot.SourceDetails?.SourceId);
        SetProperty(ref _hasPlayingTrack,
            _rows.Any(row => row.IsCurrent),
            nameof(HasPlayingTrack));
    }

    public Task<bool> AddSelectedTracksAsync()
    {
        return CanAddToPlaylist && TargetPlaylist is { } target
            ? AddTracksToPlaylistAsync(target.Id, ActionTracks.Select(row => row.Track).ToArray())
            : Task.FromResult(false);
    }

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        OnPropertyChanged(nameof(EditablePlaylists));
        if (!IsNavigationActive)
        {
            MarkTracksDirty();
            return;
        }
        TargetPlaylist = EditablePlaylists.FirstOrDefault(p => p.Id == TargetPlaylist?.Id) ?? EditablePlaylists.FirstOrDefault();
        RefreshTracks();
        base.RefreshLocalizedProperties();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!IsNavigationActive) return;
        if (args.PropertyName == nameof(Playback.CanInteract)) OnPropertyChanged(nameof(CanPlay));
    }

    public override Task ActivateAsync()
    {
        TargetPlaylist = EditablePlaylists.FirstOrDefault(p => p.Id == TargetPlaylist?.Id) ?? EditablePlaylists.FirstOrDefault();
        if (_needsRefresh) RefreshTracks();
        else RefreshPlayingTracks();
        return Task.CompletedTask;
    }

    protected void MarkTracksDirty() => _needsRefresh = true;

    private sealed record TrackListNavigationState(string Filter, string? SelectedKey, bool IsSelectionMode,
        string[] MarkedKeys);

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        foreach (var name in new[]
                 {
                     nameof(CanPlay), nameof(CanInspectTrack), nameof(CanAddToPlaylist), nameof(HasActionTracks),
                     nameof(SelectionSummary), nameof(SelectionModeLabel)
                 }) OnPropertyChanged(name);
    }

    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        foreach (var row in _rows) row.RefreshLocalizedProperties();
        ApplyFilter();
    }

    public override async ValueTask DisposeAsync()
    {
        Context.LibraryChanged -= OnLibraryChanged;
        Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        Playback.MetadataChanged -= OnPlaybackChanged;
        _preferencesSubscription.Dispose();
        _filterTimer.Stop();
        _filterTimer.Tick -= OnFilterTick;
        foreach (var row in _rows) row.PropertyChanged -= OnRowChanged;
        await base.DisposeAsync();
    }
}
