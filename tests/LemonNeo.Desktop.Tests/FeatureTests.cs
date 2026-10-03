using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LemonNeo.Application;
using LemonNeo.Configuration;
using LemonNeo.Core;
using LemonNeo.Desktop.Controls;
using LemonNeo.Desktop.Infrastructure;
using LemonNeo.Desktop.ViewModels;
using LemonNeo.Desktop.Views;
using LemonNeo.Persistence;
using LemonNeo.Plugin.Runtime;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class FeatureTests
{
    [AvaloniaFact]
    public async Task SearchUsesAllProvidersAndSupportedSourcesWithAutomaticPlaybackAndPersistsTracks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        Assert.DoesNotContain(vm.AuthenticationProviders, p => p.Id == "search-only");
        Assert.DoesNotContain(vm.PlaylistProviders, p => p.Id == "search-only");
        Assert.DoesNotContain(vm.PlaybackSources, p => p.Id == "search-only");
        var supported = new Track { Id = new("search-only", "supported"), Title = "Qualified source",
            ExternalIds = [new("TEST-NET", "qualified", "SONG-ID")] };
        var native = new Track { Id = new("TEST-SERVICE", "native"), Title = "Native source" };
        fixture.SearchOnly.Tracks = [supported, native,
            new() { Id = new("search-only", "wrong-kind"), Title = "Wrong ID kind", ExternalIds = [new("test-net", "album", "album-id")] },
            new() { Id = new("search-only", "unsupported"), Title = "Unsupported source", ExternalIds = [new("unavailable-net", "track", "song-id")] }];
        await vm.CreatePlaylistAsync("Saved search", "Description");
        var playlistId = vm.SelectedPlaylist!.Id;
        vm.Navigate(DesktopPage.Search);
        vm.SearchQuery = "all";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        Assert.Equal(4, vm.Tracks.Count);
        Assert.Contains(vm.Tracks, row => row.Track.Id == supported.Id);
        Assert.Contains(vm.Tracks, row => row.Track.Id == native.Id);
        Assert.Contains(vm.Tracks, row => row.Track.Id == fixture.Provider.First.Id);
        Assert.Contains(vm.Tracks, row => row.Track.Id == fixture.Provider.Second.Id);
        Assert.NotNull(fixture.SearchOnly.LastSearch);
        Assert.Null(fixture.SearchOnly.LastSearch.SourceNamespace);
        Assert.Equal(new[] { "other-net", "test-net" }, fixture.Provider.Searches.Select(request => request.SourceNamespace).Order());
        vm.SelectedTrack = vm.Tracks.Single(row => row.Track.Id == fixture.Provider.First.Id);
        await vm.AddToPlaylistCommand.ExecuteAsync(null);
        Assert.Equal(DesktopPage.Search, vm.Page);
        Assert.Equal(fixture.Provider.First.Id, Assert.Single((await fixture.LoadLibraryAsync()).Playlists.Single().Items).Track.Id);

        vm.SelectedTrack = vm.Tracks.Single(row => row.Track.Id == fixture.Provider.Second.Id);
        vm.SelectedPlaybackSource = vm.PlaybackSources.Single(p => p.Id == fixture.Provider.Id);
        await vm.PlaySelectedCommand.ExecuteAsync(null);
        Assert.True(vm.IsPlaying);
        Assert.Equal("two", fixture.Runtime.Get<PlaybackSessionService>().Queue.CurrentItem!.Track.Id.RemoteId);
        Assert.Null(fixture.Runtime.Get<PlaybackSessionService>().Queue.Request.RequiredSourceProviderId);
        await vm.AddTrackAsync(playlistId, fixture.Provider.Second);
        Assert.Equal(2, (await fixture.LoadLibraryAsync()).Playlists.Single().Items.Count);
    }

    [Fact]
    public async Task SearchWithNoPlaybackProvidersIsEmptyWhileConsoleSearchRemainsUnrestricted()
    {
        var provider = new SearchOnlyProvider
        {
            Tracks = [new() { Id = new("search-only", "metadata"), Title = "Metadata only" }]
        };
        var registry = new ProviderRegistry([provider]);
        var search = new SearchService(registry, registry);
        Assert.Empty(await search.SearchForPlaybackAsync("metadata", TestContext.Current.CancellationToken));
        Assert.Null(provider.LastSearch);
        var unrestricted = await search.SearchAsync("metadata", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(provider.Tracks[0].Id, Assert.Single(Assert.Single(unrestricted).Tracks).Id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.SearchForPlaybackAsync("metadata", cancellation.Token));
    }

    [AvaloniaFact]
    public async Task CookieLoginFailureClearsInputAndGroupedRemoteImportsPersist()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.Cookie = "reject";
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Equal("", vm.Cookie);
        Assert.False(fixture.Provider.State.IsAuthenticated);
        Assert.False(vm.IsBusy);
        vm.Cookie = "a-secret-cookie";
        await vm.LoginCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        Assert.Equal("", vm.Cookie);
        Assert.True(vm.CanLogout);
        Assert.Contains("user-42", vm.AuthenticationAccount);
        await vm.RemotePlaylistsCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.RemotePlaylists.Count);
        Assert.Contains(vm.RemotePlaylists, row => row.Collection == "Followed" && !row.CanImport);
        Assert.Equal("Owned", vm.SelectedRemotePlaylist!.Collection);
        await vm.ImportRemoteCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        var imported = Assert.Single((await fixture.LoadLibraryAsync()).Playlists);
        Assert.Equal("Remote description", imported.Description);
        Assert.Equal(2, imported.Items.Count);
        vm.RemotePlaylistId = "missing";
        await vm.ImportRemoteIdCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Single((await fixture.LoadLibraryAsync()).Playlists);
        await vm.LogoutCommand.ExecuteAsync(null);
        Assert.False(fixture.Provider.State.IsAuthenticated);
        Assert.Empty(vm.RemotePlaylists);
        Assert.DoesNotContain("a-secret-cookie", await File.ReadAllTextAsync(fixture.Paths.AppConfigurationFile));
    }

    [AvaloniaFact]
    public async Task PlaylistEditsReorderingRemovalAndDuplicateTrackEditingUseItemIdentities()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.CreatePlaylistAsync("Original", "Original description");
        var playlistId = vm.SelectedPlaylist!.Id;
        await vm.AddTrackAsync(playlistId, fixture.Provider.First);
        await vm.AddTrackAsync(playlistId, fixture.Provider.Second);
        await vm.AddTrackAsync(playlistId, fixture.Provider.First);
        var firstItemId = vm.Tracks[0].PlaylistItemId;
        var duplicateItemId = vm.Tracks[2].PlaylistItemId;
        vm.Filter = "One";
        Assert.Equal(new[] { 1, 3 }, vm.Tracks.Select(t => t.Number));
        vm.SelectedTrack = vm.Tracks[1];
        await vm.MoveTrackUpCommand.ExecuteAsync(null);
        Assert.Equal(duplicateItemId, vm.SelectedTrack!.PlaylistItemId);
        Assert.Equal(2, vm.SelectedTrack.Number);
        await vm.MoveTrackAsync(playlistId, duplicateItemId!.Value, 3);
        Assert.Equal(3, vm.SelectedTrack.Number);
        await vm.MoveTrackAsync(playlistId, duplicateItemId.Value, 2);
        vm.InspectTrackCommand.Execute(null);
        var editor = Assert.IsType<TrackEditorViewModel>(vm.Editor);
        editor.Title = "Edited duplicate"; editor.Artists = "A; B"; editor.Album = "New album";
        editor.DurationSeconds = "180.25";
        editor.ExternalIds.Add(new() { Namespace = "isrc", Value = "TEST00000001" });
        await vm.SaveEditorCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        var playlist = (await fixture.LoadLibraryAsync()).Playlists.Single();
        Assert.Equal("One", playlist.Items.Single(i => i.Id == firstItemId).Track.Title);
        var changed = playlist.Items.Single(i => i.Id == duplicateItemId).Track;
        Assert.Equal("Edited duplicate", changed.Title);
        Assert.Equal(new[] { "A", "B" }, changed.Artists.Select(a => a.Name));
        Assert.Equal(TimeSpan.FromSeconds(180.25), changed.Duration);
        Assert.Equal(2, changed.ExternalIds.Count);
        vm.SelectedTrack = vm.Tracks.Single(t => t.PlaylistItemId == duplicateItemId);
        vm.OpenEditor(vm.SelectedTrack);
        vm.Editor!.ExternalIds[0].Value = "changed-native-id";
        vm.Editor.ExternalIds.RemoveAt(1);
        await vm.SaveEditorAsync();
        Assert.Single((await fixture.LoadLibraryAsync()).Playlists.Single().Items.Single(i => i.Id == duplicateItemId).Track.ExternalIds);
        vm.SelectedTrack = vm.Tracks.Single(t => t.PlaylistItemId == duplicateItemId);
        await vm.RemoveTrackCommand.ExecuteAsync(null);
        Assert.Equal(2, (await fixture.LoadLibraryAsync()).Playlists.Single().Items.Count);
        await vm.UpdatePlaylistAsync(playlistId, "Renamed", null);
        Assert.Equal("Renamed", vm.PageTitle);
        Assert.Null((await fixture.LoadLibraryAsync()).Playlists.Single().Description);
        fixture.Runtime.Get<LibraryService>().Current.Mirrors.Add(new() { PlaylistId = playlistId, ProviderId = "test-service", RemotePlaylistId = "favorites" });
        await vm.DeletePlaylistAsync(playlistId);
        var empty = await fixture.LoadLibraryAsync();
        Assert.Empty(empty.Playlists); Assert.Empty(empty.Mirrors);
    }

    [AvaloniaFact]
    public async Task NewTrackValidationAndFailedSaveKeepTheEditorAndRetryDoesNotDuplicateTheItem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.CreatePlaylistAsync("Manual");
        var id = vm.SelectedPlaylist!.Id;
        vm.NewTrackCommand.Execute(null);
        vm.Editor!.Title = "Handmade"; vm.Editor.DurationSeconds = "NaN";
        await vm.SaveEditorAsync();
        Assert.True(vm.HasError); Assert.NotNull(vm.Editor);
        Assert.Empty((await fixture.LoadLibraryAsync()).Playlists.Single().Items);
        vm.Editor.DurationSeconds = "123";
        vm.Editor.ExternalIds.Add(new() { Namespace = "isrc", Value = "TEST00000002" });
        vm.Editor.ExternalIds.Add(new() { Namespace = "ISRC", Value = "TEST00000003", Kind = "-" });
        await vm.SaveEditorAsync();
        Assert.True(vm.HasError);
        Assert.Empty((await fixture.LoadLibraryAsync()).Playlists.Single().Items);
        vm.Editor.ExternalIds.RemoveAt(1);
        var blockedPath = Path.Combine(fixture.Paths.MusicLibraryDirectory, "playlists", $"{id:N}.json.tmp");
        Directory.CreateDirectory(blockedPath);
        try
        {
            await vm.SaveEditorAsync();
            Assert.True(vm.HasError); Assert.NotNull(vm.Editor); Assert.False(vm.IsBusy);
            Assert.Equal(DesktopPage.Editor, vm.Page);
            Assert.Equal("Handmade", vm.Editor.Title);
            Assert.NotNull(vm.Editor.ItemId);
        }
        finally { Directory.Delete(blockedPath); }
        await vm.SaveEditorAsync();
        Assert.Null(vm.Error); Assert.Null(vm.Editor);
        Assert.Equal("Handmade", Assert.Single((await fixture.LoadLibraryAsync()).Playlists.Single().Items).Track.Title);
    }

    [AvaloniaFact]
    public async Task EnrichmentIsPreviewedThenAppliedAndBatchMatchingUpdatesEveryDuplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.CreatePlaylistAsync("Matching");
        var id = vm.SelectedPlaylist!.Id;
        await vm.AddTrackAsync(id, fixture.Provider.First);
        await vm.AddTrackAsync(id, fixture.Provider.First);
        vm.SelectedTrack = vm.Tracks[0];
        vm.InspectTrackCommand.Execute(null);
        vm.MetadataTargets = "test-net";
        await vm.MetadataCommand.ExecuteAsync(null);
        Assert.Single(vm.MetadataMatches);
        Assert.Equal(new[] { "test-net" }, fixture.Provider.LastMetadataTargets);
        Assert.Contains("Fixture metadata warning", vm.InspectionMessages);
        await vm.EnrichCommand.ExecuteAsync(null);
        Assert.True(vm.HasEnrichedPreview);
        Assert.Equal("One", vm.Editor!.Title);
        Assert.All((await fixture.LoadLibraryAsync()).Playlists.Single().Items, i => Assert.Equal("One", i.Track.Title));
        vm.ApplyEnrichmentCommand.Execute(null);
        Assert.Equal("Matched One", vm.Editor.Title);
        Assert.Equal(2, vm.Editor.ExternalIds.Count(row => row.Namespace == "test-net"));
        await vm.SaveEditorAsync();
        Assert.Null(vm.Error);
        Assert.Equal("Matched One", (await fixture.LoadLibraryAsync()).Playlists.Single().Items[0].Track.Title);
        Assert.Equal("One", (await fixture.LoadLibraryAsync()).Playlists.Single().Items[1].Track.Title);
        vm.SelectedTrack = vm.Tracks[0];
        vm.OpenEditor(vm.SelectedTrack);
        await vm.SaveEditorAsync();
        Assert.Null(vm.Error);
        Assert.Equal(2, (await fixture.LoadLibraryAsync()).Playlists.Single().Items[0].Track.ExternalIds.Count(row => row.Namespace == "test-net"));
        await vm.MatchPlaylistAsync(id);
        Assert.All((await fixture.LoadLibraryAsync()).Playlists.Single().Items, i => Assert.Equal("Matched album", i.Track.Album!.Title));
    }

    [AvaloniaFact]
    public async Task LyricsSourcesPlaybackAndWordHighlightsShareTheConsoleTimeline()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.SearchQuery = "one";
        await vm.SearchAsync();
        vm.SelectedTrack = vm.Tracks.Single();
        vm.InspectTrackCommand.Execute(null);
        await vm.LyricsDetailsCommand.ExecuteAsync(null);
        Assert.Contains("hello world", vm.LyricsDetails); Assert.Contains("Romanization", vm.LyricsDetails);
        await vm.SourcesCommand.ExecuteAsync(null);
        Assert.Single(vm.DiscoveredSources);
        await vm.PlaySourceCommand.ExecuteAsync(null);
        Assert.Null(vm.Error);
        Assert.Equal(fixture.Provider.Id, fixture.Runtime.Get<PlaybackSessionService>().Queue.Request.RequiredSourceProviderId);
        await WaitUntilAsync(() => vm.CurrentLyricSegments.Count == 2);
        Assert.True(vm.CurrentLyricSegments[0].Reached); Assert.False(vm.CurrentLyricSegments[1].Reached);
        await vm.SeekAsync(1.6);
        Assert.All(vm.CurrentLyricSegments, s => Assert.True(s.Reached));
        Assert.All(vm.RomanizationSegments, s => Assert.True(s.Reached));
        Assert.Equal("世界", vm.Translation);
        await vm.TogglePlaybackCommand.ExecuteAsync(null);
        Assert.False(vm.IsPlaying);
        Assert.All(vm.CurrentLyricSegments, s => Assert.True(s.Reached));
        fixture.Text.Apply(LanguagePreference.Chinese);
        Assert.Contains("提供方", vm.LyricsDetails);
        await vm.ModeCommand.ExecuteAsync("Shuffle");
        Assert.Equal(PlaybackMode.Shuffle, fixture.Runtime.Get<PlaybackSessionService>().Queue.Mode);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Empty(vm.CurrentLyricSegments); Assert.Empty(vm.RomanizationSegments); Assert.Equal("", vm.Translation);
    }

    [AvaloniaFact]
    public async Task CancelledAndFailedSearchesRestoreInteractionAndCanBeRetried()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        vm.SearchQuery = "one";
        await vm.SearchAsync();
        vm.SelectedTrack = vm.Tracks[0];
        await vm.PlayAsync(vm.SelectedTrack);
        vm.SearchQuery = "wait";
        var search = vm.SearchAsync();
        await fixture.Provider.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy); Assert.False(vm.CanInteract);
        Assert.True(vm.CanTogglePlayback); Assert.True(vm.CanSeek);
        await vm.TogglePlaybackCommand.ExecuteAsync(null);
        Assert.False(vm.IsPlaying); Assert.True(vm.IsBusy);
        await vm.SeekAsync(2);
        Assert.Equal(2, vm.PositionSeconds); Assert.True(vm.IsBusy);
        vm.CancelOperationCommand.Execute(null);
        await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.IsBusy); Assert.True(vm.CanInteract); Assert.False(vm.HasError);
        Assert.Equal(fixture.Text["Cancelled"], vm.Status);
        vm.SearchQuery = "one"; fixture.Provider.FailSearch = true;
        await vm.SearchAsync();
        Assert.True(vm.HasError); Assert.True(vm.CanInteract);
        fixture.Provider.FailSearch = false;
        await vm.SearchAsync();
        Assert.Null(vm.Error); Assert.Single(vm.Tracks);
    }

    [AvaloniaFact]
    public async Task SyncAndStartupEditorShareConsoleStorageAndKeepTheToolsPageSelected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.CreatePlaylistAsync("Local");
        vm.Navigate(DesktopPage.Tools);
        await vm.SyncCommand.ExecuteAsync("status");
        Assert.Contains("Local changes", vm.SyncDetails);
        fixture.Text.Apply(LanguagePreference.Chinese);
        Assert.Contains("本地有更改", vm.SyncDetails);
        await vm.SyncCommand.ExecuteAsync("pull");
        Assert.Equal(DesktopPage.Tools, vm.Page);
        Assert.Equal(2, (await fixture.LoadLibraryAsync()).Playlists.Count);
        await vm.SyncCommand.ExecuteAsync("push");
        Assert.Null(vm.Error);
        vm.StartupScript = "# Custom\nversion\nqueue\n";
        await vm.SaveStartupCommand.ExecuteAsync(null);
        Assert.Equal(vm.StartupScript, await File.ReadAllTextAsync(vm.StartupPath));
        var shared = fixture.Runtime.Get<StartupCommands>();
        Assert.Equal("queue", (await shared.LoadAsync())[2]);
        var import = Path.Combine(fixture.Paths.DataDirectory, "startup source.txt");
        await File.WriteAllTextAsync(import, "library\nplugins\n");
        await vm.ImportStartupScriptAsync(import);
        Assert.Equal("library\nplugins\n", await File.ReadAllTextAsync(vm.StartupPath));
        await vm.ResetStartupCommand.ExecuteAsync(null);
        Assert.Equal(StartupCommands.DefaultScript, await File.ReadAllTextAsync(vm.StartupPath));
        var parsed = DesktopOptions.Parse(["--startup", import], fixture.Paths);
        Assert.Equal(Path.GetFullPath(import), parsed.StartupFile);
        Assert.Contains("Test service", vm.Diagnostics);
        Assert.Contains(fixture.Paths.DataDirectory, vm.PathDetails);
    }

    [AvaloniaFact]
    public async Task NewViewsTabsAndFlyoutsRenderAndUpdateInBothLanguagesAndPalettes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.CreatePlaylistAsync("Example");
        await vm.AddTrackAsync(vm.SelectedPlaylist!.Id, fixture.Provider.First);
        vm.SelectedTrack = vm.Tracks[0];
        vm.OpenEditor(vm.SelectedTrack);
        await vm.InspectMetadataAsync(true);
        await vm.InspectLyricsAsync();
        await vm.InspectSourcesAsync();
        var window = new MainWindow { DataContext = vm, Width = 880, Height = 620 };
        window.Show();
        try
        {
            foreach (var language in new[] { LanguagePreference.English, LanguagePreference.Chinese })
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                {
                    await fixture.Appearance.SetAsync(new(theme, language, true));
                    foreach (var page in new[] { DesktopPage.Search, DesktopPage.Online, DesktopPage.Editor, DesktopPage.Tools })
                    {
                        vm.Navigate(page);
                        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                        var view = Assert.Single(window.GetVisualDescendants().OfType<UserControl>(), v => v.IsEffectivelyVisible);
                        var tabs = view.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
                        for (var index = 0; index < (tabs?.ItemCount ?? 1); index++)
                        {
                            if (tabs is not null) tabs.SelectedIndex = index;
                            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                            using var rendered = new RenderTargetBitmap(new PixelSize(880, 620));
                            rendered.Render(window);
                            Assert.True(view.Bounds.Width > 300 && view.Bounds.Height > 200);
                            if (Environment.GetEnvironmentVariable("LEMONNEO_PREVIEW_DIRECTORY") is { Length: > 0 } directory)
                            {
                                Directory.CreateDirectory(directory);
                                rendered.Save(Path.Combine(directory, $"Full-{page}-{index}-{language}-{theme}.png"), PngBitmapEncoderOptions.Default);
                            }
                        }
                    }
                }
            vm.CloseEditorCommand.Execute(null);
            vm.SelectedPlaylist = vm.Playlists.First();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var action = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Flyout is MenuFlyout);
            var flyout = Assert.IsType<MenuFlyout>(action.Flyout);
            flyout.ShowAt(action);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(flyout.Items.OfType<MenuItem>(), item => item.Command == vm.NewTrackCommand && item.IsEnabled);
            flyout.Hide();
        }
        finally { window.Close(); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, cancellation.Token); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public TestPaths Paths { get; } = new();
        public FeatureProvider Provider { get; }
        public SearchOnlyProvider SearchOnly { get; } = new();
        public DesktopRuntime Runtime { get; }
        public LocalizationService Text { get; } = new();
        public AppearanceService Appearance { get; }
        public MainViewModel ViewModel { get; }
        private Fixture()
        {
            Provider = new(Paths);
            Runtime = new(Paths, new(Path.Combine(AppContext.BaseDirectory, "plugins"), Paths.MusicLibraryDirectory),
                [new DesktopTests.TestPlayerProvider()], [Provider, SearchOnly]);
            Appearance = new(Avalonia.Application.Current!, Text, Runtime.Get<IAppConfigurationStore>());
            ViewModel = new(Runtime, Text, Appearance, Runtime.Get<ArtworkService>());
        }
        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await fixture.ViewModel.InitializeAsync();
            Assert.Null(fixture.ViewModel.Error);
            await fixture.Appearance.SetAsync(new(ThemePreference.Light, LanguagePreference.English, true));
            return fixture;
        }
        public Task<MusicLibrary> LoadLibraryAsync() => new JsonLibraryRepository(Paths.MusicLibraryDirectory).LoadAsync();
        public async ValueTask DisposeAsync()
        {
            await ViewModel.DisposeAsync();
            await Runtime.DisposeAsync();
            Paths.Dispose();
        }
    }
}
