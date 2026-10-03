using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Automation;
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
        await vm.CreatePlaylistAsync("My local music", folderPath: folder);
        Assert.Null(vm.Error);
        Assert.Equal(2, vm.Tracks.Count);
        var importedId = Assert.Single(vm.Playlists).Id;
        var saved = Assert.Single((await new JsonLibraryRepository(paths.MusicLibraryDirectory).LoadAsync()).Playlists);
        Assert.Equal("My local music", saved.Name);
        Assert.Null(saved.Description);
        Assert.Equal(2, saved.Items.Count);
        vm.Filter = "night";
        Assert.Equal("Night", Assert.Single(vm.Tracks).Title);
        await vm.PlaySelectedCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        Assert.True(vm.IsPlaying);
        Assert.Equal("Night", vm.NowTitle);
        Assert.Single(runtime.Get<PlaybackSessionService>().Queue.Items);
        var positionNotifications = 0;
        vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.PositionSeconds)) positionNotifications++; };
        vm.IsSeeking = true;
        await vm.TogglePlaybackCommand.ExecuteAsync(null);
        Assert.Equal(PlayerState.Paused, player.Player.State);
        Assert.Equal(0, positionNotifications);
        await vm.SeekAsync(7);
        vm.EndSeek();
        Assert.True(positionNotifications > 0);
        Assert.Equal(7, vm.PositionSeconds);
        Assert.Equal(TimeSpan.FromSeconds(7), (await new JsonPlaybackStateRepository(paths.MusicLibraryDirectory).LoadAsync()).Position);
        await vm.CreatePlaylistAsync("A fresh playlist");
        Assert.Equal(2, vm.Playlists.Count);
        vm.SelectedPlaylist = vm.Playlists.Single(p => p.Id == importedId);
        Assert.Equal(2, vm.Tracks.Count);
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
        vm.Navigate(DesktopPage.Queue);
        vm.Filter = track.Title;
        Assert.Equal(2, vm.Tracks.Count);
        await vm.PlayAsync(vm.Tracks[1]);
        Assert.Null(vm.Error);
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
        await vm.ReloadLibraryAsync();
        await vm.PlayAllCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanInteract);
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
        await vm.CreatePlaylistAsync("Music", folderPath: CreateMusicFolder(paths));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await appearance.SetAsync(new(theme, LanguagePreference.Chinese, true));
                foreach (var page in Enum.GetValues<DesktopPage>())
                {
                    vm.Navigate(page);
                    foreach (var size in new[] { new Size(880, 620), new Size(1180, 790) })
                    {
                        window.Width = size.Width; window.Height = size.Height;
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
                        bitmap.Render(window);
                        var activeView = Assert.Single(window.GetVisualDescendants().OfType<UserControl>(), v => v.IsEffectivelyVisible);
                        Assert.True(activeView.Bounds.Width > 300 && activeView.Bounds.Height > 200, "Navigation must give the active page usable space at the minimum window size.");
                        var playButton = window.GetVisualDescendants().OfType<Button>().First(b => b.Command == vm.TogglePlaybackCommand);
                        Assert.True(playButton.Bounds.Width >= 40 && playButton.Bounds.Height >= 40, "Playback controls must remain usable at every supported window size.");
                        foreach (var navigation in window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nav")))
                        {
                            var location = navigation.TranslatePoint(default, window)!.Value;
                            Assert.True(location.Y >= 0 && location.Y + navigation.Bounds.Height <= size.Height - 112,
                                "Every primary navigation action must remain visible above the playback bar.");
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
            while (vm.Playlists.Count == 0 || vm.IsBusy)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(10, timeout.Token);
            }
            Assert.Null(vm.Error);
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
        await vm.CreatePlaylistAsync("Missing folder", "Description", Path.Combine(paths.DataDirectory, "missing"));
        Assert.True(vm.HasError);
        Assert.True(vm.CanInteract);
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
