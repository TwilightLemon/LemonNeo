using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application.Playback;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Search;

public sealed partial class SearchViewModel : TrackListViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSearch))]
    private string _searchQuery = "";

    private IReadOnlyList<Track> _searchTracks = [];

    public SearchViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback) : base(
        context, navigation, playback, DesktopRoutes.Search)
    {
        SearchCommand = new AsyncRelayCommand(SearchAsync);
    }

    public AsyncRelayCommand SearchCommand { get; }
    public bool CanSearch => CanInteract && !string.IsNullOrWhiteSpace(SearchQuery);

    protected override IEnumerable<TrackRow> GetTracks()
    {
        return _searchTracks.Select((t, index) => new TrackRow(t, index + 1, _text));
    }

    public override Task PlayAsync(TrackRow? selected = null)
    {
        return (selected ?? SelectedTrack ?? Tracks.FirstOrDefault()) is { } result
            ? Playback.ExecutePlaybackAsync(ct =>
                _host.Get<PlaybackSessionService>().PlayTrackAsync(result.Track, cancellationToken: ct))
            : Task.CompletedTask;
    }

    public Task SearchAsync()
    {
        return ExecuteAsync(async ct =>
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) throw new ArgumentException(_text[LanguageManager.Keys.SearchQueryRequired]);
            var results =
                await Task.Run(() => _host.Get<SearchService>().SearchForPlaybackAsync(SearchQuery.Trim(), ct), ct);
            ct.ThrowIfCancellationRequested();
            _searchTracks = results.SelectMany(r => r.Tracks).DistinctBy(t => t.Id).ToArray();
            Filter = "";
            RefreshTracks();
            SetStatus(LanguageManager.Keys.SearchCompleted, Tracks.Count);
        });
    }

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        OnPropertyChanged(nameof(CanSearch));
    }
}