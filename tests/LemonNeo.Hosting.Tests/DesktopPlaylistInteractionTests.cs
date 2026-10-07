using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using LemonNeo.Desktop;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Controls.Shared;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Library;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Shared;
using LemonNeo.Desktop.ViewModels.Shell;
using LemonNeo.Desktop.Views.Library;
using LemonNeo.Desktop.Views.Playback;
using LemonNeo.Desktop.Views.Shell;
using Xunit;

namespace LemonNeo.Hosting.Tests;

public sealed class DesktopPlaylistInteractionTests
{
    [Fact]
    public void PlayingIdentityRequiresTheWholeSourceSchemeAndCaseSensitiveValue()
    {
        var track = HostFixture.Track("Native") with { ExternalIds = [new("catalog", "AbC", "song")] };
        Assert.True(PlayingTrackMatcher.Matches(track, new("catalog", "AbC", "song")));
        Assert.True(PlayingTrackMatcher.Matches(track, new("CATALOG", "AbC", "SONG")));
        Assert.True(PlayingTrackMatcher.Matches(track, new("acceptance-music", "Native", "track-id")));
        Assert.False(PlayingTrackMatcher.Matches(track, new("catalog", "abc", "song")));
        Assert.False(PlayingTrackMatcher.Matches(track, new("other", "AbC", "song")));
        Assert.False(PlayingTrackMatcher.Matches(track, new("catalog", "AbC", "album")));
        Assert.False(PlayingTrackMatcher.Matches(track, new("catalog", "AbC")));
        Assert.False(PlayingTrackMatcher.Matches(track, new("acceptance-music", "Native", "song")));
        Assert.False(PlayingTrackMatcher.Matches(track, null));
    }

    [AvaloniaFact]
    public async Task BatchImportPreservesOrderAndPreferenceAndDeletesOnlySelectedOccurrences()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.CreatePlaylist("Source", "one", "two", "one", "three");
        var target = fixture.CreatePlaylist("Target", "existing");
        var vm = fixture.Page(source);
        vm.IsSelectionMode = true;
        vm.Tracks[0].IsMarked = true;
        vm.Tracks[1].IsMarked = true;
        vm.TargetPlaylist = target;
        vm.InsertAtStart = true;
        Assert.True(await vm.AddSelectedTracksAsync());
        Assert.Equal(["one", "two", "existing"], OrderedIds(target));
        Assert.Equal(2, vm.ActionTracks.Count);
        Assert.True(fixture.Main.Search.InsertAtStart);
        await fixture.Host.Settings.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(vm.InsertAtStart);

        vm.InsertAtStart = false;
        Assert.True(await vm.AddSelectedTracksAsync());
        Assert.Equal(["one", "two", "existing", "one", "two"], OrderedIds(target));
        await vm.RemoveSelectedTrackAsync();
        Assert.Equal(["one", "three"], OrderedIds(source));
        Assert.Empty(vm.ActionTracks);
        Assert.False(vm.CanRemoveTracks);
        await fixture.Host.Get<LibraryService>().LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["one", "three"], OrderedIds(fixture.Playlists.GetRequired(source.Id)));
    }

    [AvaloniaFact]
    public async Task ReorderingPersistsBothSidebarAndSelectedTrackBlocks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.CreatePlaylist("A", "one", "two", "three", "four", "five");
        fixture.CreatePlaylist("B");
        fixture.CreatePlaylist("C");
        var vm = fixture.Page(first);
        vm.IsSelectionMode = true;
        vm.Tracks[1].IsMarked = true;
        vm.Tracks[3].IsMarked = true;
        await vm.ReorderTracksAsync(vm.Tracks[1], vm.Tracks[4], true);
        Assert.Equal(["one", "three", "five", "two", "four"], OrderedIds(first));
        await vm.ReorderTracksAsync(vm.Tracks[3], vm.Tracks[0], false);
        Assert.Equal(["two", "four", "one", "three", "five"], OrderedIds(first));
        vm.Filter = "three";
        Assert.False(vm.CanReorderTracks);
        vm.Filter = "";
        await fixture.Main.ReorderPlaylistsAsync(fixture.Main.Playlists[2], fixture.Main.Playlists[0], false);
        Assert.Equal(["C", "A", "B"], fixture.Main.Playlists.Select(p => p.Name));
        await fixture.Host.Settings.LoadAsync(TestContext.Current.CancellationToken);
        await fixture.Host.Get<LibraryService>().LoadAsync(TestContext.Current.CancellationToken);
        fixture.Main.SharedContext.RefreshLibrary();
        Assert.Equal(["C", "A", "B"], fixture.Main.Playlists.Select(p => p.Name));
        Assert.Equal(["two", "four", "one", "three", "five"], OrderedIds(fixture.Playlists.GetRequired(first.Id)));
    }

    [AvaloniaFact]
    public async Task PlaylistPickerFitsContentAndClosesOnlyAfterSuccessfulImport()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.CreatePlaylist("Source", "one");
        var target = fixture.CreatePlaylist("Target");
        var vm = fixture.Page(source);
        vm.SelectedTrack = vm.Tracks[0];
        vm.TargetPlaylist = target;
        await fixture.Main.Navigation.NavigateAsync(DesktopRoutes.Library, source.Id);
        var window = new MainWindow { DataContext = fixture.Main };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var control = window.GetVisualDescendants().OfType<AddToPlaylistButton>().First();
            var button = control.FindControl<Button>("AddButton")!;
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            var picker = ((Control)flyout.Content!).GetVisualDescendants().OfType<ListBox>().Single();
            Assert.InRange(picker.Bounds.Height, 40, 150);
            Assert.Equal(2, picker.ItemCount);
            var confirm = ((Control)flyout.Content!).GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmAdd");
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !flyout.IsOpen);
            Assert.Single(target.Items);

            source.Items[0].Track = source.Items[0].Track with { Title = "" };
            fixture.Main.SharedContext.RefreshLibrary();
            vm.SelectedTrack = vm.Tracks[0];
            flyout.ShowAt(button);
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => vm.HasError);
            Assert.True(flyout.IsOpen);
            Assert.Single(target.Items);
            flyout.Hide();
            for (var i = 0; i < 20; i++) fixture.CreatePlaylist($"Extra {i}");
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();
            Assert.InRange(picker.Bounds.Height, 200, 240);
            flyout.Hide();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HighlightAndLocateUseResolvedSourceAcrossPlaylistsAndQueueAndSurvivePause()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.CreatePlaylist("Playback", "one", "two");
        var alias = fixture.CreatePlaylist("Alias");
        fixture.Playlists.Add(alias.Id, HostFixture.Track("unrelated") with
        {
            Title = "Different metadata",
            ExternalIds = [new("acceptance-music", "one", "track-id")]
        });
        fixture.Playlists.Add(alias.Id, HostFixture.Track("wrong-kind") with
        {
            Title = "one", ExternalIds = [new("acceptance-music", "one", "song")]
        });
        fixture.Main.SharedContext.RefreshLibrary();
        var vm = fixture.Page(alias);
        var sourceVm = fixture.Page(source);
        await sourceVm.PlayAsync(sourceVm.Tracks[0]);
        fixture.Main.Playback.UpdateSnapshot();
        Assert.True(vm.Tracks[0].IsCurrent);
        Assert.False(vm.Tracks[1].IsCurrent);
        Assert.True(vm.HasPlayingTrack);
        var session = fixture.Host.Get<PlaybackSessionService>();
        await session.PauseAsync(TestContext.Current.CancellationToken);
        fixture.Main.Playback.UpdateSnapshot();
        Assert.True(vm.Tracks[0].IsCurrent);
        vm.Filter = "wrong-kind";
        var view = new LibraryView { DataContext = vm };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        try
        {
            view.FindControl<Button>("LocatePlayingButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", vm.Filter);
            Assert.NotNull(view.FindControl<ListBox>("TrackList")!.ContainerFromItem(vm.Tracks[0]));
            await using var queue = new PlaybackQueueViewModel(fixture.Main.SharedContext, fixture.Main.Playback);
            Assert.True(queue.Tracks[0].IsCurrent);
            Assert.False(queue.Tracks[1].IsCurrent);
            queue.Filter = "two";
            var queueView = new QueueDrawerView { DataContext = queue };
            window.Content = queueView;
            Dispatcher.UIThread.RunJobs();
            queueView.FindControl<Button>("LocatePlayingButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", queue.Filter);
            await session.StopAsync(TestContext.Current.CancellationToken);
            fixture.Main.Playback.UpdateSnapshot();
            Assert.False(vm.HasPlayingTrack);
            Assert.False(queue.HasPlayingTrack);
            Assert.All(vm.Tracks, row => Assert.False(row.IsCurrent));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task PointerDraggingReordersTracksAndSidebarAndMultiSelectDoesNotStartPlayback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var playlist = fixture.CreatePlaylist("A", "one", "two", "three");
        fixture.CreatePlaylist("B");
        fixture.CreatePlaylist("C");
        var vm = fixture.Page(playlist);
        await fixture.Main.Navigation.NavigateAsync(DesktopRoutes.Library, playlist.Id);
        var window = new MainWindow { DataContext = fixture.Main };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var view = window.GetVisualDescendants().OfType<LibraryView>().Single();
            var list = view.FindControl<ListBox>("TrackList")!;
            Assert.False(vm.CanReorderTracks);
            Drag(window, list, 0, 2);
            Assert.Equal(["one", "two", "three"], OrderedIds(playlist));
            vm.IsSelectionMode = true;
            Dispatcher.UIThread.RunJobs();
            Drag(window, list, 0, 2);
            await UntilAsync(() => vm.CanInteract);
            Assert.Equal(["two", "three", "one"], OrderedIds(playlist));
            vm.IsSelectionMode = true;
            Dispatcher.UIThread.RunJobs();
            var item = (Control)list.ContainerFromIndex(0)!;
            var point = item.TranslatePoint(new Point(140, item.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.True(vm.Tracks[0].IsMarked);
            Assert.Null(fixture.Main.Playback.Snapshot.CurrentTrack);
            var checkbox = item.GetVisualDescendants().OfType<CheckBox>().Single();
            var checkPoint = checkbox.TranslatePoint(new Point(10, checkbox.Bounds.Height / 2), window)!.Value;
            window.MouseDown(checkPoint, MouseButton.Left);
            window.MouseUp(checkPoint, MouseButton.Left);
            Assert.False(vm.Tracks[0].IsMarked);
            vm.IsSelectionMode = false;
            Drag(window, window.FindControl<ListBox>("PlaylistList")!, 0, 2);
            await UntilAsync(() => fixture.Main.CanInteract);
            Assert.Equal(["B", "C", "A"], fixture.Main.Playlists.Select(p => p.Name));
            Assert.Equal(playlist.Id, fixture.Main.SelectedPlaylist?.Id);

            await vm.PlayAsync(vm.Tracks[0]);
            vm.SelectedTrack = vm.Tracks[1];
            Dispatcher.UIThread.RunJobs();
            var currentPresenter = Presenter(list, 0);
            var selectedPresenter = Presenter(list, 1);
            Assert.Equal(currentPresenter.Bounds.Size, selectedPresenter.Bounds.Size);
            Assert.Equal(currentPresenter.Background, selectedPresenter.Background);
            var filter = view.FindControl<TextBox>("LibraryFilter")!;
            var manage = view.FindControl<Button>("ManageTracksButton")!;
            Assert.Equal(filter.TranslatePoint(default, window)!.Value.Y + filter.Bounds.Height / 2,
                manage.TranslatePoint(default, window)!.Value.Y + manage.Bounds.Height / 2, 1);
            Assert.Null(view.FindControl<Button>("TrackActionsButton"));
            var locate = view.FindControl<Button>("LocatePlayingButton")!;
            var bar = window.FindControl<Border>("PlaybackBar")!;
            Assert.True(locate.TranslatePoint(default, window)!.Value.Y + locate.Bounds.Height + 8 <= bar.TranslatePoint(default, window)!.Value.Y);
            CapturePreview(window, "playlist");
            vm.IsSelectionMode = true;
            vm.Tracks[2].IsMarked = true;
            Dispatcher.UIThread.RunJobs();
            var batchAdd = view.GetVisualDescendants().OfType<AddToPlaylistButton>().Single(control => control.IsGhost);
            var batchAddButton = batchAdd.FindControl<Button>("AddButton")!;
            Assert.True(batchAdd.IsGhost);
            Assert.True(batchAdd.IsCompact);
            Assert.Contains("ghost", batchAddButton.Classes);
            Assert.Contains("compact", batchAddButton.Classes);
            Assert.Equal(currentPresenter.Bounds.Size, Presenter(list, 2).Bounds.Size);
            Assert.Equal(currentPresenter.Background, Presenter(list, 2).Background);
            Assert.NotEqual(currentPresenter.Background, selectedPresenter.Background);
            CapturePreview(window, "playlist-manage");
        }
        finally { window.Close(); }
    }

    private static ContentPresenter Presenter(ListBox list, int index) => list.ContainerFromIndex(index)!
        .GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    private static void CapturePreview(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("LEMONNEO_PLAYLIST_PREVIEW") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Drag(Window window, ListBox list, int sourceIndex, int targetIndex)
    {
        Dispatcher.UIThread.RunJobs();
        var source = (Control)list.ContainerFromIndex(sourceIndex)!;
        var target = (Control)list.ContainerFromIndex(targetIndex)!;
        var from = source.TranslatePoint(new Point(90, source.Bounds.Height / 2), window)!.Value;
        var to = target.TranslatePoint(new Point(90, target.Bounds.Height - 6), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point(from.X, from.Y + 10), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, timeout.Token); }
    }

    private static string[] OrderedIds(Playlist playlist) => playlist.Items.OrderBy(i => FractionalPlaylistOrderingService.Parse(i.SortKey)).Select(i => i.Track.Id.RemoteId).ToArray();

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryPaths _paths = new();
        private readonly AppearanceService _appearance;
        public DesktopHost Host { get; }
        public MainViewModel Main { get; }
        public LocalizationService Text { get; }
        public PlaylistService Playlists => Host.Get<PlaylistService>();
        private Fixture()
        {
            Host = new(_paths, new(_paths.PluginDirectory, _paths.MusicLibraryDirectory), [new TestPlayer()], [new TestMusic()]);
            var scope = Host.Settings.ForPackage(PackageNames.Desktop);
            DesktopPreferences.Register(scope);
            Text = new(scope);
            _appearance = new(Avalonia.Application.Current!, scope);
            Main = new(Host, Text, _appearance, Host.Get<ArtworkService>());
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.Main.InitializeAsync();
            Assert.True(fixture.Main.SharedContext.IsInitialized);
            return fixture;
        }
        public Playlist CreatePlaylist(string name, params string[] tracks)
        {
            var playlist = Playlists.Create(name);
            foreach (var track in tracks) Playlists.Add(playlist.Id, HostFixture.Track(track));
            Main.SharedContext.RefreshLibrary();
            return playlist;
        }
        public LibraryViewModel Page(Playlist playlist) => Main.Navigation.GetPage<LibraryViewModel>(DesktopRoutes.Library, playlist.Id);
        public async ValueTask DisposeAsync()
        {
            await Main.DisposeAsync();
            _appearance.Dispose(); Text.Dispose();
            await Host.DisposeAsync();
            _paths.Dispose();
        }
    }
}
