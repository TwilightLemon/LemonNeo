using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Configuration;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class NavigationTests
{
    [AvaloniaFact]
    public async Task PagesLoadOnDemandOwnTheirHeadersAndRestoreTheirViewsFromHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        var navigation = vm.Navigation;
        Assert.Equal(1, navigation.LoadedPageCount);
        Assert.Single(navigation.Host.NavigationStack);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var library = ActiveView(window);
            Assert.Equal(1, navigation.LoadedPageCount);
            var libraryHeader = Assert.Single(library.GetVisualDescendants().OfType<PageHeader>());
            Assert.Equal(fixture.Text["Library"], libraryHeader.Title);
            await navigation.NavigateAsync(DesktopRoutes.Search);
            vm.Search.SearchQuery = "one";
            await vm.Search.SearchAsync();
            vm.Search.SelectedTrack = vm.Search.Tracks.Single();
            var search = ActiveView(window);
            var searchHeader = Assert.Single(search.GetVisualDescendants().OfType<PageHeader>());
            Assert.NotSame(libraryHeader, searchHeader);
            Assert.Same(vm.Search, search.DataContext);
            Assert.Equal(2, navigation.LoadedPageCount);

            await navigation.NavigateAsync(DesktopRoutes.Settings);
            var settings = ActiveView(window);
            Assert.IsType<SettingsViewModel>(settings.DataContext);
            Assert.Equal(3, navigation.LoadedPageCount);
            await navigation.GoBackAsync();
            Assert.Same(search, ActiveView(window));
            Assert.Equal("one", vm.Search.SearchQuery);
            Assert.NotNull(vm.Search.SelectedTrack);
            Assert.True(navigation.CanGoForward);
            await navigation.GoForwardAsync();
            Assert.Same(settings, ActiveView(window));
            await navigation.GoBackAsync();
            await navigation.NavigateAsync(DesktopRoutes.Online);
            Assert.False(navigation.CanGoForward);
            Assert.Equal(4, navigation.LoadedPageCount);
            await navigation.GoBackAsync();
            await navigation.GoBackAsync();
            Assert.Same(library, ActiveView(window));
            Assert.False(navigation.CanGoBack);
            Assert.Equal(4, navigation.LoadedPageCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task GlobalSearchFromAnyPageSupportsEnterAndButtonWithoutBlockingNavigation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            Assert.Equal(1, vm.Navigation.LoadedPageCount);
            var input = window.FindControl<TextBox>("GlobalSearch")!;
            input.Text = "one";
            Dispatcher.UIThread.RunJobs();
            input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            await vm.SearchCommand.ExecutionTask!;
            Assert.Same(vm.Search, vm.Navigation.Current);
            Assert.Single(vm.Search.Tracks);
            Assert.Equal(2, vm.Navigation.LoadedPageCount);
            Assert.Empty(ActiveView(window).GetVisualDescendants().OfType<TextBox>());

            await vm.Navigation.NavigateAsync(DesktopRoutes.Settings);
            input.Text = "deferred";
            Dispatcher.UIThread.RunJobs();
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.SearchCommand);
            Assert.Equal("deferred", vm.SearchQuery);
            Assert.True(vm.SearchCommand.CanExecute(null));
            Assert.True(button.IsEnabled);
            window.UpdateLayout();
            var point = button.TranslatePoint(new(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            var pending = vm.SearchCommand.ExecutionTask!;
            await fixture.Provider.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(vm.Search, vm.Navigation.Current);
            Assert.True(vm.Search.IsBusy);
            Assert.False(vm.IsBusy);
            Assert.False(vm.Playback.IsBusy);
            Assert.True(input.IsEnabled);
            await vm.Navigation.GoBackAsync();
            Assert.Same(vm.Settings, vm.Navigation.Current);
            fixture.Provider.SearchRelease.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(vm.Settings, vm.Navigation.Current);
            await vm.Navigation.GoForwardAsync();
            Assert.Equal("deferred", input.Text);
            Assert.Equal(2, vm.Search.Tracks.Count);
        }
        finally
        {
            fixture.Provider.SearchRelease.TrySetResult();
            vm.Search.CancelOperationCommand.Execute(null);
            if (vm.SearchCommand.ExecutionTask is { } pending) await pending;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PlaylistPagesAndQueueKeepIndependentSelectionsAndFilters()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.Library.CreatePlaylistAsync("First");
        var first = vm.Library;
        await first.AddTrackAsync(first.SelectedPlaylist!.Id, fixture.Provider.First);
        first.Filter = "One";
        first.SelectedTrack = first.Tracks.Single();
        await vm.Library.CreatePlaylistAsync("Second");
        var second = vm.Library;
        Assert.NotSame(first, second);
        Assert.Empty(second.Filter);
        Assert.Null(second.SelectedTrack);
        second.Filter = "Second filter";
        await vm.Navigation.GoBackAsync();
        Assert.Same(first, vm.Navigation.Current);
        Assert.Equal("One", first.Filter);
        Assert.NotNull(first.SelectedTrack);
        await vm.Navigation.NavigateAsync(DesktopRoutes.Queue);
        vm.Queue.Filter = "Queue filter";
        Assert.Equal("One", first.Filter);
        Assert.Equal("Second filter", second.Filter);
        await vm.Navigation.GoBackAsync();
        Assert.Same(first, vm.Navigation.Current);
    }

    [AvaloniaFact]
    public async Task PendingSearchAllowsNavigationSettingsLibraryAndPlaybackAndDoesNotRedirectOnCompletion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.Navigation.NavigateAsync(DesktopRoutes.Search);
        vm.Search.SearchQuery = "one";
        await vm.Search.SearchAsync();
        await vm.Search.PlayAsync(vm.Search.Tracks.Single());
        vm.Search.SearchQuery = "deferred";
        var pending = vm.Search.SearchAsync();
        try
        {
            await fixture.Provider.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.Search.IsBusy);
            await vm.Navigation.NavigateAsync(DesktopRoutes.Settings);
            Assert.True(vm.Settings.CanInteract);
            await vm.Settings.ThemeCommand.ExecuteAsync("Dark");
            Assert.True(vm.Settings.IsThemeDark);
            Assert.True(vm.Search.IsBusy);
            await vm.Playback.TogglePlaybackCommand.ExecuteAsync(null);
            Assert.False(vm.Playback.IsPlaying);
            await vm.Playback.SeekAsync(2);
            Assert.Equal(2, vm.Playback.PositionSeconds);
            await vm.Navigation.NavigateAsync(DesktopRoutes.Library);
            await vm.Library.CreatePlaylistAsync("Created while searching");
            Assert.Single(vm.Playlists);
            Assert.True(vm.Search.IsBusy);
            Assert.False(vm.Library.IsBusy);
            Assert.False(vm.Library.CancelOperationCommand.CanExecute(null));
            fixture.Provider.SearchRelease.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DesktopRoutes.Library, vm.Navigation.Current.Route);
            Assert.Equal(2, vm.Search.Tracks.Count);
            Assert.False(vm.Search.IsBusy);
            fixture.Provider.FailSearch = true;
            await vm.Search.SearchAsync();
            Assert.True(vm.Search.HasError);
            Assert.False(vm.Library.HasError);
            Assert.False(vm.Playback.HasError);
            Assert.False(vm.HasError);
        }
        finally
        {
            fixture.Provider.SearchRelease.TrySetResult();
            vm.Search.CancelOperationCommand.Execute(null);
            await pending;
        }
    }

    [AvaloniaFact]
    public async Task SlideFadeHidesOldPageImmediatelyAndAnimatesNewPageInBothDirections()
    {
        var originalTransform = new RotateTransform(5);
        var outgoing = new Border { RenderTransform = originalTransform, Opacity = 0.8 };
        var incoming = new Border();
        var window = new Window { Content = new Grid { Children = { outgoing, incoming } } };
        window.Show();
        var transition = new SlideFadeTransition { Duration = TimeSpan.FromMilliseconds(180) };
        try
        {
            foreach (var forward in new[] { true, false })
            {
                outgoing.IsVisible = true;
                incoming.IsVisible = false;
                var animation = transition.Start(outgoing, incoming, forward, CancellationToken.None);
                Assert.False(outgoing.IsVisible);
                Assert.Same(originalTransform, outgoing.RenderTransform);
                Assert.Equal(0.8, outgoing.Opacity);
                var sampled = false;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!animation.IsCompleted)
                {
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    if (incoming.RenderTransform is TranslateTransform transform && Math.Abs(transform.Y) > 0.1 && incoming.Opacity is > 0 and < 1)
                    {
                        Assert.Equal(forward, transform.Y > 0);
                        Assert.False(outgoing.IsVisible);
                        Assert.Same(originalTransform, outgoing.RenderTransform);
                        Assert.Equal(0.8, outgoing.Opacity);
                        sampled = true;
                    }
                    await Task.Delay(5, timeout.Token);
                }
                await animation;
                Assert.True(sampled, "The transition must animate both position and opacity.");
                Assert.Null(incoming.RenderTransform);
                Assert.Same(originalTransform, outgoing.RenderTransform);
                Assert.Equal(1, incoming.Opacity);
                Assert.False(outgoing.IsVisible);
            }
            outgoing.IsVisible = true;
            using var cancellation = new CancellationTokenSource();
            var cancelled = transition.Start(outgoing, incoming, true, cancellation.Token);
            Assert.False(outgoing.IsVisible);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            cancellation.Cancel();
            await cancelled;
            Assert.Same(originalTransform, outgoing.RenderTransform);
            Assert.Equal(0.8, outgoing.Opacity);
            Assert.Null(incoming.RenderTransform);
            Assert.Equal(1, incoming.Opacity);
        }
        finally { window.Close(); }
    }

    private static UserControl ActiveView(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return Assert.Single(window.GetVisualDescendants().OfType<UserControl>(), view => view.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task RapidNavigationIsQueuedAndReduceMotionDisablesTheNextTransition()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.SharedContext.Appearance.SetAsync(new(ThemePreference.Light, LanguagePreference.English, false));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var search = vm.Navigation.NavigateAsync(DesktopRoutes.Search);
            var settings = vm.Navigation.NavigateAsync(DesktopRoutes.Settings);
            Assert.True(vm.Navigation.IsNavigating);
            Assert.False(settings.IsCompleted);
            var pending = Task.WhenAll(search, settings);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!pending.IsCompleted)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5, timeout.Token);
            }
            await pending;
            Assert.Equal(DesktopRoutes.Settings, vm.Navigation.Current.Route);
            Assert.Equal(3, vm.Navigation.Host.StackDepth);
            Assert.Single(window.GetVisualDescendants().OfType<UserControl>(), p => p.IsEffectivelyVisible);
            await vm.SharedContext.Appearance.SetAsync(new(ThemePreference.Light, LanguagePreference.English, true));
            var back = vm.Navigation.GoBackAsync();
            Assert.True(back.IsCompletedSuccessfully);
            await back;
            Assert.Equal(DesktopRoutes.Search, vm.Navigation.Current.Route);
            Assert.Equal(1, vm.Navigation.Host.NavigationStack[^1].Opacity);
            Assert.Null(vm.Navigation.Host.NavigationStack[^1].RenderTransform);
        }
        finally { window.Close(); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestPaths _paths = new();
        private readonly DesktopRuntime _runtime;
        public FeatureProvider Provider { get; }
        public LocalizationService Text { get; } = new();
        public MainViewModel ViewModel { get; }
        private Fixture()
        {
            Provider = new(_paths);
            _runtime = new(_paths, new(Path.Combine(AppContext.BaseDirectory, "plugins"), _paths.MusicLibraryDirectory),
                [new DesktopTests.TestPlayerProvider()], [Provider]);
            var appearance = new AppearanceService(Avalonia.Application.Current!, Text, _runtime.Get<IAppConfigurationStore>());
            ViewModel = new(_runtime, Text, appearance, _runtime.Get<ArtworkService>());
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.ViewModel.InitializeAsync();
            await fixture.ViewModel.SharedContext.Appearance.SetAsync(new(ThemePreference.Light, LanguagePreference.English, true));
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync(); await _runtime.DisposeAsync(); _paths.Dispose();
        }
    }
}
