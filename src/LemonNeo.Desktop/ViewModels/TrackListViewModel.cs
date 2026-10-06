using LemonNeo.Application.Library;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Infrastructure;

namespace LemonNeo.Desktop.ViewModels;

public abstract class TrackListViewModel : PageViewModel
{
    protected readonly DesktopNavigation Navigation;
    protected readonly PlaybackViewModel Playback;
    private TrackRow? _selectedTrack;
    private Playlist? _targetPlaylist;
    private string _filter = "";
    protected TrackListViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback, string route) : base(context, route)
    {
        Navigation = navigation; Playback = playback;
        PlayAllCommand = new(() => PlayAsync(), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PlaySelectedCommand = new(() => PlayAsync(SelectedTrack), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        InspectTrackCommand = new(() => { if (CanInspectTrack && SelectedTrack is { } row) _ = Navigation.OpenEditorAsync(row); });
        AddToPlaylistCommand = new(() => SelectedTrack is { } row && TargetPlaylist is { } target ? AddTrackAsync(target.Id, row.Track) : Task.CompletedTask);
        Context.LibraryChanged += OnLibraryChanged;
        Playback.PropertyChanged += OnPlaybackPropertyChanged;
        _targetPlaylist = Playlists.FirstOrDefault();
    }
    public ObservableCollection<Playlist> Playlists => Context.Playlists;
    public ObservableCollection<TrackRow> Tracks { get; } = [];
    public TrackRow? SelectedTrack { get => _selectedTrack; set { if (SetProperty(ref _selectedTrack, value)) RaiseInteractionProperties(); } }
    public Playlist? TargetPlaylist { get => _targetPlaylist; set { if (SetProperty(ref _targetPlaylist, value)) RaiseInteractionProperties(); } }
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value)) RefreshTracks(); } }
    public bool HasTracks => Tracks.Count > 0;
    public bool CanPlay => CanInteract && Playback.CanInteract && HasTracks;
    public bool CanInspectTrack => CanInteract && SelectedTrack is not null;
    public bool CanAddToPlaylist => CanInspectTrack && TargetPlaylist is not null;
    public string TrackSummary => _text.Format("TrackSummary", Tracks.Count,
        TrackRow.FormatTime(TimeSpan.FromSeconds(Tracks.Sum(t => t.Track.Duration?.TotalSeconds ?? 0))));
    public override string? PageSummary => TrackSummary;
    public AsyncRelayCommand PlayAllCommand { get; }
    public AsyncRelayCommand PlaySelectedCommand { get; }
    public RelayCommand InspectTrackCommand { get; }
    public AsyncRelayCommand AddToPlaylistCommand { get; }
    public abstract Task PlayAsync(TrackRow? selected = null);
    protected abstract IEnumerable<TrackRow> GetTracks();
    protected virtual void RefreshTracks()
    {
        var selected = SelectedTrack;
        var values = GetTracks();
        if (!string.IsNullOrWhiteSpace(Filter))
            values = values.Where(t => $"{t.Track.Title} {string.Join(' ', t.Track.Artists.Select(a => a.Name))} {t.Track.Album?.Title}"
                .Contains(Filter.Trim(), StringComparison.CurrentCultureIgnoreCase));
        Tracks.Clear();
        var artwork = _host.Get<ArtworkService>();
        foreach (var track in values) Tracks.Add(track with { ArtworkLoader = artwork });
        SelectedTrack = Tracks.FirstOrDefault(t => selected?.PlaylistItemId is { } itemId ? t.PlaylistItemId == itemId
            : selected?.QueueItemId is { } queueId ? t.QueueItemId == queueId : t.Track.Id == selected?.Track.Id);
        OnPropertyChanged(nameof(HasTracks)); OnPropertyChanged(nameof(TrackSummary)); OnPropertyChanged(nameof(PageSummary));
        RaiseInteractionProperties();
    }
    public Task AddTrackAsync(Guid playlistId, Track track) => ExecuteAsync(async ct =>
    {
        _host.Get<PlaylistService>().Add(playlistId, track);
        await _host.Get<LibraryService>().SaveAsync(ct);
        Context.RefreshLibrary(); SetStatus("TrackAdded");
    });
    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        TargetPlaylist = Playlists.FirstOrDefault(p => p.Id == TargetPlaylist?.Id) ?? Playlists.FirstOrDefault();
        RefreshTracks(); RefreshLocalizedProperties();
    }
    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Playback.CanInteract)) OnPropertyChanged(nameof(CanPlay));
    }
    public override Task ActivateAsync() { RefreshTracks(); return Task.CompletedTask; }
    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        foreach (var name in new[] { nameof(CanPlay), nameof(CanInspectTrack), nameof(CanAddToPlaylist) }) OnPropertyChanged(name);
    }
    protected override void RefreshLocalizedProperties() { base.RefreshLocalizedProperties(); RefreshTracks(); }
    public override async ValueTask DisposeAsync()
    {
        Context.LibraryChanged -= OnLibraryChanged;
        Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        await base.DisposeAsync();
    }
}
