using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Desktop;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Resources.Themes;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Library;
using LemonNeo.Desktop.ViewModels.Playback;
using LemonNeo.Desktop.ViewModels.Shared;
using Xunit;

namespace LemonNeo.Tests;

public sealed class TrackListTests
{
    [AvaloniaFact]
    public async Task RefreshNavigationAndLanguageKeepRowsSelectionAndArtworkBindings()
    {
        await using var desktop = new TestDesktop();
        await desktop.Host.Settings.LoadAsync();
        var playlists = desktop.Host.Get<PlaylistService>();
        var playlist = playlists.Create("test");
        // Duplicate tracks are separate playlist entries and must stay separate rows.
        playlists.AddRange(playlist.Id, [Track("Rock", "same"), Track("Rock", "same"), Track("Jazz", "other")]);
        desktop.Context.RefreshLibrary();
        await using var view = new LibraryViewModel(desktop.Context, null!, desktop.Playback, DesktopRoutes.Library, playlist.Id);
        var rows = view.Tracks.ToArray();
        view.IsSelectionMode = true;
        rows[1].IsMarked = true;
        view.SelectedTrack = rows[1];
        var changes = new List<NotifyCollectionChangedAction>();
        view.Tracks.CollectionChanged += (_, e) => changes.Add(e.Action);
        var artworkChanges = 0;
        foreach (var row in rows)
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TrackRow.ArtworkUri)) artworkChanges++; };

        desktop.Context.RefreshLibrary();
        await view.ActivateAsync();
        desktop.Text.Apply(LanguagePreference.English);
        desktop.Text.Apply(LanguagePreference.Chinese);
        desktop.Context.Settings.Apply<LibraryPreferences>(value => value with { InsertAtStart = !value.InsertAtStart });
        Assert.Empty(changes);
        Assert.Equal(0, artworkChanges);
        for (var i = 0; i < rows.Length; i++) Assert.Same(rows[i], view.Tracks[i]);
        Assert.Same(rows[1], view.SelectedTrack);
        Assert.True(rows[1].IsMarked);

        playlists.UpdateTrack(playlist.Id, rows[1].PlaylistItemId!.Value, rows[1].Track with { Title = "Renamed" });
        desktop.Context.RefreshLibrary();
        Assert.Equal("Renamed", rows[1].Title);
        Assert.Empty(changes);
        Assert.Equal(0, artworkChanges);

        playlists.UpdateTrack(playlist.Id, rows[1].PlaylistItemId!.Value,
            rows[1].Track with { ArtworkUri = ImageTestContext.Uri("new") });
        desktop.Context.RefreshLibrary();
        Assert.Equal(1, artworkChanges);
        Assert.Empty(changes);

        playlists.Reorder(playlist.Id, [rows[1].PlaylistItemId!.Value], rows[0].PlaylistItemId!.Value, false);
        desktop.Context.RefreshLibrary();
        Assert.Equal([NotifyCollectionChangedAction.Move], changes);
        Assert.Same(rows[1], view.Tracks[0]);
        Assert.Same(rows[1], view.SelectedTrack);
        Assert.Equal(1, rows[1].Number);
        Assert.Equal(2, rows[0].Number);
        Assert.True(rows[1].IsMarked);
    }

    [AvaloniaFact]
    public async Task TypingIsDebouncedAndClearingRestoresTheSameRowsImmediately()
    {
        await using var desktop = new TestDesktop();
        var playlists = desktop.Host.Get<PlaylistService>();
        var playlist = playlists.Create("test");
        playlists.AddRange(playlist.Id, [Track("Rock One", "1"), Track("Rock Two", "2"), Track("Jazz", "3"), Track("Pop", "4")]);
        desktop.Context.RefreshLibrary();
        await using var view = new LibraryViewModel(desktop.Context, null!, desktop.Playback, DesktopRoutes.Library, playlist.Id);
        var rows = view.Tracks.ToArray();
        view.SelectedTrack = rows[0];
        var changes = new List<NotifyCollectionChangedAction>();
        view.Tracks.CollectionChanged += (_, e) => changes.Add(e.Action);
        view.Filter = "R";
        view.Filter = "Ro";
        view.Filter = "Rock";
        Assert.Empty(changes);
        Assert.Equal(4, view.Tracks.Count);
        await Task.Delay(350);
        Assert.Equal(2, view.Tracks.Count);
        Assert.Equal([NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Remove], changes);
        Assert.Same(rows[0], view.Tracks[0]);
        Assert.Same(rows[1], view.Tracks[1]);
        Assert.Same(rows[0], view.SelectedTrack);

        view.Filter = "";
        Assert.Equal(4, view.Tracks.Count);
        for (var i = 0; i < rows.Length; i++) Assert.Same(rows[i], view.Tracks[i]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);

    }

    [AvaloniaFact]
    public async Task DisposingPageCancelsPendingFilter()
    {
        await using var desktop = new TestDesktop();
        var playlists = desktop.Host.Get<PlaylistService>();
        var playlist = playlists.Create("test");
        playlists.AddRange(playlist.Id, [Track("Rock", "1"), Track("Jazz", "2")]);
        desktop.Context.RefreshLibrary();
        var changes = 0;
        await using (var view = new LibraryViewModel(desktop.Context, null!, desktop.Playback, DesktopRoutes.Library, playlist.Id))
        {
            view.Tracks.CollectionChanged += (_, _) => changes++;
            view.Filter = "Jazz";
        }
        await Task.Delay(350);
        Assert.Equal(0, changes);
    }

    [AvaloniaFact]
    public async Task QueueDrawerPreservesRowsAcrossQueueChangesAndFiltering()
    {
        await using var desktop = new TestDesktop();
        var queue = desktop.Host.Get<IPlaybackQueue>();
        queue.Replace([Track("Rock", "1"), Track("Jazz", "2")]);
        await using var view = new PlaybackQueueViewModel(desktop.Context, desktop.Playback);
        var first = view.Tracks[0];
        view.SelectedTrack = first;
        var changes = new List<NotifyCollectionChangedAction>();
        view.Tracks.CollectionChanged += (_, e) => changes.Add(e.Action);
        await view.ActivateAsync();
        desktop.Text.Apply(LanguagePreference.English);
        Assert.Empty(changes);
        queue.Enqueue(Track("Pop", "3"));
        await view.ActivateAsync();
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        Assert.Same(first, view.Tracks[0]);
        Assert.Same(first, view.SelectedTrack);
        queue.UpdateTrack(first.Id, first.Row.Track with { Title = "Rock Updated" });
        await view.ActivateAsync();
        Assert.Same(first, view.Tracks[0]);
        Assert.Equal("Rock Updated", first.Row.Title);
        view.Filter = "Rock";
        await Task.Delay(350);
        Assert.Same(first, Assert.Single(view.Tracks));
        view.Filter = "";
        Assert.Equal(3, view.Tracks.Count);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
    }

    private static Track Track(string title, string id) => new()
    {
        Id = new MusicItemId("test", id), Title = title, ArtworkUri = ImageTestContext.Uri()
    };

    internal sealed class TestDesktop : IAsyncDisposable
    {
        private readonly Paths _paths = new();
        private readonly AppearanceService _appearance;
        public DesktopHost Host { get; }
        public DesktopContext Context { get; }
        public LocalizationService Text { get; }
        public PlaybackViewModel Playback { get; }
        public AppearanceService Appearance => _appearance;

        public TestDesktop()
        {
            Host = new DesktopHost(_paths, _paths.MusicLibraryDirectory, _paths.CacheDirectory, new Storage());
            var settings = Host.Settings.ForPackage(PackageNames.Desktop);
            DesktopPreferences.Register(settings);
            Text = new LocalizationService(settings);
            var application = Avalonia.Application.Current!;
            if (!application.Resources.MergedDictionaries.OfType<ThemeResources>().Any())
                application.Resources.MergedDictionaries.Add(new ThemeResources());
            _appearance = new AppearanceService(application, settings);
            Context = new DesktopContext(Host, Text, _appearance) { IsInitialized = true };
            Playback = new PlaybackViewModel(Context, Host.Get<ArtworkService>());
        }

        public async ValueTask DisposeAsync()
        {
            await Playback.DisposeAsync();
            _appearance.Dispose();
            Text.Dispose();
            await Host.DisposeAsync();
            if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
        }
    }

    private sealed class Storage : IDesktopStorageService
    {
        public Task ChangeLibraryAsync(string directory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ChangeCacheAsync(string directory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Paths : IAppPathProvider
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LemonNeo-list-tests", Guid.NewGuid().ToString("N"));
        public string ConfigurationDirectory => Path.Combine(Root, "Settings");
        public string DataDirectory => Path.Combine(Root, "Data");
        public string CacheDirectory => Path.Combine(Root, "Cache");
        public string MusicLibraryDirectory => Path.Combine(Root, "Library");
        public string PluginDirectory => Path.Combine(Root, "Plugins");
        public string DataProtectionKeysDirectory => Path.Combine(Root, "Keys");
        public string GetPackageConfigurationDirectory(string packageId) => Path.Combine(ConfigurationDirectory, packageId);
        public string GetEntityConfigurationFile(string packageId, string entityName) => Path.Combine(GetPackageConfigurationDirectory(packageId), entityName + ".json");
        public string GetPluginCredentialDirectory(string pluginId) => Path.Combine(DataDirectory, "Credentials", pluginId);
    }
}
