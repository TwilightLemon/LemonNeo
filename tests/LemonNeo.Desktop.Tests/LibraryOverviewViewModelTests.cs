using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using LemonNeo.Desktop.Services;
using LemonNeo.Desktop.ViewModels.Tools;
using Xunit;

namespace LemonNeo.Desktop.Tests;

public sealed class LibraryOverviewViewModelTests
{
    [Fact]
    public void StatisticsDeduplicateTracksAndCountKnownDurationOnce()
    {
        using var text = new LocalizationService();
        var library = new MusicLibrary
        {
            Playlists =
            [
                Playlist("First", Song("a"), Song("b", 60)),
                Playlist("Second", Song("a", 120), Song("c")),
                Playlist("Empty")
            ]
        };
        var model = new LibraryOverviewViewModel(() => library, new Registry(), text, (_, _) => Task.CompletedTask);

        model.Refresh();

        Assert.Equal(["3", "3", "4", "3:00"], model.Statistics.Select(statistic => statistic.Value));
        Assert.Empty(model.SyncedPlaylists);
        Assert.False(model.HasSyncedPlaylists);
        library.Playlists.Clear();
        model.Refresh();
        Assert.Equal(["0", "0", "0", "—"], model.Statistics.Select(statistic => statistic.Value));
    }

    [Fact]
    public async Task ListsBoundPlaylistsIncludingPausedOnesAndPersistsIndividualSwitchChanges()
    {
        using var text = new LocalizationService();
        var first = Playlist("A source", Song("a"));
        var folder = Playlist("B folder");
        var ordinary = Playlist("Local copy", Song("b"));
        var library = new MusicLibrary
        {
            Playlists = [folder, ordinary, first],
            Mirrors =
            [
                new() { PlaylistId = first.Id, ProviderId = "test", RemotePlaylistId = "remote", IsPaused = true },
                new() { PlaylistId = folder.Id, SourceKind = PlaylistSourceKind.Folder, DirectoryPath = "C:/music" }
            ]
        };
        var calls = new List<(Guid Id, bool Paused)>();
        var model = new LibraryOverviewViewModel(() => library, new Registry(), text, (id, paused) =>
        {
            calls.Add((id, paused));
            var index = library.Mirrors.FindIndex(mirror => mirror.PlaylistId == id);
            library.Mirrors[index] = library.Mirrors[index] with { IsPaused = paused };
            return Task.CompletedTask;
        });
        model.Refresh();
        Assert.Equal([first.Id, folder.Id], model.SyncedPlaylists.Select(row => row.PlaylistId));
        var row = model.SyncedPlaylists[0];
        Assert.Contains("Test music", row.Summary);
        Assert.False(row.IsAutoSyncEnabled);
        Assert.True(model.SyncedPlaylists[1].IsAutoSyncEnabled);

        await row.SetAutoSyncEnabledAsync(true);

        Assert.Equal([(first.Id, false)], calls);
        Assert.True(row.IsAutoSyncEnabled);
        Assert.Same(row, model.SyncedPlaylists[0]);
        await row.SetAutoSyncEnabledAsync(false);
        Assert.False(row.IsAutoSyncEnabled);
        Assert.True(row.CanChangeAutoSync);
        Assert.True(library.Mirrors[0].IsPaused);
        Assert.False(library.Mirrors[1].IsPaused);
    }

    [Fact]
    public async Task GlobalPauseDoesNotReplaceIndividualPreferences()
    {
        using var text = new LocalizationService();
        var playlist = Playlist("Source");
        var library = new MusicLibrary
        {
            Playlists = [playlist], IsPlaylistSyncPaused = true,
            Mirrors = [new() { PlaylistId = playlist.Id, ProviderId = "test", IsPaused = false }]
        };
        var model = new LibraryOverviewViewModel(() => library, new Registry(), text, (_, paused) =>
        {
            library.Mirrors[0] = library.Mirrors[0] with { IsPaused = paused };
            return Task.CompletedTask;
        });
        model.Refresh();
        var row = Assert.Single(model.SyncedPlaylists);
        Assert.True(row.IsAutoSyncEnabled);
        Assert.Equal(text[LanguageManager.Keys.PlaylistSyncGlobalPaused], row.Status);
        await row.SetAutoSyncEnabledAsync(false);
        library.IsPlaylistSyncPaused = false;
        model.Refresh();
        Assert.False(row.IsAutoSyncEnabled);
        Assert.Equal(text[LanguageManager.Keys.PlaylistSyncPaused], row.Status);
    }

    [Fact]
    public async Task SwitchIsDisabledDuringSaveAndIsNotReentered()
    {
        using var text = new LocalizationService();
        var playlist = Playlist("Source");
        var mirror = new PlaylistMirror { PlaylistId = playlist.Id };
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        PlaylistSyncRowViewModel? row = null;
        row = new(playlist.Id, text, async paused =>
        {
            calls++;
            await response.Task;
            mirror = mirror with { IsPaused = paused };
        }, () => row!.Update(playlist, mirror, "Test music", false));
        row.Update(playlist, mirror, "Test music", false);
        var pending = row.SetAutoSyncEnabledAsync(false);
        Assert.False(row.CanChangeAutoSync);
        await row.SetAutoSyncEnabledAsync(false);
        Assert.Equal(1, calls);
        response.SetResult();
        await pending;
        Assert.True(row.CanChangeAutoSync);
        Assert.False(row.IsAutoSyncEnabled);
    }

    [Fact]
    public async Task ReplacingLibraryReplacesRowsAndOldRowsCannotChangeTheNewLibrary()
    {
        using var text = new LocalizationService();
        var playlist = Playlist("Original");
        var mirror = new PlaylistMirror { PlaylistId = playlist.Id, ProviderId = "test" };
        var library = new MusicLibrary { Playlists = [playlist], Mirrors = [mirror] };
        var calls = 0;
        var model = new LibraryOverviewViewModel(() => library, new Registry(), text, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });
        model.Refresh();
        var oldRow = Assert.Single(model.SyncedPlaylists);
        library = new() { Playlists = [new() { Id = playlist.Id, Name = "Replacement" }], Mirrors = [mirror] };
        model.Refresh();
        var newRow = Assert.Single(model.SyncedPlaylists);
        Assert.NotSame(oldRow, newRow);
        Assert.Equal("Replacement", newRow.Name);
        await oldRow.SetAutoSyncEnabledAsync(false);
        Assert.Equal(0, calls);
        await newRow.SetAutoSyncEnabledAsync(false);
        Assert.Equal(1, calls);
        library.Mirrors.Clear();
        model.Refresh();
        Assert.Empty(model.SyncedPlaylists);
    }

    private static Track Song(string id, double? seconds = null) => new()
    {
        Id = new("test", id), Title = id, Duration = seconds is { } duration ? TimeSpan.FromSeconds(duration) : null
    };
    private static Playlist Playlist(string name, params Track[] tracks) => new()
    {
        Name = name, Items = tracks.Select((track, index) =>
            new PlaylistItem { Track = track, SortKey = (index + 1).ToString() }).ToList()
    };
    private sealed class Registry : IProviderRegistry
    {
        public IReadOnlyCollection<IMusicProvider> Providers => [new Provider()];
        public IMusicProvider? Get(string providerId) => providerId == "test" ? Providers.Single() : null;
    }
    private sealed class Provider : IMusicProvider
    {
        public string Id => "test";
        public ProviderDescriptor Descriptor => new("Test music", "");
    }
}
