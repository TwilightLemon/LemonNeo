using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Configuration;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using LemonNeo.Persistence;
using LemonNeo.Plugin.Abstractions;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class DesktopTests
{
    [AvaloniaFact]
    public async Task ImportFilterPlaybackAndPlaylistPersistenceUseTheSharedServices()
    {
        using var paths = new TestPaths();
        var folder = CreateMusicFolder(paths);
        var player = new TestPlayerProvider();
        await using var runtime = CreateRuntime(paths, player);
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await vm.InitializeAsync();
        Assert.Null(vm.Error);
        await vm.Library.CreatePlaylistAsync("My local music", folderPath: folder);
        Assert.Null(vm.Library.Error);
        Assert.Equal(2, vm.Library.Tracks.Count);
        var importedId = Assert.Single(vm.Playlists).Id;
        var saved = Assert.Single((await new JsonLibraryRepository(paths.MusicLibraryDirectory).LoadAsync()).Playlists);
        Assert.Equal("My local music", saved.Name);
        Assert.Null(saved.Description);
        Assert.Equal(2, saved.Items.Count);
        vm.Library.Filter = "night";
        Assert.Equal("Night", Assert.Single(vm.Library.Tracks).Title);
        await vm.Library.PlaySelectedCommand.ExecuteAsync(null);
        Assert.Null(vm.Playback.Error);
        Assert.True(vm.Playback.IsPlaying);
        Assert.Equal("Night", vm.Playback.NowTitle);
        Assert.Single(runtime.Get<PlaybackSessionService>().Queue.Items);
        var positionNotifications = 0;
        vm.Playback.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.Playback.PositionSeconds)) positionNotifications++; };
        vm.Playback.IsSeeking = true;
        await vm.Playback.TogglePlaybackCommand.ExecuteAsync(null);
        Assert.Equal(PlayerState.Paused, player.Player.State);
        Assert.Equal(0, positionNotifications);
        await vm.Playback.SeekAsync(7);
        vm.Playback.EndSeek();
        Assert.True(positionNotifications > 0);
        Assert.Equal(7, vm.Playback.PositionSeconds);
        Assert.Equal(TimeSpan.FromSeconds(7), (await new JsonPlaybackStateRepository(paths.MusicLibraryDirectory).LoadAsync()).Position);
        await vm.Library.CreatePlaylistAsync("A fresh playlist");
        Assert.Equal(2, vm.Playlists.Count);
        vm.SelectedPlaylist = vm.Playlists.Single(p => p.Id == importedId);
        Assert.Equal("night", vm.Library.Filter);
        Assert.Single(vm.Library.Tracks);
        vm.Library.Filter = "";
        Assert.Equal(2, vm.Library.Tracks.Count);
    }

    [AvaloniaFact]
    public async Task RestoredQueueKeepsItemIdentitiesAndSourceWhenPlayingAFilteredDuplicate()
    {
        using var paths = new TestPaths();
        var folder = CreateMusicFolder(paths);
        await using var runtime = CreateRuntime(paths, new TestPlayerProvider());
        await runtime.InitializeAsync(CancellationToken.None);
        var imported = await runtime.Get<FolderImportService>().ImportAsync(folder);
        await runtime.Get<LibraryService>().SaveAsync();
        var track = imported.Playlist.Items[0].Track;
        var first = new PlaybackQueueItem { Track = track };
        var second = new PlaybackQueueItem { Track = imported.Playlist.Items[1].Track };
        var duplicate = new PlaybackQueueItem { Track = track };
        var request = new PlaybackRequest(RequiredSourceProviderId: "local");
        runtime.Get<PlaybackSessionService>().Queue.Restore(new()
        {
            Items = [first, second, duplicate], CurrentItemId = first.Id, Request = request
        });
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await vm.InitializeAsync();
        await vm.Navigation.NavigateAsync(DesktopRoutes.Queue);
        vm.Queue.Filter = track.Title;
        Assert.Equal(2, vm.Queue.Tracks.Count);
        await vm.Queue.PlayAsync(vm.Queue.Tracks[1]);
        Assert.Null(vm.Playback.Error);
        var queue = runtime.Get<PlaybackSessionService>().Queue;
        Assert.Equal(new[] { first.Id, second.Id, duplicate.Id }, queue.Items.Select(i => i.Id));
        Assert.Equal(duplicate.Id, queue.CurrentItem!.Id);
        Assert.Equal(request, queue.Request);
    }

    [AvaloniaFact]
    public async Task FailedPlaybackIsVisibleAndDoesNotLeaveTheInterfaceBusy()
    {
        using var paths = new TestPaths();
        await using var runtime = CreateRuntime(paths, new TestPlayerProvider());
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await vm.InitializeAsync();
        var playlist = runtime.Get<PlaylistService>().Create("Unavailable");
        runtime.Get<PlaylistService>().Add(playlist.Id, new Track { Id = new("missing", "missing"), Title = "No source" });
        await runtime.Get<LibraryService>().SaveAsync();
        await vm.Library.ReloadLibraryAsync();
        await vm.Library.PlayAllCommand.ExecuteAsync(null);
        Assert.True(vm.Playback.HasError);
        Assert.False(vm.Playback.IsBusy);
        Assert.True(vm.Playback.CanInteract);
    }

    [AvaloniaFact]
    public async Task ViewsRenderAtMinimumAndDefaultSizesInBothPalettes()
    {
        using var paths = new TestPaths();
        await using var runtime = CreateRuntime(paths, new TestPlayerProvider());
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await vm.InitializeAsync();
        await vm.Library.CreatePlaylistAsync("Music", folderPath: CreateMusicFolder(paths));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await appearance.SetAsync(new(theme, LanguagePreference.Chinese, true));
                foreach (var page in DesktopRoutes.All)
                {
                    await vm.Navigation.NavigateAsync(page);
                    foreach (var size in new[] { new Size(880, 620), new Size(1180, 790) })
                    {
                        window.Width = size.Width; window.Height = size.Height;
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
                        bitmap.Render(window);
                        var activeView = Assert.Single(window.GetVisualDescendants().OfType<UserControl>(), v => v.IsEffectivelyVisible);
                        Assert.True(activeView.Bounds.Width > 300 && activeView.Bounds.Height > 200, "Navigation must give the active page usable space at the minimum window size.");
                        var playButton = window.GetVisualDescendants().OfType<Button>().First(b => b.Command == vm.Playback.TogglePlaybackCommand);
                        Assert.True(playButton.Bounds.Width >= 40 && playButton.Bounds.Height >= 40, "Playback controls must remain usable at every supported window size.");
                        var sidebar = window.FindControl<Border>("Sidebar")!;
                        var topBar = window.FindControl<Border>("TopBar")!;
                        var playbackBar = window.FindControl<Border>("PlaybackBar")!;
                        var pageContent = window.FindControl<ContentControl>("PageContent")!;
                        var playbackLocation = playbackBar.TranslatePoint(default, window)!.Value;
                        var pageLocation = pageContent.TranslatePoint(default, window)!.Value;
                        Assert.Equal(size.Height, sidebar.Bounds.Height);
                        Assert.Equal(0, topBar.TranslatePoint(default, window)!.Value.Y);
                        Assert.Equal(sidebar.Bounds.Right + 1, playbackLocation.X);
                        Assert.Equal(size.Height, playbackLocation.Y + playbackBar.Bounds.Height);
                        Assert.Equal(topBar.Bounds.Width, playbackBar.Bounds.Width);
                        Assert.True(pageLocation.Y >= topBar.Bounds.Height && pageLocation.Y + pageContent.Bounds.Height <= playbackLocation.Y);
                        foreach (var buttonName in new[] { "BackButton", "ForwardButton" })
                        {
                            var button = window.FindControl<Button>(buttonName)!;
                            Assert.InRange(button.Bounds.Width, 32, 34);
                            Assert.Equal(32, button.Bounds.Height);
                            Assert.True(button.TranslatePoint(default, window)!.Value.Y + button.Bounds.Height <= topBar.Bounds.Height);
                        }
                        Assert.Equal(32, window.FindControl<TextBox>("GlobalSearch")!.Bounds.Height);
                        var timeline = window.FindControl<Slider>("Timeline")!;
                        var timelineCenter = timeline.TranslatePoint(new(timeline.Bounds.Width / 2, timeline.Bounds.Height / 2), window)!.Value;
                        Assert.Equal(playbackBar.Bounds.Width, timeline.Bounds.Width);
                        Assert.InRange(Math.Abs(timelineCenter.Y - playbackLocation.Y), 0, 1);
                        var playCenter = playButton.TranslatePoint(new(playButton.Bounds.Width / 2, playButton.Bounds.Height / 2), window)!.Value;
                        Assert.InRange(Math.Abs(playCenter.X - (playbackLocation.X + playbackBar.Bounds.Width / 2)), 0, 1);
                        Assert.InRange(Math.Abs(playCenter.Y - (playbackLocation.Y + playbackBar.Bounds.Height / 2)), 0, 1);
                        foreach (var buttonName in new[] { "PreviousButton", "PlayPauseButton", "NextButton", "PlaybackModeButton" })
                        {
                            var button = window.FindControl<Button>(buttonName)!;
                            var icon = Assert.Single(button.GetVisualDescendants().OfType<Viewbox>(), v => v.IsVisible);
                            var iconCenter = icon.TranslatePoint(new(icon.Bounds.Width / 2, icon.Bounds.Height / 2), button)!.Value;
                            Assert.InRange(Math.Abs(iconCenter.X - button.Bounds.Width / 2), 0, 1);
                            Assert.InRange(Math.Abs(iconCenter.Y - button.Bounds.Height / 2), 0, 1);
                        }
                        foreach (var navigation in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nav")))
                        {
                            var location = navigation.TranslatePoint(default, window)!.Value;
                            Assert.True(location.Y >= 0 && location.Y + navigation.Bounds.Height <= size.Height,
                                "Every primary navigation action must remain visible in the full-height sidebar.");
                        }
                        // Optional artifacts for visual review; CI needs no display server.
                        if (Environment.GetEnvironmentVariable("LEMONNEO_PREVIEW_DIRECTORY") is { Length: > 0 } directory)
                        {
                            Directory.CreateDirectory(directory);
                            bitmap.Save(Path.Combine(directory, $"{page}-{theme}-{size.Width}.png"), PngBitmapEncoderOptions.Default);
                        }
                    }
                }
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PlaybackBarSupportsSeekingModeIconsSongInformationNavigationAndLoadingOverlay()
    {
        using var paths = new TestPaths();
        await using var runtime = CreateRuntime(paths, new TestPlayerProvider());
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await vm.InitializeAsync();
        await appearance.SetAsync(new(ThemePreference.Light, LanguagePreference.English, true));
        await vm.Library.CreatePlaylistAsync("Music", folderPath: CreateMusicFolder(paths));
        await vm.Library.PlayAllCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = vm, Width = 880, Height = 620 };
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? pending = null;
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var bar = window.FindControl<Border>("PlaybackBar")!;
            var timeline = window.FindControl<Slider>("Timeline")!;
            var loading = window.FindControl<ProgressBar>("PlaybackLoading")!;
            var mode = window.FindControl<Button>("PlaybackModeButton")!;
            Assert.DoesNotContain(bar.GetVisualDescendants().OfType<Button>(), b => b.Command == vm.Playback.StopCommand || b.Command == vm.Playback.CancelOperationCommand);
            foreach (var expected in new[] { PlaybackMode.RepeatAll, PlaybackMode.Shuffle, PlaybackMode.Sequential })
            {
                await ClickAsync(mode);
                Assert.Equal(expected, runtime.Get<PlaybackSessionService>().Queue.Mode);
                Assert.True(window.TryFindResource($"Icon.Playback.{expected}", window.ActualThemeVariant, out var geometry));
                Assert.Same(geometry, Assert.Single(mode.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), p => p.IsEffectivelyVisible).Data);
                Assert.Equal(vm.Playback.ModeLabel, mode.GetValue(AutomationProperties.NameProperty));
            }

            await ClickAsync(window.FindControl<Button>("PlayPauseButton")!);
            Assert.False(vm.Playback.IsPlaying);
            SavePreview("Playback-Paused");
            await ClickAsync(window.FindControl<Button>("PlayPauseButton")!);
            Assert.True(vm.Playback.IsPlaying);
            SavePreview("Playback-Playing");
            await ClickAsync(window.FindControl<Button>("NextButton")!);
            Assert.Equal(1, runtime.Get<PlaybackSessionService>().Snapshot.CurrentIndex);
            await ClickAsync(window.FindControl<Button>("PreviousButton")!);
            Assert.Equal(0, runtime.Get<PlaybackSessionService>().Snapshot.CurrentIndex);

            var middle = timeline.TranslatePoint(new(timeline.Bounds.Width / 2, timeline.Bounds.Height / 2 + 1), window)!.Value;
            window.MouseDown(middle, MouseButton.Left); window.MouseUp(middle, MouseButton.Left);
            await WaitForSeekAsync();
            Assert.InRange(vm.Playback.PositionSeconds, 89, 91);
            timeline.Focus();
            window.KeyPressQwerty(PhysicalKey.End, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.End, RawInputModifiers.None);
            await WaitForSeekAsync();
            Assert.Equal(180, vm.Playback.PositionSeconds);
            window.UpdateLayout();
            var thumb = timeline.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().Single();
            var thumbPoint = thumb.TranslatePoint(new(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            var quarter = timeline.TranslatePoint(new(timeline.Bounds.Width / 4, timeline.Bounds.Height / 2), window)!.Value;
            window.MouseDown(thumbPoint, MouseButton.Left);
            window.MouseMove(quarter, RawInputModifiers.LeftMouseButton);
            window.MouseUp(quarter, MouseButton.Left);
            await WaitForSeekAsync();
            Assert.InRange(vm.Playback.PositionSeconds, 43, 47);

            var info = window.FindControl<Button>("NowPlayingButton")!;
            foreach (var part in new Control[]
            {
                info.GetVisualDescendants().OfType<LemonNeo.Desktop.Controls.Artwork>().Single(),
                info.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == vm.Playback.NowTitle),
                info.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == vm.Playback.NowArtist)
            })
            {
                var point = part.TranslatePoint(new(part.Bounds.Width / 2, part.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                await vm.Navigation.NavigateCommand.ExecutionTask!;
                Assert.Equal(DesktopRoutes.Lyrics, vm.Navigation.Current.Route);
                await vm.Navigation.GoBackAsync();
            }

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = vm.Playback.ExecutePlaybackAsync(async ct => { started.TrySetResult(); await release.Task.WaitAsync(ct); });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(loading.IsVisible);
            Assert.Equal(timeline.Bounds.Width, loading.Bounds.Width);
            var loadingCenter = loading.TranslatePoint(new(loading.Bounds.Width / 2, loading.Bounds.Height / 2), window)!.Value;
            Assert.Equal(middle.Y - 1, loadingCenter.Y);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(30);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            SavePreview("Playback-Loading");
            await ClickAsync(info);
            Assert.Equal(DesktopRoutes.Lyrics, vm.Navigation.Current.Route);
            release.TrySetResult(); await pending;
            Dispatcher.UIThread.RunJobs();
            Assert.False(loading.IsVisible);
        }
        finally
        {
            release.TrySetResult();
            if (pending is not null) await pending;
            window.Close();
        }

        async Task ClickAsync(Button button)
        {
            window.UpdateLayout();
            var point = button.TranslatePoint(new(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            if (button.Command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand { ExecutionTask: { } execution })
                await execution.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        }
        async Task WaitForSeekAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (vm.Playback.IsSeeking || vm.Playback.IsBusy)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5, timeout.Token);
            }
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        }
        void SavePreview(string name)
        {
            if (Environment.GetEnvironmentVariable("LEMONNEO_PREVIEW_DIRECTORY") is not { Length: > 0 } directory) return;
            Directory.CreateDirectory(directory);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Width, (int)window.Height));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(directory, $"{name}.png"), PngBitmapEncoderOptions.Default);
        }
    }

    [AvaloniaFact]
    public async Task FolderPlaylistDialogCreatesOnlyAfterConfirmationAndPersistsEditedFields()
    {
        using var paths = new TestPaths();
        var folder = CreateMusicFolder(paths);
        await using var runtime = CreateRuntime(paths, new TestPlayerProvider());
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await vm.InitializeAsync();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            var add = window.GetVisualDescendants().OfType<Button>().Single(button =>
                button.GetValue(AutomationProperties.NameProperty) == text["CreatePlaylist"]);
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var cancelled = Assert.IsType<PlaylistDialog>(Assert.Single(window.OwnedWindows));
            var draft = Assert.IsType<PlaylistDialogViewModel>(cancelled.DataContext);
            draft.SelectFolder(folder);
            Assert.Equal("Music", draft.Name);
            Assert.Empty(runtime.Get<LibraryService>().Current.Playlists);
            cancelled.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.Playlists);
            Assert.Empty((await new JsonLibraryRepository(paths.MusicLibraryDirectory).LoadAsync()).Playlists);

            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var confirmed = Assert.IsType<PlaylistDialog>(Assert.Single(window.OwnedWindows));
            var edited = Assert.IsType<PlaylistDialogViewModel>(confirmed.DataContext);
            edited.SelectFolder(folder);
            confirmed.FindControl<TextBox>("PlaylistName")!.Text = "  Custom title  ";
            confirmed.FindControl<TextBox>("PlaylistDescription")!.Text = "  Custom description  ";
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(runtime.Get<LibraryService>().Current.Playlists);
            confirmed.FindControl<Button>("CreateButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (vm.Playlists.Count == 0 || vm.Library.IsBusy)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10, timeout.Token);
            }
            Assert.Null(vm.Library.Error);
            var saved = Assert.Single((await new JsonLibraryRepository(paths.MusicLibraryDirectory).LoadAsync()).Playlists);
            Assert.Equal("Custom title", saved.Name);
            Assert.Equal("Custom description", saved.Description);
            Assert.Equal(2, saved.Items.Count);
            Assert.Equal(saved.Id, vm.SelectedPlaylist!.Id);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FailedFolderScanDoesNotCreateAnEmptyPlaylist()
    {
        using var paths = new TestPaths();
        await using var runtime = CreateRuntime(paths, new TestPlayerProvider());
        var text = new LocalizationService();
        var appearance = new AppearanceService(Avalonia.Application.Current!, text, runtime.Get<IAppConfigurationStore>());
        await using var vm = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await vm.InitializeAsync();
        await vm.Library.CreatePlaylistAsync("Missing folder", "Description", Path.Combine(paths.DataDirectory, "missing"));
        Assert.True(vm.Library.HasError);
        Assert.True(vm.Library.CanInteract);
        Assert.Empty(vm.Playlists);
        Assert.Empty(runtime.Get<LibraryService>().Current.Playlists);
        Assert.Empty((await new JsonLibraryRepository(paths.MusicLibraryDirectory).LoadAsync()).Playlists);
    }

    private static DesktopRuntime CreateRuntime(TestPaths paths, IPlayerProvider player) => new(paths,
        new(Path.Combine(AppContext.BaseDirectory, "plugins"), paths.MusicLibraryDirectory), [player]);

    private static string CreateMusicFolder(TestPaths paths)
    {
        var folder = Path.Combine(paths.DataDirectory, "Music");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "Morning.wav"), [0]);
        File.WriteAllBytes(Path.Combine(folder, "Night.wav"), [0]);
        return folder;
    }

    internal sealed class TestPlayerProvider : IPlayerProvider
    {
        public string Id => "test-file-player";
        public PlayerDescriptor Descriptor => new("Test player", "In-memory test timeline");
        public int Priority => int.MaxValue;
        public IPlayer Player { get; } = new TestPlayer();
        public bool CanPlay(PlaybackSource source) => source is FilePlaybackSource;
    }

    private sealed class TestPlayer : ISeekablePlayer
    {
        public PlayerState State { get; private set; }
        public Track? CurrentTrack { get; private set; }
        public PlayerTimeline Timeline { get; private set; } = new(TimeSpan.Zero, TimeSpan.FromMinutes(3));
        public Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default)
        { CurrentTrack = track; State = PlayerState.Playing; Timeline = new(TimeSpan.Zero, TimeSpan.FromMinutes(3)); return Task.CompletedTask; }
        public Task PauseAsync(CancellationToken cancellationToken = default) { State = PlayerState.Paused; return Task.CompletedTask; }
        public Task ResumeAsync(CancellationToken cancellationToken = default) { State = PlayerState.Playing; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { State = PlayerState.Stopped; Timeline = Timeline with { Position = TimeSpan.Zero }; return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) { Timeline = Timeline with { Position = position }; return Task.CompletedTask; }
    }
}
