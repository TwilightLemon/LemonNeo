using LemonNeo.Application.Playback;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Search;

public sealed partial class SearchViewModel : TrackListViewModel
{
    private IReadOnlyList<Track> _searchTracks = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSearch))] private string _searchQuery = "";
    public SearchViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback) : base(context, navigation, playback, DesktopRoutes.Search)
    { SearchCommand = new(SearchAsync); }
    public AsyncRelayCommand SearchCommand { get; }
    public bool CanSearch => CanInteract && !string.IsNullOrWhiteSpace(SearchQuery);
    protected override IEnumerable<TrackRow> GetTracks() => _searchTracks.Select((t, index) => new TrackRow(t, index + 1, _text));
    public override Task PlayAsync(TrackRow? selected = null) => (selected ?? SelectedTrack ?? Tracks.FirstOrDefault()) is { } result
        ? Playback.ExecutePlaybackAsync(ct => _host.Get<PlaybackSessionService>().PlayTrackAsync(result.Track, new PlaybackRequest(), ct)) : Task.CompletedTask;
    public Task SearchAsync() => ExecuteAsync(async ct =>
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) throw new ArgumentException(_text["SearchQueryRequired"]);
        var results = await Task.Run(() => _host.Get<SearchService>().SearchForPlaybackAsync(SearchQuery.Trim(), ct), ct);
        ct.ThrowIfCancellationRequested();
        _searchTracks = results.SelectMany(r => r.Tracks).DistinctBy(t => t.Id).ToArray();
        Filter = "";
        RefreshTracks();
        SetStatus("SearchCompleted", Tracks.Count);
    });

    protected override void RaiseInteractionProperties() { base.RaiseInteractionProperties(); OnPropertyChanged(nameof(CanSearch)); }
}
