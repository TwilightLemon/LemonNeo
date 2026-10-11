using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Accounts;
using LemonNeo.Desktop.ViewModels.Library;
using LemonNeo.Desktop.ViewModels.Lyrics;
using LemonNeo.Desktop.ViewModels.Metadata;
using LemonNeo.Desktop.ViewModels.Online;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Search;
using LemonNeo.Desktop.ViewModels.Settings;
using LemonNeo.Desktop.ViewModels.Shared;
using LemonNeo.Desktop.ViewModels.Tools;

namespace LemonNeo.Desktop.ViewModels.Shell;

/// <summary>The shell presents startup state and owns navigation; the controller owns startup execution.</summary>
public sealed class MainViewModel : PageViewModel
{
    private bool _isQueueOpen;
    private PlaybackQueueViewModel? _queueDrawer;
    private SearchViewModel? _searchPage;
    private string _searchQuery = "";
    private PlaylistMenuItemViewModel? _selectedPlaylist;

    public MainViewModel(DesktopHost host, LocalizationService text, AppearanceService appearance,
        ArtworkService artwork)
        : base(new DesktopContext(host, text, appearance), "Library")
    {
        Playback = new PlaybackViewModel(Context, artwork);
        Navigation = new DesktopNavigation(CreatePage, Context, Playback);
        Navigation.PropertyChanged += OnNavigationChanged;
        Navigation.Initialize();
        SearchCommand = new AsyncRelayCommand(SubmitSearchAsync, () => CanSearch);
        ToggleQueueCommand = new RelayCommand(ToggleQueue);
        CloseQueueCommand = new RelayCommand(() => IsQueueOpen = false, () => IsQueueOpen);
        Context.LibraryChanged += OnLibraryChanged;
    }

    public DesktopContext SharedContext => Context;
    public PlaybackViewModel Playback { get; }
    public DesktopNavigation Navigation { get; }
    public ObservableCollection<PlaylistMenuItemViewModel> Playlists { get; } = [];

    public PlaylistMenuItemViewModel? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            SetProperty(ref _selectedPlaylist, value);
            if (value is not null && (Navigation.Current is not LibraryViewModel { IsLibrary: true } library ||
                                      library.SelectedPlaylist?.Id != value.Id))
                _ = Navigation.NavigateAsync(DesktopRoutes.Library, value.Id);
        }
    }

    public LibraryViewModel Library =>
        Navigation.GetPage<LibraryViewModel>(DesktopRoutes.Library, _selectedPlaylist?.Id);

    public LibraryViewModel Queue => Navigation.GetPage<LibraryViewModel>(DesktopRoutes.Queue);
    public SearchViewModel Search => Navigation.GetPage<SearchViewModel>(DesktopRoutes.Search);
    public OnlineViewModel Online => Navigation.GetPage<OnlineViewModel>(DesktopRoutes.Online);
    public AccountsViewModel Accounts => Navigation.GetPage<AccountsViewModel>(DesktopRoutes.Accounts);
    public TrackEditorPageViewModel EditorPage => Navigation.GetPage<TrackEditorPageViewModel>(DesktopRoutes.Editor);
    public ToolsViewModel Tools => Navigation.GetPage<ToolsViewModel>(DesktopRoutes.Tools);
    public SettingsViewModel Settings => Navigation.GetPage<SettingsViewModel>(DesktopRoutes.Settings);
    public LyricsViewModel Lyrics => Navigation.GetPage<LyricsViewModel>(DesktopRoutes.Lyrics);

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value)) return;
            OnPropertyChanged(nameof(CanSearch));
            SearchCommand.NotifyCanExecuteChanged();
        }
    }

    public bool CanSearch => Context.IsInitialized && !string.IsNullOrWhiteSpace(SearchQuery) &&
                             _searchPage?.CanInteract != false;

    public AsyncRelayCommand SearchCommand { get; }

    public PlaybackQueueViewModel? QueueDrawer
    {
        get => _queueDrawer;
        private set => SetProperty(ref _queueDrawer, value);
    }

    public bool IsQueueOpen
    {
        get => _isQueueOpen;
        set
        {
            if (SetProperty(ref _isQueueOpen, value)) CloseQueueCommand.NotifyCanExecuteChanged();
        }
    }

    public RelayCommand ToggleQueueCommand { get; }
    public RelayCommand CloseQueueCommand { get; }

    private void ToggleQueue()
    {
        if (!IsQueueOpen)
        {
            QueueDrawer ??= new PlaybackQueueViewModel(Context, Playback);
            _ = QueueDrawer.ActivateAsync();
        }

        IsQueueOpen = !IsQueueOpen;
    }

    private async Task SubmitSearchAsync()
    {
        if (!CanSearch) return;
        var query = SearchQuery.Trim();
        var page = Search;
        if (!page.CanInteract) return;
        page.SearchQuery = query;
        await Navigation.NavigateAsync(DesktopRoutes.Search);
        await page.SearchAsync();
    }

    private SearchViewModel CreateSearchPage()
    {
        _searchPage = new SearchViewModel(Context, Navigation, Playback);
        _searchPage.PropertyChanged += OnSearchChanged;
        return _searchPage;
    }

    private void OnSearchChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SearchViewModel.SearchQuery) &&
            ReferenceEquals(Navigation.Current, _searchPage))
            SearchQuery = _searchPage!.SearchQuery;
        if (args.PropertyName == nameof(CanInteract))
        {
            OnPropertyChanged(nameof(CanSearch));
            SearchCommand.NotifyCanExecuteChanged();
        }
    }

    protected override void RaiseInteractionProperties()
    {
        base.RaiseInteractionProperties();
        OnPropertyChanged(nameof(CanSearch));
        SearchCommand.NotifyCanExecuteChanged();
    }

    private PageViewModel CreatePage(string route, Guid? playlist)
    {
        return route switch
        {
            DesktopRoutes.Library or DesktopRoutes.Queue => new LibraryViewModel(Context, Navigation, Playback, route,
                playlist),
            DesktopRoutes.Search => CreateSearchPage(),
            DesktopRoutes.Online => new OnlineViewModel(Context),
            DesktopRoutes.Accounts => new AccountsViewModel(Context),
            DesktopRoutes.Editor => new TrackEditorPageViewModel(Context, Navigation, Playback, null),
            DesktopRoutes.MatchPlaylist => new MatchPlaylistPageViewModel(Context, Navigation,
                playlist ?? throw new ArgumentException("A playlist is required.")),
            DesktopRoutes.Tools => new ToolsViewModel(Context),
            DesktopRoutes.Settings => new SettingsViewModel(Context),
            DesktopRoutes.Lyrics => new LyricsViewModel(Context, Playback),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown desktop route.")
        };
    }

    private void OnNavigationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(DesktopNavigation.Current)) return;
        IsQueueOpen = false;
        SyncPlaylistSelection();
        if (Navigation.Current is SearchViewModel search) SearchQuery = search.SearchQuery;
    }

    private void SyncPlaylistSelection()
    {
        var playlistId = Navigation.Current is LibraryViewModel { IsLibrary: true } library
            ? library.SelectedPlaylist?.Id
            : null;
        SetProperty(ref _selectedPlaylist, Playlists.FirstOrDefault(p => p.Id == playlistId), nameof(SelectedPlaylist));
    }

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        var values = Context.Playlists;
        for (var i = Playlists.Count - 1; i >= 0; i--)
            if (!values.Any(p => p.Id == Playlists[i].Id))
                Playlists.RemoveAt(i);
        for (var i = 0; i < values.Count; i++)
        {
            var existing = Playlists.FirstOrDefault(p => p.Id == values[i].Id);
            if (existing is null)
            {
                Playlists.Insert(i, new PlaylistMenuItemViewModel(values[i]));
            }
            else
            {
                existing.Update(values[i]);
                var index = Playlists.IndexOf(existing);
                if (index != i) Playlists.Move(index, i);
            }
        }

        SyncPlaylistSelection();
    }

    public Task ReorderPlaylistsAsync(PlaylistMenuItemViewModel source, PlaylistMenuItemViewModel target, bool after)
    {
        if (!CanInteract || source.Id == target.Id) return Task.CompletedTask;
        return ExecuteAsync(async ct =>
        {
            var ids = Context.Playlists.Select(p => p.Id).ToList();
            if (!ids.Contains(target.Id) || !ids.Remove(source.Id)) return;
            ids.Insert(ids.IndexOf(target.Id) + (after ? 1 : 0), source.Id);
            Context.Settings.Apply<LibraryPreferences>(preferences =>
                preferences with { PlaylistOrder = ids.ToArray() });
            await _host.Settings.SaveAsync(ct);
            Context.RefreshLibrary();
        });
    }

    internal void BeginStartup()
    {
        SetError(null);
        SetStatus("Loading");
        IsBusy = true;
        RaiseInteractionProperties();
    }

    internal async Task CompleteStartupAsync()
    {
        Context.IsInitialized = true;
        Context.RefreshLibrary();
        var report = _host.Plugins.LastReport;
        if (report.Failures.Count > 0)
            SetError("PluginFailures", [report.Failures.Count],
                string.Join(Environment.NewLine, report.Failures.Select(f => f.Error)));
        Playback.UpdateSnapshot();
        await Navigation.Current.ActivateAsync();
        SetStatus("Ready");
        IsBusy = false;
        RaiseInteractionProperties();
    }

    internal void ReportStartupFailure(string message)
    {
        SetError("StartupFailed", [message]);
        SetStatus("Ready");
        IsBusy = false;
        RaiseInteractionProperties();
    }

    public override async ValueTask DisposeAsync()
    {
        Context.LibraryChanged -= OnLibraryChanged;
        Navigation.PropertyChanged -= OnNavigationChanged;
        if (_searchPage is not null) _searchPage.PropertyChanged -= OnSearchChanged;
        await base.DisposeAsync();
        IsQueueOpen = false;
        if (QueueDrawer is not null) await QueueDrawer.DisposeAsync();
        await Navigation.DisposeAsync();
        await Playback.DisposeAsync();
    }
}