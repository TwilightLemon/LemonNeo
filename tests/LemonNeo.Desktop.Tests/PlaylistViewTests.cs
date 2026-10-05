using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Core;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class PlaylistViewTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3600, "1:00:00")]
    [InlineData(86399, "23:59:59")]
    [InlineData(86400, "24:00:00")]
    [InlineData(175298, "48:41:38")]
    [InlineData(360065, "100:01:05")]
    public void DurationDisplaysTotalHoursAcrossMultipleDays(int seconds, string expected) =>
        Assert.Equal(expected, TrackRow.FormatTime(TimeSpan.FromSeconds(seconds)));

    [AvaloniaFact]
    public async Task LargePlaylistSummariesRetainAllHoursAndUpdateWithTrackMetadata()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths,
            new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await appearance.SetAsync(new UiPreferences(Language: LanguagePreference.Chinese, ReduceMotion: true));
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await shell.InitializeAsync();
        Assert.False(shell.HasError, shell.Error);
        var playlists = runtime.Get<PlaylistService>();
        var playlist = playlists.Create("Large playlist");
        for (var i = 0; i < 788; i++) playlists.Add(playlist.Id, Track(i.ToString(), 222));
        shell.SharedContext.RefreshLibrary();
        var page = shell.Navigation.GetPage<LibraryViewModel>(DesktopRoutes.Library, playlist.Id);
        var matchPage = shell.Navigation.GetPage<MatchPlaylistPageViewModel>(DesktopRoutes.MatchPlaylist, playlist.Id);
        Assert.Equal("788 首歌曲 · 48:35:36", page.PageSummary);
        Assert.Equal("Large playlist · 788 首歌曲 · 48:35:36", matchPage.PageSummary);

        var unknown = playlists.Add(playlist.Id, Track("unknown", null));
        shell.SharedContext.RefreshLibrary();
        Assert.Equal("789 首歌曲 · 48:35:36", page.PageSummary);

        playlists.UpdateTrack(playlist.Id, unknown.Id, unknown.Track with { Duration = TimeSpan.FromMinutes(2) });
        shell.SharedContext.RefreshLibrary();
        Assert.Equal("789 首歌曲 · 48:37:36", page.PageSummary);
        Assert.Equal("Large playlist · 789 首歌曲 · 48:37:36", matchPage.PageSummary);
    }

    [AvaloniaFact]
    public async Task SidebarUpdatesNamesAndCountsWhilePreservingSelectionAndNavigation()
    {
        using var paths = new TestPaths();
        await using var runtime = new DesktopRuntime(paths,
            new DesktopOptions(Path.Combine(paths.DataDirectory, "plugins"), paths.MusicLibraryDirectory));
        var text = new LocalizationService();
        using var appearance = new AppearanceService(Avalonia.Application.Current!, text, new AppearanceTests.MemoryStore());
        await appearance.SetAsync(new UiPreferences(Language: LanguagePreference.English, ReduceMotion: true));
        await using var shell = new MainViewModel(runtime, text, appearance, runtime.Get<ArtworkService>());
        await shell.InitializeAsync();
        Assert.False(shell.HasError, shell.Error);
        var playlists = runtime.Get<PlaylistService>();
        var playlist = playlists.Create("Alpha");
        playlists.Create("Middle");
        shell.SharedContext.RefreshLibrary();
        var selected = shell.Playlists.Single(p => p.Id == playlist.Id);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            await shell.Navigation.NavigateAsync(DesktopRoutes.Library, playlist.Id);
            var page = Assert.IsType<LibraryViewModel>(shell.Navigation.Current);
            var list = window.FindControl<ListBox>("PlaylistList")!;
            var loadedPages = shell.Navigation.LoadedPageCount;

            void AssertSidebar(string name, int count)
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Same(selected, shell.SelectedPlaylist);
                Assert.Same(selected, list.SelectedItem);
                Assert.Same(page, shell.Navigation.Current);
                Assert.Equal(loadedPages, shell.Navigation.LoadedPageCount);
                var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(shell.Playlists.IndexOf(selected)));
                var labels = container.GetVisualDescendants().OfType<TextBlock>().Select(label => label.Text).ToArray();
                Assert.Contains(name, labels);
                Assert.Contains(count.ToString(), labels);
                Assert.Equal(name, page.PageTitle);
                Assert.Equal(count, page.Tracks.Count);
            }

            AssertSidebar("Alpha", 0);
            await page.UpdatePlaylistAsync(playlist.Id, "Zulu", "Renamed");
            Assert.False(page.HasError, page.Error);
            Assert.Equal(1, shell.Playlists.IndexOf(selected));
            AssertSidebar("Zulu", 0);

            await page.AddTrackAsync(playlist.Id, Track("added", 120));
            Assert.False(page.HasError, page.Error);
            AssertSidebar("Zulu", 1);
            Assert.Equal("1 tracks · 2:00", page.PageSummary);

            page.SelectedTrack = Assert.Single(page.Tracks);
            await page.RemoveSelectedTrackAsync();
            Assert.False(page.HasError, page.Error);
            AssertSidebar("Zulu", 0);

            await page.ReloadLibraryAsync();
            Assert.False(page.HasError, page.Error);
            Assert.NotSame(playlist, page.SelectedPlaylist);
            AssertSidebar("Zulu", 0);

            await page.DeletePlaylistAsync(playlist.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.False(page.HasError, page.Error);
            Assert.DoesNotContain(selected, shell.Playlists);
            Assert.Null(shell.SelectedPlaylist);
            Assert.Null(list.SelectedItem);
            Assert.Equal("Middle", Assert.Single(shell.Playlists).Name);
        }
        finally { window.Close(); }
    }

    private static Track Track(string id, int? seconds) => new()
    {
        Id = new("test", id), Title = id,
        Duration = seconds is { } duration ? TimeSpan.FromSeconds(duration) : null
    };
}
