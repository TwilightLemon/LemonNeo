using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
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
using LemonNeo.Plugin.Abstractions;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class FeatureTests
{
    [AvaloniaFact]
    public async Task SearchUsesSupportedSourcesAndLibraryAndPlaylistsAlwaysUseAutomaticPlayback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        Assert.DoesNotContain(vm.Online.AuthenticationProviders, p => p.Id == "search-only");
        Assert.DoesNotContain(vm.Online.PlaylistProviders, p => p.Id == "search-only");
        var supported = new Track { Id = new("search-only", "supported"), Title = "Qualified source",
            ExternalIds = [new("TEST-NET", "qualified", "SONG-ID")] };
        var native = new Track { Id = new("TEST-SERVICE", "native"), Title = "Native source" };
        fixture.SearchOnly.Tracks = [supported, native,
            new() { Id = new("search-only", "wrong-kind"), Title = "Wrong ID kind", ExternalIds = [new("test-net", "album", "album-id")] },
            new() { Id = new("search-only", "unsupported"), Title = "Unsupported source", ExternalIds = [new("unavailable-net", "track", "song-id")] }];
        await vm.Library.CreatePlaylistAsync("Saved search", "Description");
        var playlistId = vm.SelectedPlaylist!.Id;
        await vm.Navigation.NavigateAsync(DesktopRoutes.Search);
        vm.Search.SearchQuery = "all";
        await vm.Search.SearchCommand.ExecuteAsync(null);
        Assert.Null(vm.Search.Error);
        Assert.Equal(4, vm.Search.Tracks.Count);
        Assert.Contains(vm.Search.Tracks, row => row.Track.Id == supported.Id);
        Assert.Contains(vm.Search.Tracks, row => row.Track.Id == native.Id);
        Assert.Contains(vm.Search.Tracks, row => row.Track.Id == fixture.Provider.First.Id);
        Assert.Contains(vm.Search.Tracks, row => row.Track.Id == fixture.Provider.Second.Id);
        Assert.NotNull(fixture.SearchOnly.LastSearch);
        Assert.Null(fixture.SearchOnly.LastSearch.SourceNamespace);
        Assert.Equal(new[] { "other-net", "test-net" }, fixture.Provider.Searches.Select(request => request.SourceNamespace).Order());
        vm.Search.SelectedTrack = vm.Search.Tracks.Single(row => row.Track.Id == fixture.Provider.First.Id);
        await vm.Search.AddToPlaylistCommand.ExecuteAsync(null);
        Assert.Equal(DesktopRoutes.Search, vm.Navigation.Current.Route);
        Assert.Equal(fixture.Provider.First.Id, Assert.Single((await fixture.LoadLibraryAsync()).Playlists.Single().Items).Track.Id);

        vm.Search.SelectedTrack = vm.Search.Tracks.Single(row => row.Track.Id == fixture.Provider.Second.Id);
        await vm.Search.PlaySelectedCommand.ExecuteAsync(null);
        Assert.True(vm.Playback.IsPlaying);
        Assert.Equal("two", fixture.Runtime.Get<PlaybackSessionService>().Queue.CurrentItem!.Track.Id.RemoteId);
        Assert.Null(fixture.Runtime.Get<PlaybackSessionService>().Queue.Request.RequiredSourceProviderId);
        await vm.Library.AddTrackAsync(playlistId, fixture.Provider.Second);
        Assert.Equal(2, (await fixture.LoadLibraryAsync()).Playlists.Single().Items.Count);

        var session = fixture.Runtime.Get<PlaybackSessionService>();
        // Starting library playback resets a restored source restriction to automatic selection.
        session.Queue.SetRequest(new(RequiredSourceProviderId: "search-only"));
        await vm.Navigation.NavigateAsync(DesktopRoutes.Library, playlistId);
        vm.Library.SelectedTrack = vm.Library.Tracks.Single(row => row.Track.Id == fixture.Provider.Second.Id);
        await vm.Library.PlaySelectedCommand.ExecuteAsync(null);
        Assert.Null(vm.Playback.Error);
        Assert.Equal(fixture.Provider.Second.Id, session.Queue.CurrentItem!.Track.Id);
        Assert.Null(session.Queue.Request.RequiredSourceProviderId);
        session.Queue.SetRequest(new(RequiredSourceProviderId: "search-only"));
        await vm.Navigation.NavigateAsync(DesktopRoutes.Library);
        await vm.Library.PlayAllCommand.ExecuteAsync(null);
        Assert.Null(vm.Playback.Error);
        Assert.Equal(2, session.Queue.Items.Count);
        Assert.Null(session.Queue.Request.RequiredSourceProviderId);
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
        vm.Online.Cookie = "reject";
        await vm.Online.LoginCommand.ExecuteAsync(null);
        Assert.True(vm.Online.HasError);
        Assert.Equal("", vm.Online.Cookie);
        Assert.False(fixture.Provider.State.IsAuthenticated);
        Assert.False(vm.Online.IsBusy);
        vm.Online.Cookie = "a-secret-cookie";
        await vm.Online.LoginCommand.ExecuteAsync(null);
        Assert.Null(vm.Online.Error);
        Assert.Equal("", vm.Online.Cookie);
        Assert.True(vm.Online.CanLogout);
        Assert.Contains("user-42", vm.Online.AuthenticationAccount);
        await vm.Online.RemotePlaylistsCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Online.RemotePlaylists.Count);
        Assert.Contains(vm.Online.RemotePlaylists, row => row.Collection == "Followed" && !row.CanImport);
        Assert.Equal("Owned", vm.Online.SelectedRemotePlaylist!.Collection);
        await vm.Online.ImportRemoteCommand.ExecuteAsync(null);
        Assert.Null(vm.Online.Error);
        var imported = Assert.Single((await fixture.LoadLibraryAsync()).Playlists);
        Assert.Equal("Remote description", imported.Description);
        Assert.Equal(2, imported.Items.Count);
        vm.Online.RemotePlaylistId = "missing";
        await vm.Online.ImportRemoteIdCommand.ExecuteAsync(null);
        Assert.True(vm.Online.HasError);
        Assert.Single((await fixture.LoadLibraryAsync()).Playlists);
        await vm.Online.LogoutCommand.ExecuteAsync(null);
        Assert.False(fixture.Provider.State.IsAuthenticated);
        Assert.Empty(vm.Online.RemotePlaylists);
        Assert.DoesNotContain("a-secret-cookie", await File.ReadAllTextAsync(fixture.Paths.AppConfigurationFile));
    }

    [AvaloniaFact]
    public async Task PlaylistEditsReorderingRemovalAndDuplicateTrackEditingUseItemIdentities()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.Library.CreatePlaylistAsync("Original", "Original description");
        var playlistId = vm.SelectedPlaylist!.Id;
        await vm.Library.AddTrackAsync(playlistId, fixture.Provider.First);
        await vm.Library.AddTrackAsync(playlistId, fixture.Provider.Second);
        await vm.Library.AddTrackAsync(playlistId, fixture.Provider.First);
        var firstItemId = vm.Library.Tracks[0].PlaylistItemId;
        var duplicateItemId = vm.Library.Tracks[2].PlaylistItemId;
        vm.Library.Filter = "One";
        Assert.Equal(new[] { 1, 3 }, vm.Library.Tracks.Select(t => t.Number));
        vm.Library.SelectedTrack = vm.Library.Tracks[1];
        await vm.Library.MoveTrackUpCommand.ExecuteAsync(null);
        Assert.Equal(duplicateItemId, vm.Library.SelectedTrack!.PlaylistItemId);
        Assert.Equal(2, vm.Library.SelectedTrack.Number);
        await vm.Library.MoveTrackAsync(playlistId, duplicateItemId!.Value, 3);
        Assert.Equal(3, vm.Library.SelectedTrack.Number);
        await vm.Library.MoveTrackAsync(playlistId, duplicateItemId.Value, 2);
        vm.Library.InspectTrackCommand.Execute(null);
        var editor = Assert.IsType<TrackEditorViewModel>(vm.EditorPage.Editor);
        editor.Title = "Edited duplicate"; editor.Artists = "A; B"; editor.Album = "New album";
        editor.DurationSeconds = "180.25";
        editor.ExternalIds.Add(new() { Namespace = "isrc", Value = "TEST00000001" });
        await vm.EditorPage.SaveEditorCommand.ExecuteAsync(null);
        Assert.Null(vm.EditorPage.Error);
        var playlist = (await fixture.LoadLibraryAsync()).Playlists.Single();
        Assert.Equal("One", playlist.Items.Single(i => i.Id == firstItemId).Track.Title);
        var changed = playlist.Items.Single(i => i.Id == duplicateItemId).Track;
        Assert.Equal("Edited duplicate", changed.Title);
        Assert.Equal(new[] { "A", "B" }, changed.Artists.Select(a => a.Name));
        Assert.Equal(TimeSpan.FromSeconds(180.25), changed.Duration);
        Assert.Equal(2, changed.ExternalIds.Count);
        Assert.Equal("One", vm.Library.Filter);
        vm.Library.Filter = "";
        vm.Library.SelectedTrack = vm.Library.Tracks.Single(t => t.PlaylistItemId == duplicateItemId);
        await vm.Navigation.OpenEditorAsync(vm.Library.SelectedTrack);
        vm.EditorPage.Editor!.ExternalIds[0].Value = "changed-native-id";
        vm.EditorPage.Editor.ExternalIds.RemoveAt(1);
        await vm.EditorPage.SaveEditorAsync();
        Assert.Single((await fixture.LoadLibraryAsync()).Playlists.Single().Items.Single(i => i.Id == duplicateItemId).Track.ExternalIds);
        vm.Library.SelectedTrack = vm.Library.Tracks.Single(t => t.PlaylistItemId == duplicateItemId);
        await vm.Library.RemoveTrackCommand.ExecuteAsync(null);
        Assert.Equal(2, (await fixture.LoadLibraryAsync()).Playlists.Single().Items.Count);
        await vm.Library.UpdatePlaylistAsync(playlistId, "Renamed", null);
        Assert.Equal("Renamed", vm.Navigation.Current.PageTitle);
        Assert.Null((await fixture.LoadLibraryAsync()).Playlists.Single().Description);
        fixture.Runtime.Get<LibraryService>().Current.Mirrors.Add(new() { PlaylistId = playlistId, ProviderId = "test-service", RemotePlaylistId = "favorites" });
        await vm.Library.DeletePlaylistAsync(playlistId);
        var empty = await fixture.LoadLibraryAsync();
        Assert.Empty(empty.Playlists); Assert.Empty(empty.Mirrors);
    }

    [AvaloniaFact]
    public async Task NewTrackValidationAndFailedSaveKeepTheEditorAndRetryDoesNotDuplicateTheItem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.Library.CreatePlaylistAsync("Manual");
        var id = vm.SelectedPlaylist!.Id;
        vm.Library.NewTrackCommand.Execute(null);
        vm.EditorPage.Editor!.Title = "Handmade"; vm.EditorPage.Editor.DurationSeconds = "NaN";
        await vm.EditorPage.SaveEditorAsync();
        Assert.True(vm.EditorPage.HasError); Assert.NotNull(vm.EditorPage.Editor);
        Assert.Empty((await fixture.LoadLibraryAsync()).Playlists.Single().Items);
        vm.EditorPage.Editor.DurationSeconds = "123";
        vm.EditorPage.Editor.ExternalIds.Add(new() { Namespace = "isrc", Value = "TEST00000002" });
        vm.EditorPage.Editor.ExternalIds.Add(new() { Namespace = "ISRC", Value = "TEST00000003", Kind = "-" });
        await vm.EditorPage.SaveEditorAsync();
        Assert.True(vm.EditorPage.HasError);
        Assert.Empty((await fixture.LoadLibraryAsync()).Playlists.Single().Items);
        vm.EditorPage.Editor.ExternalIds.RemoveAt(1);
        var blockedPath = Path.Combine(fixture.Paths.MusicLibraryDirectory, "playlists", $"{id:N}.json.tmp");
        Directory.CreateDirectory(blockedPath);
        try
        {
            await vm.EditorPage.SaveEditorAsync();
            Assert.True(vm.EditorPage.HasError); Assert.NotNull(vm.EditorPage.Editor); Assert.False(vm.EditorPage.IsBusy);
            Assert.Equal(DesktopRoutes.Editor, vm.Navigation.Current.Route);
            Assert.Equal("Handmade", vm.EditorPage.Editor.Title);
            Assert.NotNull(vm.EditorPage.Editor.ItemId);
        }
        finally { Directory.Delete(blockedPath); }
        await vm.EditorPage.SaveEditorAsync();
        Assert.Null(vm.EditorPage.Error); Assert.Equal(DesktopRoutes.Library, vm.Navigation.Current.Route);
        Assert.Equal("Handmade", Assert.Single((await fixture.LoadLibraryAsync()).Playlists.Single().Items).Track.Title);
    }

    [AvaloniaFact]
    public async Task EnrichmentIsPreviewedThenAppliedAndBatchMatchingUpdatesEveryDuplicate()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.Library.CreatePlaylistAsync("Matching");
        var id = vm.SelectedPlaylist!.Id;
        await vm.Library.AddTrackAsync(id, fixture.Provider.First);
        await vm.Library.AddTrackAsync(id, fixture.Provider.First);
        vm.Library.SelectedTrack = vm.Library.Tracks[0];
        vm.Library.InspectTrackCommand.Execute(null);
        vm.EditorPage.MetadataTargets = "test-net";
        await vm.EditorPage.MetadataCommand.ExecuteAsync(null);
        Assert.Single(vm.EditorPage.MetadataMatches);
        Assert.Equal(new[] { "test-net" }, fixture.Provider.LastMetadataTargets);
        Assert.Contains("Fixture metadata warning", vm.EditorPage.InspectionMessages);
        await vm.EditorPage.EnrichCommand.ExecuteAsync(null);
        Assert.True(vm.EditorPage.HasEnrichedPreview);
        Assert.Equal("One", vm.EditorPage.Editor!.Title);
        Assert.All((await fixture.LoadLibraryAsync()).Playlists.Single().Items, i => Assert.Equal("One", i.Track.Title));
        vm.EditorPage.ApplyEnrichmentCommand.Execute(null);
        Assert.Equal("Matched One", vm.EditorPage.Editor.Title);
        Assert.Equal(2, vm.EditorPage.Editor.ExternalIds.Count(row => row.Namespace == "test-net"));
        await vm.EditorPage.SaveEditorAsync();
        Assert.Null(vm.EditorPage.Error);
        Assert.Equal("Matched One", (await fixture.LoadLibraryAsync()).Playlists.Single().Items[0].Track.Title);
        Assert.Equal("One", (await fixture.LoadLibraryAsync()).Playlists.Single().Items[1].Track.Title);
        vm.Library.SelectedTrack = vm.Library.Tracks[0];
        await vm.Navigation.OpenEditorAsync(vm.Library.SelectedTrack);
        await vm.EditorPage.SaveEditorAsync();
        Assert.Null(vm.EditorPage.Error);
        Assert.Equal(2, (await fixture.LoadLibraryAsync()).Playlists.Single().Items[0].Track.ExternalIds.Count(row => row.Namespace == "test-net"));
        await vm.Library.MatchPlaylistAsync(id);
        Assert.All((await fixture.LoadLibraryAsync()).Playlists.Single().Items, i => Assert.Equal("Matched album", i.Track.Album!.Title));
    }

    [AvaloniaFact]
    public async Task LyricsSourcesPlaybackAndWordHighlightsShareTheConsoleTimeline()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.Navigation.NavigateAsync(DesktopRoutes.Search);
        vm.Search.SearchQuery = "one";
        await vm.Search.SearchAsync();
        vm.Search.SelectedTrack = vm.Search.Tracks.Single();
        vm.Search.InspectTrackCommand.Execute(null);
        await vm.EditorPage.LyricsDetailsCommand.ExecuteAsync(null);
        Assert.Contains("hello world", vm.EditorPage.LyricsDetails); Assert.Contains("Romanization", vm.EditorPage.LyricsDetails);
        await vm.EditorPage.SourcesCommand.ExecuteAsync(null);
        Assert.Single(vm.EditorPage.DiscoveredSources);
        await vm.EditorPage.PlaySourceCommand.ExecuteAsync(null);
        Assert.Null(vm.Playback.Error);
        Assert.Equal(fixture.Provider.Id, fixture.Runtime.Get<PlaybackSessionService>().Queue.Request.RequiredSourceProviderId);
        await WaitUntilAsync(() => vm.Lyrics.CurrentLyricSegments.Count == 2);
        Assert.True(vm.Lyrics.CurrentLyricSegments[0].Reached); Assert.False(vm.Lyrics.CurrentLyricSegments[1].Reached);
        await vm.Playback.SeekAsync(1.6);
        Assert.All(vm.Lyrics.CurrentLyricSegments, s => Assert.True(s.Reached));
        Assert.All(vm.Lyrics.RomanizationSegments, s => Assert.True(s.Reached));
        Assert.Equal("世界", vm.Lyrics.Translation);
        await vm.Playback.TogglePlaybackCommand.ExecuteAsync(null);
        Assert.False(vm.Playback.IsPlaying);
        Assert.All(vm.Lyrics.CurrentLyricSegments, s => Assert.True(s.Reached));
        fixture.Text.Apply(LanguagePreference.Chinese);
        Assert.Contains("提供方", vm.EditorPage.LyricsDetails);
        await vm.Playback.ModeCommand.ExecuteAsync("Shuffle");
        Assert.Equal(PlaybackMode.Shuffle, fixture.Runtime.Get<PlaybackSessionService>().Queue.Mode);
        await vm.Playback.StopCommand.ExecuteAsync(null);
        Assert.Empty(vm.Lyrics.CurrentLyricSegments); Assert.Empty(vm.Lyrics.RomanizationSegments); Assert.Equal("", vm.Lyrics.Translation);
    }

    [AvaloniaFact]
    public async Task NowPlayingLyricsLayoutAndDisplaySettingsWorkInBothPalettesAndWindowSizes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        var main = new[] { "隣にいてくれたのに", "\"I'm me\" なんて言ってさ", "一人よがり最悪だ", "自分らしくいたいだけ", "願いごとはひとつだけ", "君とまた歩きたい" };
        var romanization = new[] { "tonari ni i te kure ta noni", "nante i xtu te sa", "hitori yogari saiaku da", "jibun rashiku itai dake", "negaigoto wa hitotsu dake", "kimi to mata arukitai" };
        var translated = new[] { "你总会陪在我身边", "但我总是只为自己着想", "自以为是的我 简直是太差劲了", "只是想活出自己的样子", "我的愿望只有一个", "想与你再次并肩前行" };
        fixture.Provider.LyricsOverride = new("", IsSynchronized: true,
            Lines: main.Select((line, i) => new LyricsLine(line, i * 1000, (i + 1) * 1000,
                Words: line.Select((c, j) => new LyricsWord(c.ToString(), i * 1000 + j * 1000 / line.Length, i * 1000 + (j + 1) * 1000 / line.Length)).ToArray())).ToArray(),
            RomanizationLines: romanization.Select((line, i) => new LyricsRomanizationLine(line.Split(' ').Select((word, j) =>
                new LyricsWord(word + " ", i * 1000 + j * 1000 / line.Split(' ').Length, i * 1000 + (j + 1) * 1000 / line.Split(' ').Length)).ToArray())).ToArray(),
            TranslationLines: translated.Select((line, i) => new LyricsLine(line, i * 1000, (i + 1) * 1000)).ToArray());
        await vm.Navigation.NavigateAsync(DesktopRoutes.Search);
        vm.Search.SearchQuery = "one"; await vm.Search.SearchAsync();
        vm.Search.SelectedTrack = vm.Search.Tracks.Single();
        await vm.Search.PlaySelectedCommand.ExecuteAsync(null);
        await vm.Navigation.NavigateAsync(DesktopRoutes.Lyrics);
        await WaitUntilAsync(() => vm.Lyrics.Document is not null);
        await vm.Playback.SeekAsync(1.6);
        await vm.Playback.TogglePlaybackCommand.ExecuteAsync(null);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        try
        {
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                foreach (var size in new[] { new Size(1180, 790), new Size(880, 620) })
                {
                    await fixture.Appearance.SetAsync(fixture.Appearance.Current with { Theme = theme, Language = LanguagePreference.Chinese });
                    window.Width = size.Width; window.Height = size.Height;
                    Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    var view = Assert.Single(window.GetVisualDescendants().OfType<LyricsView>());
                    var presenter = Assert.Single(view.GetVisualDescendants().OfType<LyricsPresenter>());
                    var cover = Assert.Single(view.GetVisualDescendants().OfType<Artwork>());
                    Assert.Equal(1, presenter.CurrentIndex);
                    Assert.Equal(vm.Lyrics.LyricFontSize, presenter.FontSize);
                    Assert.InRange(presenter.Bounds.Height, 120, view.Bounds.Height);
                    var coverPoint = cover.TranslatePoint(default, view)!.Value;
                    var lyricPoint = presenter.TranslatePoint(default, view)!.Value;
                    if (size.Width == 1180) Assert.True(lyricPoint.X > coverPoint.X + cover.Bounds.Width);
                    else Assert.True(lyricPoint.Y >= coverPoint.Y + cover.Bounds.Height);
                    using var rendered = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
                    rendered.Render(window);
                    if (Environment.GetEnvironmentVariable("LEMONNEO_PREVIEW_DIRECTORY") is { Length: > 0 } directory)
                    {
                        Directory.CreateDirectory(directory);
                        rendered.Save(Path.Combine(directory, $"Lyrics-{theme}-{size.Width}.png"), PngBitmapEncoderOptions.Default);
                    }
                }
            var timeline = Assert.Single(window.GetVisualDescendants().OfType<LyricsPresenter>());
            var currentMain = Assert.Single(timeline.GetVisualDescendants().OfType<LyricText>(), t => t.Text == main[1]);
            var currentRomanization = Assert.Single(timeline.GetVisualDescendants().OfType<LyricText>(), t => t.Text?.Trim() == romanization[1]);
            var currentTranslation = Assert.Single(timeline.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == translated[1]);
            foreach (var target in new TextBlock[] { currentMain, currentRomanization, currentTranslation })
            {
                await vm.Playback.SeekAsync(1.6); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                await WaitUntilAsync(() => vm.Playback.PositionSeconds == 1 && !vm.Lyrics.SeekLyricCommand.IsRunning);
                Assert.False(vm.Playback.IsPlaying);
                Assert.True(timeline.IsFollowing);
                Assert.True(Assert.IsType<StackPanel>(target.Parent).Focusable);
            }
            var settings = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LyricsSettingsButton");
            var flyout = Assert.IsType<Flyout>(settings.Flyout);
            flyout.ShowAt(settings); Dispatcher.UIThread.RunJobs();
            var panel = Assert.IsType<StackPanel>(flyout.Content);
            var bold = Assert.Single(panel.GetVisualDescendants().OfType<ToggleSwitch>(), toggle => toggle.Name == "BoldLyricsToggle");
            Assert.False(bold.IsChecked);
            bold.IsChecked = true;
            await WaitUntilAsync(() => fixture.Appearance.Current.BoldLyrics);
            Assert.True(vm.Lyrics.BoldLyrics); Assert.True(timeline.BoldLyrics);
            Assert.Equal(FontWeight.Bold, currentMain.FontWeight);
            Assert.Equal(FontWeight.Bold, currentRomanization.FontWeight);
            Assert.Equal(FontWeight.Bold, currentTranslation.FontWeight);
            var left = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AlignLeftButton");
            var center = panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AlignCenterButton");
            foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await fixture.Appearance.SetAsync(fixture.Appearance.Current with { Theme = theme });
                Assert.NotNull(center.Command);
                center.Command.Execute(center.CommandParameter);
                await WaitUntilAsync(() => fixture.Appearance.Current.LyricAlignment == LyricAlignment.Center);
                Dispatcher.UIThread.RunJobs();
                Assert.True(center.TryFindResource("Brush.Accent", center.ActualThemeVariant, out var accent));
                Assert.Equal(((ISolidColorBrush)accent!).Color, ((ISolidColorBrush)center.Background!).Color);
                Assert.Equal(((ISolidColorBrush)center.Background!).Color, ((ISolidColorBrush)Assert.Single(center.GetVisualDescendants().OfType<Border>()).Background!).Color);
                Assert.NotEqual(((ISolidColorBrush)center.Background!).Color, ((ISolidColorBrush)left.Background!).Color);
                Assert.NotNull(left.Command);
                left.Command.Execute(left.CommandParameter);
                await WaitUntilAsync(() => fixture.Appearance.Current.LyricAlignment == LyricAlignment.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(((ISolidColorBrush)accent!).Color, ((ISolidColorBrush)left.Background!).Color);
                Assert.NotEqual(((ISolidColorBrush)left.Background!).Color, ((ISolidColorBrush)center.Background!).Color);
                if (Environment.GetEnvironmentVariable("LEMONNEO_PREVIEW_DIRECTORY") is { Length: > 0 } directory)
                {
                    var popup = TopLevel.GetTopLevel(panel)!;
                    using var rendered = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(popup.Bounds.Width), (int)Math.Ceiling(popup.Bounds.Height)));
                    rendered.Render(popup);
                    rendered.Save(Path.Combine(directory, $"Lyrics-Settings-{theme}.png"), PngBitmapEncoderOptions.Default);
                }
            }
            var toggles = panel.GetVisualDescendants().OfType<ToggleSwitch>().ToArray();
            Assert.Equal(3, toggles.Length);
            foreach (var toggle in toggles) toggle.IsChecked = false;
            panel.GetVisualDescendants().OfType<Slider>().Single().Value = 32;
            Assert.False(vm.Lyrics.ShowRomanization); Assert.False(vm.Lyrics.ShowTranslation);
            Assert.False(vm.Lyrics.BoldLyrics); Assert.False(timeline.BoldLyrics);
            Assert.Equal(FontWeight.Normal, currentMain.FontWeight);
            Assert.Equal(FontWeight.Normal, currentRomanization.FontWeight);
            Assert.Equal(FontWeight.Normal, currentTranslation.FontWeight);
            Assert.Equal(32, vm.Lyrics.LyricFontSize);
            vm.Lyrics.AlignLeftCommand.Execute(null);
            await WaitUntilAsync(() => fixture.Appearance.Current.LyricFontSize == 32 && !fixture.Appearance.Current.ShowRomanization
                && !fixture.Appearance.Current.ShowTranslation && !fixture.Appearance.Current.BoldLyrics
                && fixture.Appearance.Current.LyricAlignment == LyricAlignment.Left);
            Assert.Equal(TextAlignment.Left, Assert.Single(window.GetVisualDescendants().OfType<LyricsPresenter>()).TextAlignment);
            flyout.Hide();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelledAndFailedSearchesRestoreInteractionAndCanBeRetried()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.Navigation.NavigateAsync(DesktopRoutes.Search);
        vm.Search.SearchQuery = "one";
        await vm.Search.SearchAsync();
        vm.Search.SelectedTrack = vm.Search.Tracks[0];
        await vm.Search.PlayAsync(vm.Search.SelectedTrack);
        vm.Search.SearchQuery = "wait";
        var search = vm.Search.SearchAsync();
        await fixture.Provider.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.Search.IsBusy); Assert.False(vm.Search.CanInteract);
        Assert.True(vm.Playback.CanTogglePlayback); Assert.True(vm.Playback.CanSeek);
        await vm.Playback.TogglePlaybackCommand.ExecuteAsync(null);
        Assert.False(vm.Playback.IsPlaying); Assert.True(vm.Search.IsBusy);
        await vm.Playback.SeekAsync(2);
        Assert.Equal(2, vm.Playback.PositionSeconds); Assert.True(vm.Search.IsBusy);
        vm.Search.CancelOperationCommand.Execute(null);
        await search.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(vm.Search.IsBusy); Assert.True(vm.Search.CanInteract); Assert.False(vm.Search.HasError);
        Assert.Equal(fixture.Text["Cancelled"], vm.Search.Status);
        vm.Search.SearchQuery = "one"; fixture.Provider.FailSearch = true;
        await vm.Search.SearchAsync();
        Assert.True(vm.Search.HasError); Assert.True(vm.Search.CanInteract);
        fixture.Provider.FailSearch = false;
        await vm.Search.SearchAsync();
        Assert.Null(vm.Search.Error); Assert.Single(vm.Search.Tracks);
    }

    [AvaloniaFact]
    public async Task SyncAndStartupEditorShareConsoleStorageAndKeepTheToolsPageSelected()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.Library.CreatePlaylistAsync("Local");
        await vm.Navigation.NavigateAsync(DesktopRoutes.Tools);
        await vm.Tools.ActivateAsync();
        await vm.Tools.SyncCommand.ExecuteAsync("status");
        Assert.Contains("Local changes", vm.Tools.SyncDetails);
        fixture.Text.Apply(LanguagePreference.Chinese);
        Assert.Contains("本地有更改", vm.Tools.SyncDetails);
        await vm.Tools.SyncCommand.ExecuteAsync("pull");
        Assert.Equal(DesktopRoutes.Tools, vm.Navigation.Current.Route);
        Assert.Equal(2, (await fixture.LoadLibraryAsync()).Playlists.Count);
        await vm.Tools.SyncCommand.ExecuteAsync("push");
        Assert.Null(vm.Tools.Error);
        vm.Tools.StartupScript = "# Custom\nversion\nqueue\n";
        await vm.Tools.SaveStartupCommand.ExecuteAsync(null);
        Assert.Equal(vm.Tools.StartupScript, await File.ReadAllTextAsync(vm.Tools.StartupPath));
        var shared = fixture.Runtime.Get<StartupCommands>();
        Assert.Equal("queue", (await shared.LoadAsync())[2]);
        var import = Path.Combine(fixture.Paths.DataDirectory, "startup source.txt");
        await File.WriteAllTextAsync(import, "library\nplugins\n");
        await vm.Tools.ImportStartupScriptAsync(import);
        Assert.Equal("library\nplugins\n", await File.ReadAllTextAsync(vm.Tools.StartupPath));
        await vm.Tools.ResetStartupCommand.ExecuteAsync(null);
        Assert.Equal(StartupCommands.DefaultScript, await File.ReadAllTextAsync(vm.Tools.StartupPath));
        var parsed = DesktopOptions.Parse(["--startup", import], fixture.Paths);
        Assert.Equal(Path.GetFullPath(import), parsed.StartupFile);
        Assert.Contains("Test service", vm.Tools.Diagnostics);
        Assert.Contains(fixture.Paths.DataDirectory, vm.Tools.PathDetails);
    }

    [AvaloniaFact]
    public async Task NewViewsTabsAndFlyoutsRenderAndUpdateInBothLanguagesAndPalettes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var vm = fixture.ViewModel;
        await vm.Library.CreatePlaylistAsync("Example");
        await vm.Library.AddTrackAsync(vm.SelectedPlaylist!.Id, fixture.Provider.First);
        vm.Library.SelectedTrack = vm.Library.Tracks[0];
        await vm.Navigation.OpenEditorAsync(vm.Library.SelectedTrack);
        await vm.EditorPage.InspectMetadataAsync(true);
        await vm.EditorPage.InspectLyricsAsync();
        await vm.EditorPage.InspectSourcesAsync();
        var window = new MainWindow { DataContext = vm, Width = 880, Height = 620 };
        window.Show();
        try
        {
            foreach (var language in new[] { LanguagePreference.English, LanguagePreference.Chinese })
                foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Dark })
                {
                    await fixture.Appearance.SetAsync(new(theme, language, true));
                    foreach (var page in new[] { DesktopRoutes.Library, DesktopRoutes.Search, DesktopRoutes.Online, DesktopRoutes.Editor, DesktopRoutes.Tools })
                    {
                        await vm.Navigation.NavigateAsync(page, page == DesktopRoutes.Library ? vm.Playlists.First().Id : null);
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
            await vm.EditorPage.CloseEditorCommand.ExecuteAsync(null);
            vm.SelectedPlaylist = vm.Playlists.First();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var action = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Flyout is MenuFlyout);
            var flyout = Assert.IsType<MenuFlyout>(action.Flyout);
            flyout.ShowAt(action);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(flyout.Items.OfType<MenuItem>(), item => item.Command == vm.Library.NewTrackCommand && item.IsEnabled);
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
