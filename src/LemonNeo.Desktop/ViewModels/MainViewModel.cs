using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Plugin.Runtime;

namespace LemonNeo.Desktop.ViewModels;

/// <summary>The shell owns startup and navigation; feature operations belong to their pages.</summary>
public sealed class MainViewModel : PageViewModel
{
    private Playlist? _selectedPlaylist;
    private string _searchQuery = "";
    private SearchViewModel? _searchPage;
    private PlaybackQueueViewModel? _queueDrawer;
    private bool _isQueueOpen;
    public MainViewModel(DesktopRuntime runtime, LocalizationService text, AppearanceService appearance, ArtworkService artwork)
        : base(new DesktopContext(runtime, text, appearance), "Library")
    {
        Playback = new(Context, artwork);
        Navigation = new(CreatePage, Context, Playback);
        Navigation.PropertyChanged += OnNavigationChanged;
        Navigation.Initialize();
        RefreshCommand = new(InitializeAsync);
        SearchCommand = new(SubmitSearchAsync, () => CanSearch);
        ToggleQueueCommand = new(ToggleQueue);
        CloseQueueCommand = new(() => IsQueueOpen = false, () => IsQueueOpen);
    }
    public DesktopContext SharedContext => Context;
    public PlaybackViewModel Playback { get; }
    public DesktopNavigation Navigation { get; }
    public ObservableCollection<Playlist> Playlists => Context.Playlists;
    public Playlist? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            SetProperty(ref _selectedPlaylist, value);
            if (value is not null && (Navigation.Current is not LibraryViewModel { IsLibrary: true } library || library.SelectedPlaylist?.Id != value.Id))
                _ = Navigation.NavigateAsync(DesktopRoutes.Library, value.Id);
        }
    }
    public LibraryViewModel Library => Navigation.GetPage<LibraryViewModel>(DesktopRoutes.Library, _selectedPlaylist?.Id);
    public LibraryViewModel Queue => Navigation.GetPage<LibraryViewModel>(DesktopRoutes.Queue);
    public SearchViewModel Search => Navigation.GetPage<SearchViewModel>(DesktopRoutes.Search);
    public OnlineViewModel Online => Navigation.GetPage<OnlineViewModel>(DesktopRoutes.Online);
    public AccountsViewModel Accounts => Navigation.GetPage<AccountsViewModel>(DesktopRoutes.Accounts);
    public TrackEditorPageViewModel EditorPage => Navigation.GetPage<TrackEditorPageViewModel>(DesktopRoutes.Editor);
    public ToolsViewModel Tools => Navigation.GetPage<ToolsViewModel>(DesktopRoutes.Tools);
    public SettingsViewModel Settings => Navigation.GetPage<SettingsViewModel>(DesktopRoutes.Settings);
    public LyricsViewModel Lyrics => Navigation.GetPage<LyricsViewModel>(DesktopRoutes.Lyrics);
    public bool CanRetryStartup => !Context.IsInitialized;
    public AsyncRelayCommand RefreshCommand { get; }
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
    public bool CanSearch => Context.IsInitialized && !string.IsNullOrWhiteSpace(SearchQuery) && _searchPage?.IsBusy != true;
    public AsyncRelayCommand SearchCommand { get; }
    public PlaybackQueueViewModel? QueueDrawer { get => _queueDrawer; private set => SetProperty(ref _queueDrawer, value); }
    public bool IsQueueOpen
    {
        get => _isQueueOpen;
        set { if (SetProperty(ref _isQueueOpen, value)) CloseQueueCommand.NotifyCanExecuteChanged(); }
    }
    public RelayCommand ToggleQueueCommand { get; }
    public RelayCommand CloseQueueCommand { get; }
    private void ToggleQueue()
    {
        if (!IsQueueOpen)
        {
            QueueDrawer ??= new(Context, Playback);
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
        _searchPage = new(Context, Navigation, Playback);
        _searchPage.PropertyChanged += OnSearchChanged;
        return _searchPage;
    }

    private void OnSearchChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SearchViewModel.SearchQuery) && ReferenceEquals(Navigation.Current, _searchPage))
            SearchQuery = _searchPage!.SearchQuery;
        if (args.PropertyName == nameof(SearchViewModel.IsBusy))
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

    private PageViewModel CreatePage(string route, Guid? playlist) => route switch
    {
        DesktopRoutes.Library or DesktopRoutes.Queue => new LibraryViewModel(Context, Navigation, Playback, route, playlist),
        DesktopRoutes.Search => CreateSearchPage(),
        DesktopRoutes.Online => new OnlineViewModel(Context),
        DesktopRoutes.Accounts => new AccountsViewModel(Context),
        DesktopRoutes.Editor => new TrackEditorPageViewModel(Context, Navigation, Playback, null),
        DesktopRoutes.MatchPlaylist => new MatchPlaylistPageViewModel(Context, Navigation, playlist ?? throw new ArgumentException("A playlist is required.")),
        DesktopRoutes.Tools => new ToolsViewModel(Context, Navigation),
        DesktopRoutes.Settings => new SettingsViewModel(Context),
        DesktopRoutes.Lyrics => new LyricsViewModel(Context, Playback),
        DesktopRoutes.Components => new ComponentsViewModel(Context),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown desktop route.")
    };

    private void OnNavigationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(DesktopNavigation.Current)) return;
        IsQueueOpen = false;
        if (Navigation.Current is LibraryViewModel { IsLibrary: true } library)
            SetProperty(ref _selectedPlaylist, library.SelectedPlaylist, nameof(SelectedPlaylist));
        if (Navigation.Current is SearchViewModel search) SearchQuery = search.SearchQuery;
    }

    public Task InitializeAsync() => RunOperationAsync(async ct =>
    {
        await Context.Appearance.LoadAsync(ct);
        await Task.Run(() => _runtime.InitializeAsync(ct), ct);
        ct.ThrowIfCancellationRequested();
        Context.IsInitialized = true;
        Context.RefreshLibrary();
        var report = _runtime.Get<PluginLoader>().LastReport;
        if (report.Failures.Count > 0)
            SetError("PluginFailures", [report.Failures.Count], string.Join(Environment.NewLine, report.Failures.Select(f => f.Error)));
        Playback.UpdateSnapshot();
        _ = Navigation.Current.ActivateAsync();
        OnPropertyChanged(nameof(CanRetryStartup));
    }, "Loading", allowUninitialized: true);

    public override async ValueTask DisposeAsync()
    {
        Navigation.PropertyChanged -= OnNavigationChanged;
        if (_searchPage is not null) _searchPage.PropertyChanged -= OnSearchChanged;
        await base.DisposeAsync();
        IsQueueOpen = false;
        if (QueueDrawer is not null) await QueueDrawer.DisposeAsync();
        await Navigation.DisposeAsync();
        await Playback.DisposeAsync();
        if (Context.IsInitialized)
        {
            await _runtime.Get<LibraryService>().SaveAsync();
            await _runtime.Get<PlaybackSessionService>().SaveAsync();
        }
    }
}
