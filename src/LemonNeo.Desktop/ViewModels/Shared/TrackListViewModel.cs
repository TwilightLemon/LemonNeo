using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    protected readonly PlaybackViewModel Playback;
    private readonly IDisposable _preferencesSubscription;
    private string _filter = "";
    private bool _hasPlayingTrack;
    private bool _isSelectionMode;
    private ExternalMusicId? _lastPlayingId;
    private TrackRow? _selectedTrack;
    private Playlist? _targetPlaylist;
    private bool _updatingSelection;

    protected TrackListViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback,
        string route) : base(context, route)
    {
        Navigation = navigation;
        Playback = playback;
        PlayAllCommand = new AsyncRelayCommand(() => PlayAsync(), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PlaySelectedCommand = new AsyncRelayCommand(() => PlayAsync(SelectedTrack),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        InspectTrackCommand = new RelayCommand(() =>
        {
            if (CanInspectTrack && SelectedTrack is { } row) _ = Navigation.OpenEditorAsync(row);
        });
        AddToPlaylistCommand = new AsyncRelayCommand(async () => await AddSelectedTracksAsync());
        ToggleSelectionCommand = new RelayCommand(() => IsSelectionMode = !IsSelectionMode);
        SelectAllCommand = new RelayCommand(() => SetSelection(true));
        ClearSelectionCommand = new RelayCommand(() => SetSelection(false));
        Context.LibraryChanged += OnLibraryChanged;
        Playback.PropertyChanged += OnPlaybackPropertyChanged;
        Playback.Changed += OnPlaybackChanged;
        _preferencesSubscription =
            Context.Settings.Subscribe<LibraryPreferences>(_ => OnPropertyChanged(nameof(InsertAtStart)));
        _targetPlaylist = Playlists.FirstOrDefault();
    }

    public ObservableCollection<Playlist> Playlists => Context.Playlists;
    public ObservableCollection<TrackRow> Tracks { get; } = [];

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
            if (SetProperty(ref _filter, value)) RefreshTracks();
        }
    }

    public bool HasTracks => Tracks.Count > 0;
    public bool CanPlay => CanInteract && Playback.CanInteract && HasTracks;
    public bool CanInspectTrack => CanInteract && SelectedTrack is not null && !IsSelectionMode;

    public IReadOnlyList<TrackRow> ActionTracks => IsSelectionMode ? Tracks.Where(t => t.IsMarked).ToArray()
        : SelectedTrack is { } row ? [row] : [];

    public bool HasActionTracks => CanInteract && ActionTracks.Count > 0;
    public bool CanAddToPlaylist => HasActionTracks && TargetPlaylist is not null;

    public bool IsSelectionMode
    {
        get => _isSelectionMode;
        set
        {
            if (!SetProperty(ref _isSelectionMode, value)) return;
            foreach (var row in Tracks) row.IsSelectionMode = value;
            SetSelection(false);
        }
    }

    public string SelectionSummary => _text.Format("SelectedTrackCount", ActionTracks.Count);
    public string SelectionModeLabel => _text[IsSelectionMode ? "FinishSelection" : "ManageTracks"];

    public bool InsertAtStart
    {
        get => Context.Settings.Get<LibraryPreferences>().InsertAtStart;
        set => Context.Settings.Apply<LibraryPreferences>(preferences => preferences with { InsertAtStart = value });
    }

    public bool HasPlayingTrack => _hasPlayingTrack;

    public string TrackSummary => _text.Format("TrackSummary", Tracks.Count,
        TrackRow.FormatTime(TimeSpan.FromSeconds(Tracks.Sum(t => t.Track.Duration?.TotalSeconds ?? 0))));

    public override string? PageSummary => TrackSummary;
    public AsyncRelayCommand PlayAllCommand { get; }
    public AsyncRelayCommand PlaySelectedCommand { get; }
    public RelayCommand InspectTrackCommand { get; }
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
    protected abstract IEnumerable<TrackRow> GetTracks();

    protected virtual void RefreshTracks()
    {
        var selected = SelectedTrack;
        var marked = Tracks.Where(t => t.IsMarked).Select(RowKey).ToHashSet();
        var values = GetTracks();
        if (!string.IsNullOrWhiteSpace(Filter))
            values = values.Where(t =>
                $"{t.Track.Title} {string.Join(' ', t.Track.Artists.Select(a => a.Name))} {t.Track.Album?.Title}"
                    .Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase));
        var rows = values.ToArray();
        foreach (var row in Tracks) row.PropertyChanged -= OnRowChanged;
        Tracks.Clear();
        var artwork = _host.Get<ArtworkService>();
        foreach (var row in rows)
        {
            row.ArtworkLoader = artwork;
            row.IsSelectionMode = IsSelectionMode;
            row.IsMarked = marked.Contains(RowKey(row));
            row.PropertyChanged += OnRowChanged;
            Tracks.Add(row);
        }

        SelectedTrack = Tracks.FirstOrDefault(t => selected?.PlaylistItemId is { } itemId ? t.PlaylistItemId == itemId
            : selected?.QueueItemId is { } queueId ? t.QueueItemId == queueId : t.Track.Id == selected?.Track.Id);
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
        if (!Equals(_lastPlayingId, Playback.Snapshot.SourceDetails?.SourceId)) RefreshPlayingTracks();
    }

    protected void RefreshPlayingTracks()
    {
        var source = Playback.Snapshot.SourceDetails?.SourceId;
        _lastPlayingId = source;
        foreach (var row in Tracks) row.IsCurrent = PlayingTrackMatcher.Matches(row.Track, source);
        SetProperty(ref _hasPlayingTrack,
            source is not null && GetTracks().Any(row => PlayingTrackMatcher.Matches(row.Track, source)),
            nameof(HasPlayingTrack));
    }

    public async Task<bool> AddSelectedTracksAsync()
    {
        if (!CanAddToPlaylist || TargetPlaylist is not { } target) return false;
        var tracks = ActionTracks.Select(row => row.Track).ToArray();
        var insertAtStart = InsertAtStart;
        var succeeded = false;
        await ExecuteAsync(async ct =>
        {
            await _host.Settings.SaveAsync(ct);
            var playlists = _host.Get<PlaylistService>();
            var added = playlists.AddRange(target.Id, tracks, insertAtStart);
            try
            {
                await _host.Get<LibraryService>().SaveAsync(ct);
            }
            catch
            {
                foreach (var item in added) playlists.Remove(target.Id, item.Id);
                Context.RefreshLibrary();
                throw;
            }

            Context.RefreshLibrary();
            SetStatus("TracksAdded", tracks.Length, target.Name);
            succeeded = true;
        });
        return succeeded;
    }

    public Task AddTrackAsync(Guid playlistId, Track track)
    {
        return ExecuteAsync(async ct =>
        {
            _host.Get<PlaylistService>().AddRange(playlistId, [track], InsertAtStart);
            await _host.Get<LibraryService>().SaveAsync(ct);
            Context.RefreshLibrary();
            SetStatus("TrackAdded");
        });
    }

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        TargetPlaylist = Playlists.FirstOrDefault(p => p.Id == TargetPlaylist?.Id) ?? Playlists.FirstOrDefault();
        RefreshLocalizedProperties();
    }

    private void OnPlaybackPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Playback.CanInteract)) OnPropertyChanged(nameof(CanPlay));
    }

    public override Task ActivateAsync()
    {
        RefreshTracks();
        return Task.CompletedTask;
    }

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
        RefreshTracks();
    }

    public override async ValueTask DisposeAsync()
    {
        Context.LibraryChanged -= OnLibraryChanged;
        Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        Playback.Changed -= OnPlaybackChanged;
        _preferencesSubscription.Dispose();
        foreach (var row in Tracks) row.PropertyChanged -= OnRowChanged;
        await base.DisposeAsync();
    }
}