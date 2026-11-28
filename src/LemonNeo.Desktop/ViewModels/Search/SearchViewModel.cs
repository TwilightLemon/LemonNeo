using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Playback;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Shared;

namespace LemonNeo.Desktop.ViewModels.Search;

public sealed partial class SearchViewModel : TrackListViewModel
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanSearch))]
    private string _searchQuery = "";

    private IReadOnlyList<Track> _searchTracks = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasResults))]
    private IReadOnlyList<CatalogItem> _catalogResults = [];
    [ObservableProperty] private SearchKindOption? _selectedKind;
    [ObservableProperty] private SearchSourceOption? _selectedSource;
    [ObservableProperty] private IReadOnlyList<SearchKindOption> _kinds = [];
    [ObservableProperty] private IReadOnlyList<SearchSourceOption> _sources = [];
    private bool _restoring;

    public SearchViewModel(DesktopContext context, DesktopNavigation navigation, PlaybackViewModel playback) : base(
        context, navigation, playback, DesktopRoutes.Search)
    {
        SearchCommand = new AsyncRelayCommand(SearchAsync);
        OpenResultCommand = new AsyncRelayCommand<CatalogItem>(item =>
            item is null ? Task.CompletedTask : Navigation.OpenCatalogItemAsync(item));
        RefreshOptions();
    }

    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand<CatalogItem> OpenResultCommand { get; }
    public bool CanSearch => CanInteract && !string.IsNullOrWhiteSpace(SearchQuery);
    public bool IsTrackSearch => SelectedKind is null or { Kind: SearchKind.Track };
    public bool IsCatalogSearch => !IsTrackSearch;
    public bool HasResults => IsTrackSearch ? HasTracks : CatalogResults.Count > 0;
    public override string? PageSummary => IsTrackSearch ? base.PageSummary : null;

    public override async Task ActivateAsync()
    {
        RefreshOptions();
        await base.ActivateAsync();
    }

    partial void OnSelectedKindChanged(SearchKindOption? oldValue, SearchKindOption? newValue)
    {
        if (_restoring || newValue is null || oldValue?.Kind == newValue.Kind) return;
        RefreshOptions();
        OnPropertyChanged(nameof(IsTrackSearch));
        OnPropertyChanged(nameof(IsCatalogSearch));
        ClearResults();
    }

    partial void OnSelectedSourceChanged(SearchSourceOption? oldValue, SearchSourceOption? newValue)
    {
        if (_restoring || newValue is null || oldValue?.Scope == newValue.Scope) return;
        var kind = SelectedKind?.Kind ?? SearchKind.Track;
        Context.Settings.Apply<SearchPreferences>(value => kind switch
        {
            SearchKind.Album => value with { Albums = newValue.Scope },
            SearchKind.Artist => value with { Artists = newValue.Scope },
            _ => value with { Tracks = newValue.Scope }
        });
        ClearResults();
    }

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
            var kind = SelectedKind?.Kind ?? SearchKind.Track;
            var scope = SelectedSource?.Scope ?? new SearchScope();
            var results = await _host.Get<SearchService>()
                .SearchOnlineAsync(SearchQuery.Trim(), kind, scope.ProviderId, scope.Namespace, ct);
            ct.ThrowIfCancellationRequested();
            _searchTracks = results.Tracks;
            var artwork = _host.Get<ArtworkService>();
            var registry = _host.Get<IProviderRegistry>();
            CatalogResults = results.Albums.Select(album => new CatalogItem(SearchKind.Album, album.Id, album.Title,
                    album.ArtworkUri, string.Join(" · ", album.Artists.Select(a => a.Name)
                        .Append(registry.Get(album.Id.ProviderId)?.Descriptor.Name).OfType<string>()), artwork))
                .Concat(results.Artists.Select(artist => new CatalogItem(SearchKind.Artist, artist.Id, artist.Name,
                    artist.ArtworkUri, registry.Get(artist.Id.ProviderId)?.Descriptor.Name, artwork))).ToArray();
            Filter = "";
            RefreshTracks();
            if (results.FailedSources.Count > 0)
                Notify(results.AllSourcesFailed ? NotificationLevel.Error : NotificationLevel.Warning,
                    LanguageManager.Keys.SearchSourcesFailed, string.Join(", ", results.FailedSources));
        });
    }

    private void RefreshOptions()
    {
        var kind = SelectedKind?.Kind ?? SearchKind.Track;
        var preferences = Context.Settings.Get<SearchPreferences>();
        var scope = kind switch
        {
            SearchKind.Album => preferences.Albums,
            SearchKind.Artist => preferences.Artists,
            _ => preferences.Tracks
        } ?? new SearchScope();
        var providers = _host.Get<IProviderRegistry>().Providers;
        var sources = new List<SearchSourceOption> { new(new SearchScope(), _text[LanguageManager.Keys.AllSearchSources]) };
        foreach (var provider in providers.Where(provider =>
                     kind == SearchKind.Track ? provider is ISearchProvider : provider is ICatalogProvider)
                 .OrderBy(provider => provider.Descriptor.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            sources.Add(new SearchSourceOption(new SearchScope(provider.Id), provider.Descriptor.Name));
            if (provider is ISearchProvider { SearchNamespaces.Count: > 1 } scoped)
                sources.AddRange(scoped.SearchNamespaces.Order(StringComparer.OrdinalIgnoreCase)
                    .Select(ns => new SearchSourceOption(new SearchScope(provider.Id, ns), $"{provider.Descriptor.Name} / {ns}")));
        }
        _restoring = true;
        try
        {
            Kinds =
            [
                new(SearchKind.Track, _text[LanguageManager.Keys.SearchTracks]),
                new(SearchKind.Album, _text[LanguageManager.Keys.SearchAlbums]),
                new(SearchKind.Artist, _text[LanguageManager.Keys.SearchArtists])
            ];
            SelectedKind = Kinds.First(option => option.Kind == kind);
            Sources = sources;
            SelectedSource = sources.FirstOrDefault(source => source.Scope == scope) ?? sources[0];
        }
        finally
        {
            _restoring = false;
        }
    }

    private void ClearResults()
    {
        _searchTracks = [];
        CatalogResults = [];
        IsSelectionMode = false;
        RefreshTracks();
    }

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        OnPropertyChanged(nameof(CanSearch));
        OnPropertyChanged(nameof(HasResults));
    }

    protected override void RefreshLocalizedProperties()
    {
        base.RefreshLocalizedProperties();
        RefreshOptions();
    }
}

public sealed record SearchKindOption(SearchKind Kind, string Name);
public sealed record SearchSourceOption(SearchScope Scope, string Name);
