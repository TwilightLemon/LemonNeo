using LemonNeo.Core.Library;
using LemonNeo.Core.Lyrics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;

namespace LemonNeo.Desktop.Infrastructure;

public static class DesktopRoutes
{
    public const string Library = "Library", Search = "Search", Accounts = "Accounts", Online = "Online", Queue = "Queue", Lyrics = "Lyrics",
        Editor = "Editor", MatchPlaylist = "MatchPlaylist", Tools = "Tools", Settings = "Settings", Components = "Components";
    public static IReadOnlyList<string> All { get; } = [Library, Search, Accounts, Online, Queue, Lyrics, Editor, MatchPlaylist, Tools, Settings, Components];
}

public sealed class DesktopNavigation : ObservableObject, IAsyncDisposable
{
    private readonly Func<string, Guid?, PageViewModel> _factory;
    private readonly AppearanceService _appearance;
    private readonly DesktopContext _context;
    private readonly PlaybackViewModel _playback;
    private readonly Dictionary<(string Route, Guid? Playlist), PageViewModel> _pages = [];
    private readonly List<PageViewModel> _ownedPages = [];
    private readonly List<Task> _retiredPages = [];
    private readonly Stack<ContentPage> _forward = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PageViewModel _current = null!;
    private bool _isNavigating;
    private bool _disposed;

    public DesktopNavigation(Func<string, Guid?, PageViewModel> factory, DesktopContext context, PlaybackViewModel playback)
    {
        _factory = factory; _context = context; _appearance = context.Appearance; _playback = playback;
        Host = new NavigationPage { IsGestureEnabled = false, IsBackButtonVisible = false, ClipToBounds = true, Background = Brushes.Transparent };
        NavigateCommand = new(route => NavigateAsync(route ?? DesktopRoutes.Library), AsyncRelayCommandOptions.AllowConcurrentExecutions);
        BackCommand = new(GoBackAsync, () => CanGoBack);
        ForwardCommand = new(GoForwardAsync, () => CanGoForward);
    }

    public NavigationPage Host { get; }
    public PageViewModel Current { get => _current; private set => SetProperty(ref _current, value); }
    public int LoadedPageCount => _ownedPages.Count;
    public bool IsNavigating { get => _isNavigating; private set => SetProperty(ref _isNavigating, value); }
    public bool CanGoBack => !IsNavigating && Host.CanGoBack;
    public bool CanGoForward => !IsNavigating && _forward.Count > 0;
    public bool IsLibrary => Current.Route == DesktopRoutes.Library;
    public bool IsSearch => Current.Route == DesktopRoutes.Search;
    public bool IsAccounts => Current.Route == DesktopRoutes.Accounts;
    public bool IsOnline => Current.Route == DesktopRoutes.Online;
    public bool IsQueue => Current.Route == DesktopRoutes.Queue;
    public bool IsLyrics => Current.Route == DesktopRoutes.Lyrics;
    public bool IsTools => Current.Route == DesktopRoutes.Tools;
    public bool IsSettings => Current.Route == DesktopRoutes.Settings;
    public AsyncRelayCommand<string> NavigateCommand { get; }
    public AsyncRelayCommand BackCommand { get; }
    public AsyncRelayCommand ForwardCommand { get; }

    public T GetPage<T>(string route, Guid? playlist = null) where T : PageViewModel
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = (route, playlist);
        if (!_pages.TryGetValue(key, out var page))
        {
            page = _factory(route, playlist);
            _pages.Add(key, page); _ownedPages.Add(page);
            OnPropertyChanged(nameof(LoadedPageCount));
        }
        return (T)page;
    }

    public void Initialize()
    {
        Current = GetPage<LibraryViewModel>(DesktopRoutes.Library);
        Host.Content = CreatePage(Current);
        RaiseNavigationProperties();
    }

    public Task NavigateAsync(string route, Guid? playlist = null) => NavigateToAsync(GetPage<PageViewModel>(route, playlist));

    public Task OpenEditorAsync(TrackRow row, bool isNew = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var editor = new TrackEditorPageViewModel(_context, this, _playback, row, isNew);
        _pages[(DesktopRoutes.Editor, null)] = editor;
        _ownedPages.Add(editor);
        OnPropertyChanged(nameof(LoadedPageCount));
        return NavigateToAsync(editor);
    }

    private async Task NavigateToAsync(PageViewModel page)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || ReferenceEquals(Current, page)) return;
            IsNavigating = true;
            _forward.Clear();
            Current = page;
            RaiseNavigationProperties();
            await Host.PushAsync(CreatePage(page), Transition());
            ReleaseAbandonedPages();
            // Page loading owns its own operation. Navigation never waits for network/provider work.
            _ = page.ActivateAsync();
        }
        finally { IsNavigating = false; RaiseNavigationProperties(); _gate.Release(); }
    }

    public async Task GoBackAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || !Host.CanGoBack) return;
            IsNavigating = true; RaiseNavigationProperties();
            var removed = await Host.PopAsync(Transition());
            if (removed is ContentPage page) _forward.Push(page);
            Current = (PageViewModel)Host.NavigationStack[^1].DataContext!;
            _ = Current.ActivateAsync();
        }
        finally { IsNavigating = false; RaiseNavigationProperties(); _gate.Release(); }
    }

    public async Task GoForwardAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || !_forward.TryPop(out var page)) return;
            IsNavigating = true;
            Current = (PageViewModel)page.DataContext!;
            RaiseNavigationProperties();
            await Host.PushAsync(page, Transition());
            _ = Current.ActivateAsync();
        }
        finally { IsNavigating = false; RaiseNavigationProperties(); _gate.Release(); }
    }

    private SlideFadeTransition Transition() => new()
    {
        Duration = _appearance.Current.ReduceMotion || TopLevel.GetTopLevel(Host) is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(220)
    };

    private static ContentPage CreatePage(PageViewModel model)
    {
        Control view = model switch
        {
            LibraryViewModel => new LibraryView(), SearchViewModel => new SearchView(), OnlineViewModel => new OnlineView(),
            AccountsViewModel => new AccountsView(),
            LyricsViewModel => new LyricsView(), TrackEditorPageViewModel => new TrackEditorView(), ToolsViewModel => new ToolsView(),
            MatchPlaylistPageViewModel => new MatchPlaylistView(),
            SettingsViewModel => new SettingsView(), ComponentsViewModel => new ComponentsView(),
            _ => throw new ArgumentException($"No view registered for {model.GetType().Name}.")
        };
        var page = new ContentPage
        {
            DataContext = model, Content = view, Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
        };
        NavigationPage.SetHasNavigationBar(page, false);
        return page;
    }

    private void RaiseNavigationProperties()
    {
        foreach (var name in new[] { nameof(CanGoBack), nameof(CanGoForward), nameof(IsLibrary), nameof(IsSearch), nameof(IsAccounts), nameof(IsOnline),
            nameof(IsQueue), nameof(IsLyrics), nameof(IsTools), nameof(IsSettings) }) OnPropertyChanged(name);
        BackCommand.NotifyCanExecuteChanged(); ForwardCommand.NotifyCanExecuteChanged();
    }

    private void ReleaseAbandonedPages()
    {
        var retained = _pages.Values.Concat(Host.NavigationStack.Select(p => (PageViewModel)p.DataContext!))
            .Concat(_forward.Select(p => (PageViewModel)p.DataContext!)).ToHashSet();
        _retiredPages.RemoveAll(t => t.IsCompletedSuccessfully);
        foreach (var page in _ownedPages.Where(p => !retained.Contains(p)).ToArray())
        {
            _ownedPages.Remove(page);
            // Cancellation can outlive the transition; it must not hold up navigation.
            _retiredPages.Add(page.DisposeAsync().AsTask());
        }
        OnPropertyChanged(nameof(LoadedPageCount));
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await Task.WhenAll(_ownedPages.Select(p => p.DisposeAsync().AsTask()).Concat(_retiredPages));
        _forward.Clear(); _pages.Clear(); _ownedPages.Clear();
    }
}
