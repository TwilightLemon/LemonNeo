using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application.Library;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Library;
using LemonNeo.Desktop.ViewModels.Metadata;
using LemonNeo.Desktop.ViewModels.Shared;
using LemonNeo.Desktop.ViewModels.Shell;
using Xunit;

namespace LemonNeo.Tests;

public sealed class NavigationTests
{
    [AvaloniaFact]
    public async Task ManyNavigationsKeepOneViewAndBoundPageModels()
    {
        await using var desktop = new TrackListTests.TestDesktop();
        await desktop.Host.Settings.LoadAsync();
        desktop.Context.Settings.Apply<AppearancePreferences>(p => p with { ReduceMotion = true });
        await using var shell = new MainViewModel(desktop.Host, desktop.Text, desktop.Appearance,
            desktop.Host.Get<ArtworkService>());
        shell.SharedContext.IsInitialized = true;
        var nav = shell.Navigation;
        var window = new Window { Width = 1000, Height = 700, Content = nav.Host };
        window.Show();
        try
        {
            for (var i = 0; i < 500; i++)
            {
                await nav.NavigateAsync(DesktopRoutes.Search);
                await nav.NavigateAsync(DesktopRoutes.Library);
            }
            window.UpdateLayout();
            Assert.Single(nav.Host.NavigationStack);
            Assert.Equal(20, nav.HistoryEntryCount);
            Assert.Equal(2, nav.LoadedPageCount);
            Assert.NotEmpty(nav.Host.GetVisualDescendants());

            var playlists = desktop.Host.Get<PlaylistService>();
            for (var i = 0; i < 30; i++)
            {
                var playlist = playlists.Create($"Playlist {i}");
                shell.SharedContext.RefreshLibrary();
                await nav.NavigateAsync(DesktopRoutes.Library, playlist.Id);
            }
            window.UpdateLayout();
            Assert.Single(nav.Host.NavigationStack);
            Assert.Equal(20, nav.HistoryEntryCount);
            Assert.InRange(nav.LoadedPageCount, 1, 6);
            Assert.NotEmpty(nav.Host.GetVisualDescendants());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BackForwardRestoresFilterAndEditorDraftAndClearsForward()
    {
        await using var desktop = new TrackListTests.TestDesktop();
        await desktop.Host.Settings.LoadAsync();
        desktop.Context.Settings.Apply<AppearancePreferences>(p => p with { ReduceMotion = true });
        var playlists = desktop.Host.Get<PlaylistService>();
        var playlist = playlists.Create("Draft test");
        playlists.AddRange(playlist.Id, [new Track { Id = new MusicItemId("test", "1"), Title = "Original" }]);
        await using var shell = new MainViewModel(desktop.Host, desktop.Text, desktop.Appearance,
            desktop.Host.Get<ArtworkService>());
        shell.SharedContext.IsInitialized = true;
        shell.SharedContext.RefreshLibrary();
        var nav = shell.Navigation;
        var window = new Window { Width = 1000, Height = 700, Content = nav.Host };
        window.Show();
        try
        {
            await nav.NavigateAsync(DesktopRoutes.Library, playlist.Id);
            var library = Assert.IsType<LibraryViewModel>(nav.Current);
            library.Filter = "Original";
            library.IsSelectionMode = true;
            library.SelectedTrack = Assert.Single(library.Tracks);
            library.SelectedTrack.IsMarked = true;
            await nav.OpenEditorAsync(library.SelectedTrack);
            var editor = Assert.IsType<TrackEditorPageViewModel>(nav.Current);
            editor.Editor!.Title = "Unsaved title";

            await nav.GoBackAsync();
            library = Assert.IsType<LibraryViewModel>(nav.Current);
            Assert.Equal("Original", library.Filter);
            Assert.True(library.IsSelectionMode);
            Assert.Equal("Original", library.SelectedTrack?.Title);
            Assert.True(library.SelectedTrack?.IsMarked);
            await nav.GoForwardAsync();
            Assert.Equal("Unsaved title", Assert.IsType<TrackEditorPageViewModel>(nav.Current).Editor?.Title);

            await nav.GoBackAsync();
            await nav.NavigateAsync(DesktopRoutes.Search);
            Assert.False(nav.CanGoForward);
            Assert.Equal(DesktopRoutes.Search, nav.Current.Route);
            Assert.Single(nav.Host.NavigationStack);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EvictedPlaylistRestoresStateAndDeletedPlaylistLeavesHistory()
    {
        await using var desktop = new TrackListTests.TestDesktop();
        await desktop.Host.Settings.LoadAsync();
        desktop.Context.Settings.Apply<AppearancePreferences>(p => p with { ReduceMotion = true });
        var playlists = desktop.Host.Get<PlaylistService>();
        var first = playlists.Create("First");
        playlists.AddRange(first.Id, [new Track { Id = new MusicItemId("test", "1"), Title = "Remember me" }]);
        await using var shell = new MainViewModel(desktop.Host, desktop.Text, desktop.Appearance,
            desktop.Host.Get<ArtworkService>());
        shell.SharedContext.IsInitialized = true;
        shell.SharedContext.RefreshLibrary();
        var nav = shell.Navigation;
        var window = new Window { Width = 1000, Height = 700, Content = nav.Host };
        window.Show();
        try
        {
            await nav.NavigateAsync(DesktopRoutes.Library, first.Id);
            var original = Assert.IsType<LibraryViewModel>(nav.Current);
            original.Filter = "Remember";
            original.SelectedTrack = Assert.Single(original.Tracks);
            await nav.NavigateAsync(DesktopRoutes.Search);
            playlists.AddRange(first.Id,
                [new Track { Id = new MusicItemId("test", "2"), Title = "Remember another" }]);
            shell.SharedContext.RefreshLibrary();
            Assert.Single(original.Tracks);
            await nav.GoBackAsync();
            Assert.Equal(2, original.Tracks.Count);
            for (var i = 0; i < 7; i++)
            {
                var next = playlists.Create($"Next {i}");
                shell.SharedContext.RefreshLibrary();
                await nav.NavigateAsync(DesktopRoutes.Library, next.Id);
            }
            Assert.InRange(nav.LoadedPageCount, 1, 6);
            for (var i = 0; i < 7; i++) await nav.GoBackAsync();
            var restored = Assert.IsType<LibraryViewModel>(nav.Current);
            Assert.NotSame(original, restored);
            Assert.Equal("Remember", restored.Filter);
            Assert.Equal("Remember me", restored.SelectedTrack?.Title);

            await nav.NavigateAsync(DesktopRoutes.MatchPlaylist, first.Id);
            Assert.IsType<MatchPlaylistPageViewModel>(nav.Current);
            await nav.GoBackAsync();
            playlists.Delete(first.Id);
            shell.SharedContext.RefreshLibrary();
            Assert.False(nav.CanGoForward);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task BackRestoresTrackListScrollOffset()
    {
        await using var desktop = new TrackListTests.TestDesktop();
        await desktop.Host.Settings.LoadAsync();
        desktop.Context.Settings.Apply<AppearancePreferences>(p => p with { ReduceMotion = true });
        var playlists = desktop.Host.Get<PlaylistService>();
        var playlist = playlists.Create("Long playlist");
        playlists.AddRange(playlist.Id, Enumerable.Range(0, 100).Select(i =>
            new Track { Id = new MusicItemId("test", i.ToString()), Title = $"Track {i}" }).ToArray());
        await using var shell = new MainViewModel(desktop.Host, desktop.Text, desktop.Appearance,
            desktop.Host.Get<ArtworkService>());
        shell.SharedContext.IsInitialized = true;
        shell.SharedContext.RefreshLibrary();
        var nav = shell.Navigation;
        var window = new Window { Width = 1000, Height = 700, Content = nav.Host };
        window.Show();
        try
        {
            await nav.NavigateAsync(DesktopRoutes.Library, playlist.Id);
            window.UpdateLayout();
            var list = nav.Host.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "TrackList");
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroller.Offset = new Vector(0, 300);
            Assert.InRange(scroller.Offset.Y, 250, 350);
            await nav.NavigateAsync(DesktopRoutes.Search);
            await nav.GoBackAsync();
            window.UpdateLayout();
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            list = nav.Host.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "TrackList");
            scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.InRange(scroller.Offset.Y, 250, 350);
        }
        finally
        {
            window.Close();
        }
    }
}
