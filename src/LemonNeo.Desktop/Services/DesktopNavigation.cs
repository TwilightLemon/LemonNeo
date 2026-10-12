using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LemonNeo.Desktop.Controls.Navigation;
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
using LemonNeo.Desktop.Views.Accounts;
using LemonNeo.Desktop.Views.Library;
using LemonNeo.Desktop.Views.Lyrics;
using LemonNeo.Desktop.Views.Metadata;
using LemonNeo.Desktop.Views.Online;
using LemonNeo.Desktop.Views.Search;
using LemonNeo.Desktop.Views.Settings;
using LemonNeo.Desktop.Views.Tools;

namespace LemonNeo.Desktop.Services;

/// <summary>Owns one visual page and bounded, lightweight back/forward history.</summary>
public sealed class DesktopNavigation : ObservableObject, IAsyncDisposable
{
    private const int MaximumHistoryEntries = 20;
    private const int MaximumInactivePages = 4;
    private readonly AppearanceService _appearance;
    private readonly DesktopContext _context;
    private readonly Func<string, Guid?, PageViewModel> _factory;
    private readonly Stack<HistoryEntry> _back = [];
    private readonly Stack<HistoryEntry> _forward = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<PageViewModel> _ownedPages = [];
    private readonly HashSet<PageViewModel> _pendingRetirement = [];
    private readonly Dictionary<(string Route, Guid? Playlist), PageViewModel> _pages = [];
    private readonly LinkedList<(string Route, Guid? Playlist)> _recent = [];
    private readonly Dictionary<(string Route, Guid? Playlist), LinkedListNode<(string Route, Guid? Playlist)>> _recentNodes = [];
    private readonly PlaybackViewModel _playback;
    private readonly List<Task> _retiredPages = [];
    private PageViewModel _current = null!;
    private HistoryEntry _currentEntry = null!;
    private bool _disposed;
    private bool _isNavigating;

    public DesktopNavigation(Func<string, Guid?, PageViewModel> factory, DesktopContext context,
        PlaybackViewModel playback)
    {
        _factory = factory;
        _context = context;
        _appearance = context.Appearance;
        _playback = playback;
        Host = new NavigationPage
        {
            IsGestureEnabled = false, IsBackButtonVisible = false, ClipToBounds = true, Background = Brushes.Transparent
        };
        NavigateCommand = new AsyncRelayCommand<string>(route => NavigateAsync(route ?? DesktopRoutes.Library),
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        BackCommand = new AsyncRelayCommand(GoBackAsync, () => CanGoBack);
        ForwardCommand = new AsyncRelayCommand(GoForwardAsync, () => CanGoForward);
        _context.LibraryChanged += OnLibraryChanged;
    }

    public NavigationPage Host { get; }

    public PageViewModel Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public int LoadedPageCount => _ownedPages.Count;
    public int HistoryEntryCount => _back.Count + _forward.Count;
    public bool IsNavigating
    {
        get => _isNavigating;
        private set => SetProperty(ref _isNavigating, value);
    }

    public bool CanGoBack => !IsNavigating && _back.Count > 0;
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
        var page = GetCachedPage(route, playlist);
        TrimInactivePages(page);
        return (T)page;
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Current = GetCachedPage(DesktopRoutes.Library, null);
        _currentEntry = new HistoryEntry(DesktopRoutes.Library, null);
        Host.Content = CreatePage(Current);
        RaiseNavigationProperties();
    }

    public async Task NavigateAsync(string route, Guid? playlist = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            if (_currentEntry.Route == route && _currentEntry.Playlist == playlist) return;
            var entry = new HistoryEntry(route, playlist);
            if (route == DesktopRoutes.MatchPlaylist)
            {
                entry.TransientPage = _factory(route, playlist);
                entry.TransientPage.SetNavigationActive(false);
                Own(entry.TransientPage);
            }
            await NavigateToNewEntryAsync(entry);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task OpenEditorAsync(TrackRow row, bool isNew = false)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var editor = new TrackEditorPageViewModel(_context, this, _playback, row, isNew);
            editor.SetNavigationActive(false);
            Own(editor);
            var entry = new HistoryEntry(DesktopRoutes.Editor, null) { TransientPage = editor };
            try
            {
                await NavigateToNewEntryAsync(entry);
            }
            catch
            {
                RetireIfUnreferenced(entry);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task NavigateToNewEntryAsync(HistoryEntry entry)
    {
        IsNavigating = true;
        RaiseNavigationProperties();
        try
        {
            var old = CaptureCurrent();
            if (!await ShowAsync(entry))
            {
                RetireIfUnreferenced(entry);
                return;
            }
            _back.Push(old);
            RetireHistory(_forward);
            TrimBackHistory();
            TrimInactivePages();
            RaiseNavigationProperties();
        }
        catch
        {
            RetireIfUnreferenced(entry);
            throw;
        }
        finally
        {
            IsNavigating = false;
            RaiseNavigationProperties();
        }
    }

    public async Task GoBackAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _back.Count == 0) return;
            IsNavigating = true;
            RaiseNavigationProperties();
            try
            {
                var destination = _back.Peek();
                var old = CaptureCurrent();
                if (!await ShowAsync(destination)) return;
                _back.Pop();
                _forward.Push(old);
                TrimInactivePages();
                RaiseNavigationProperties();
            }
            finally
            {
                IsNavigating = false;
                RaiseNavigationProperties();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task GoForwardAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _forward.Count == 0) return;
            IsNavigating = true;
            RaiseNavigationProperties();
            try
            {
                var destination = _forward.Peek();
                var old = CaptureCurrent();
                if (!await ShowAsync(destination)) return;
                _forward.Pop();
                _back.Push(old);
                TrimInactivePages();
                RaiseNavigationProperties();
            }
            finally
            {
                IsNavigating = false;
                RaiseNavigationProperties();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private HistoryEntry CaptureCurrent()
    {
        _currentEntry.State = Current.CaptureNavigationState();
        if (Host.NavigationStack.LastOrDefault() is ContentPage page)
            _currentEntry.ScrollOffset = FindScrollViewer(page)?.Offset;
        return _currentEntry;
    }

    private async Task<bool> ShowAsync(HistoryEntry entry)
    {
        var pageModel = entry.TransientPage ?? GetCachedPage(entry.Route, entry.Playlist);
        var next = CreatePage(pageModel);
        var previous = Host.NavigationStack.LastOrDefault() as ContentPage;
        try
        {
            await Host.ReplaceAsync(next, Transition());
            if (!ReferenceEquals(Host.NavigationStack.LastOrDefault(), next)) return false;
            Current.SetNavigationActive(false);
            // Releasing the content after the transition severs every history reference to its controls.
            if (previous is not null)
            {
                previous.Content = null;
                previous.DataContext = null;
            }
            _currentEntry = entry;
            Current = pageModel;
            pageModel.SetNavigationActive(true);
            // The page owns its network work; a transition never waits on providers.
            _ = pageModel.ActivateAsync();
            pageModel.RestoreNavigationState(entry.State);
            RestoreScroll(next, entry.ScrollOffset);
            return true;
        }
        finally
        {
            if (!ReferenceEquals(Host.NavigationStack.LastOrDefault(), next))
            {
                next.Content = null;
                next.DataContext = null;
            }
        }
    }

    private void RestoreScroll(ContentPage page, Vector? offset)
    {
        if (offset is not { } value) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || !ReferenceEquals(Host.NavigationStack.LastOrDefault(), page)) return;
            if (TopLevel.GetTopLevel(page) is { } top) top.UpdateLayout();
            else page.UpdateLayout();
            if (FindScrollViewer(page) is { } viewer) viewer.Offset = value;
        }, DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(ContentPage page)
    {
        if (page.Content is not Control view) return null;
        var trackList = view.GetVisualDescendants().OfType<ListBox>()
            .FirstOrDefault(control => control.Name == "TrackList");
        return (trackList ?? view).GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
    }

    private PageViewModel GetCachedPage(string route, Guid? playlist)
    {
        var key = (route, playlist);
        if (!_pages.TryGetValue(key, out var page))
        {
            page = _factory(route, playlist);
            if (_current is not null) page.SetNavigationActive(false);
            _pages.Add(key, page);
            Own(page);
        }
        Touch(key);
        return page;
    }

    private void Own(PageViewModel page)
    {
        if (!_ownedPages.Add(page)) return;
        page.OperationCompleted += OnPageOperationCompleted;
        OnPropertyChanged(nameof(LoadedPageCount));
    }

    private void Touch((string Route, Guid? Playlist) key)
    {
        if (_recentNodes.TryGetValue(key, out var node)) _recent.Remove(node);
        _recentNodes[key] = _recent.AddLast(key);
    }

    private void TrimInactivePages(PageViewModel? preserve = null)
    {
        var inactive = _pages.Count(pair => !IsPinned(pair.Key.Route) && !ReferenceEquals(pair.Value, Current));
        while (inactive > MaximumInactivePages)
        {
            var key = _recent.FirstOrDefault(candidate =>
                _pages.TryGetValue(candidate, out var page) && !IsPinned(candidate.Route) &&
                !ReferenceEquals(page, Current) && !ReferenceEquals(page, preserve) && !page.HasActiveOperation);
            if (key == default || !_pages.TryGetValue(key, out var old)) break;
            _pages.Remove(key);
            _recent.Remove(_recentNodes[key]);
            _recentNodes.Remove(key);
            Retire(old);
            inactive--;
        }
    }

    private static bool IsPinned(string route) => route is DesktopRoutes.Search or DesktopRoutes.Lyrics;

    private void TrimBackHistory()
    {
        if (_back.Count <= MaximumHistoryEntries) return;
        var retained = _back.Take(MaximumHistoryEntries).ToArray();
        var removed = _back.Skip(MaximumHistoryEntries).ToArray();
        _back.Clear();
        for (var i = retained.Length - 1; i >= 0; i--) _back.Push(retained[i]);
        foreach (var entry in removed) RetireIfUnreferenced(entry);
        OnPropertyChanged(nameof(HistoryEntryCount));
    }

    private void RetireHistory(Stack<HistoryEntry> history)
    {
        var removed = history.ToArray();
        history.Clear();
        foreach (var entry in removed) RetireIfUnreferenced(entry);
        OnPropertyChanged(nameof(HistoryEntryCount));
    }

    private void RetireIfUnreferenced(HistoryEntry entry)
    {
        if (entry.TransientPage is not { } page || ReferenceEquals(page, Current) ||
            _back.Any(item => ReferenceEquals(item.TransientPage, page)) ||
            _forward.Any(item => ReferenceEquals(item.TransientPage, page))) return;
        Retire(page);
    }

    private void Retire(PageViewModel page)
    {
        // Let a save or provider operation finish after its history entry is discarded.
        if (page.HasActiveOperation && !_disposed)
        {
            _pendingRetirement.Add(page);
            return;
        }
        _pendingRetirement.Remove(page);
        if (!_ownedPages.Remove(page)) return;
        page.OperationCompleted -= OnPageOperationCompleted;
        _retiredPages.Add(page.DisposeAsync().AsTask());
        ObserveCompletedRetirements();
        OnPropertyChanged(nameof(LoadedPageCount));
    }

    private void ObserveCompletedRetirements()
    {
        for (var i = _retiredPages.Count - 1; i >= 0; i--)
        {
            var task = _retiredPages[i];
            if (!task.IsCompleted) continue;
            if (task.IsFaulted) Trace.WriteLine($"Page disposal failed: {task.Exception}");
            _retiredPages.RemoveAt(i);
        }
    }

    private void OnPageOperationCompleted(object? sender, EventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (sender is PageViewModel page && _pendingRetirement.Contains(page)) Retire(page);
            // An operation may also have protected a cached page from eviction.
            TrimInactivePages();
        });
    }

    private void OnLibraryChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        var ids = _context.Playlists.Select(playlist => playlist.Id).ToHashSet();
        foreach (var (key, page) in _pages.ToArray())
        {
            if (key.Playlist is not { } id || ids.Contains(id) || ReferenceEquals(page, Current)) continue;
            _pages.Remove(key);
            _recent.Remove(_recentNodes[key]);
            _recentNodes.Remove(key);
            Retire(page);
        }
        RemoveInvalidHistory(_back, ids);
        RemoveInvalidHistory(_forward, ids);
        TrimInactivePages();
        RaiseNavigationProperties();
    }

    private void RemoveInvalidHistory(Stack<HistoryEntry> history, HashSet<Guid> playlistIds)
    {
        var valid = history.Where(entry => entry.Playlist is not { } id || playlistIds.Contains(id)).ToArray();
        if (valid.Length == history.Count) return;
        var removed = history.Where(entry => entry.Playlist is { } id && !playlistIds.Contains(id)).ToArray();
        history.Clear();
        for (var i = valid.Length - 1; i >= 0; i--) history.Push(valid[i]);
        foreach (var entry in removed) RetireIfUnreferenced(entry);
        OnPropertyChanged(nameof(HistoryEntryCount));
    }

    private SlideFadeTransition Transition() => new()
    {
        Duration = _appearance.Current.ReduceMotion || TopLevel.GetTopLevel(Host) is null
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(220)
    };

    private static ContentPage CreatePage(PageViewModel model)
    {
        Control view = model switch
        {
            LibraryViewModel => new LibraryView(), SearchViewModel => new SearchView(),
            OnlineViewModel => new OnlineView(),
            AccountsViewModel => new AccountsView(),
            LyricsViewModel => new LyricsView(), TrackEditorPageViewModel => new TrackEditorView(),
            ToolsViewModel => new ToolsView(),
            MatchPlaylistPageViewModel => new MatchPlaylistView(),
            SettingsViewModel => new SettingsView(),
            _ => throw new ArgumentException($"No view registered for {model.GetType().Name}.")
        };
        var page = new ContentPage
        {
            DataContext = model, Content = view, Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        NavigationPage.SetHasNavigationBar(page, false);
        return page;
    }

    private void RaiseNavigationProperties()
    {
        foreach (var name in new[]
                 {
                     nameof(CanGoBack), nameof(CanGoForward), nameof(IsLibrary), nameof(IsSearch), nameof(IsAccounts),
                     nameof(IsOnline), nameof(IsQueue), nameof(IsLyrics), nameof(IsTools), nameof(IsSettings),
                     nameof(HistoryEntryCount)
                 }) OnPropertyChanged(name);
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _context.LibraryChanged -= OnLibraryChanged;
            foreach (var page in _ownedPages)
            {
                page.OperationCompleted -= OnPageOperationCompleted;
                _retiredPages.Add(page.DisposeAsync().AsTask());
            }
            _ownedPages.Clear();
            _pendingRetirement.Clear();
            _pages.Clear();
            _recent.Clear();
            _recentNodes.Clear();
            _back.Clear();
            _forward.Clear();
            if (Host.NavigationStack.LastOrDefault() is ContentPage pageView)
            {
                pageView.Content = null;
                pageView.DataContext = null;
            }
            Host.Content = null;
            _current = null!;
            _currentEntry = null!;
            tasks = _retiredPages.ToArray();
            _retiredPages.Clear();
            OnPropertyChanged(nameof(LoadedPageCount));
        }
        finally
        {
            _gate.Release();
        }
        await Task.WhenAll(tasks);
    }

    private sealed class HistoryEntry(string route, Guid? playlist)
    {
        public string Route { get; } = route;
        public Guid? Playlist { get; } = playlist;
        public PageViewModel? TransientPage { get; set; }
        public object? State { get; set; }
        public Vector? ScrollOffset { get; set; }
    }
}
