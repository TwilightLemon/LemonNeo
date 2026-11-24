using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Application.Library;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using LemonNeo.Infrastructure.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LemonNeo.Application.Tests;

public sealed class PlaylistSyncServiceTests
{
    [Fact]
    public async Task UpdatesMembershipOrderAndMetadataWhilePreservingOccurrencesAndEnrichedIds()
    {
        using var fixture = new Fixture();
        var a = Song("a");
        var playlist = fixture.Import(Snapshot(a, Song("b"), a));
        var ids = playlist.Items.Select(item => item.Id).ToArray();
        playlist.DecodeProfile = new() { Speed = 1.1 };
        playlist.Items[0].DecodeProfile = new() { Speed = 1.25 };
        fixture.Playlists.EnrichExternalIds(playlist.Id, ids[0], [new("isrc", "enriched")]);
        fixture.Provider.Remote = Snapshot(a with { Title = "Updated" }, a, Song("c")) with { Name = "Renamed" };

        var result = await fixture.Sync.SynchronizeAsync(playlist.Id);

        Assert.True(result.Succeeded);
        Assert.True(result.Changed);
        Assert.Equal("Renamed", playlist.Name);
        Assert.Equal(["a", "a", "c"], playlist.Items.Select(item => item.Track.Id.RemoteId));
        Assert.Equal([ids[0], ids[2]], playlist.Items.Take(2).Select(item => item.Id));
        Assert.Equal(1.1, playlist.DecodeProfile?.Speed);
        Assert.Equal(1.25, playlist.Items[0].DecodeProfile?.Speed);
        Assert.Null(playlist.Items[1].DecodeProfile);
        Assert.Equal("Updated", playlist.Items[0].Track.Title);
        Assert.Contains(new ExternalMusicId("isrc", "enriched"), playlist.Items[0].Track.ExternalIds);
        Assert.Equal(3, playlist.Items.Select(item => item.Id).Distinct().Count());
        var same = await fixture.Sync.SynchronizeAsync(playlist.Id);
        Assert.True(same.Succeeded);
        Assert.False(same.Changed);

        var copy = fixture.Playlists.CreateEditableCopy(playlist.Id);
        Assert.Equal(playlist.DecodeProfile, copy.DecodeProfile);
        Assert.Equal(playlist.Items[0].DecodeProfile, copy.Items[0].DecodeProfile);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("missing")]
    [InlineData("exception")]
    [InlineData("wrong-source")]
    public async Task KeepsLastSuccessfulSnapshotWhenSourceCannotBeTrusted(string failure)
    {
        using var fixture = new Fixture();
        var playlist = fixture.Import(Snapshot(Song("a")));
        var before = fixture.Sync.GetMirror(playlist.Id)!;
        fixture.Provider.Remote = failure switch
        {
            "incomplete" => Snapshot() with { IsComplete = false },
            "missing" => null,
            "wrong-source" => Snapshot() with { Id = "different" },
            _ => Snapshot()
        };
        if (failure == "exception") fixture.Provider.Read = _ => throw new IOException("Offline");

        var result = await fixture.Sync.SynchronizeAsync(playlist.Id);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Equal("a", Assert.Single(playlist.Items).Track.Id.RemoteId);
        Assert.Equal(before.LastSynchronizedAt, fixture.Sync.GetMirror(playlist.Id)!.LastSynchronizedAt);
        Assert.Equal(before.ContentFingerprint, fixture.Sync.GetMirror(playlist.Id)!.ContentFingerprint);
    }

    [Fact]
    public async Task AcceptsConfirmedEmptyPlaylistAndClearsOldErrorsOnRecovery()
    {
        using var fixture = new Fixture();
        var playlist = fixture.Import(Snapshot(Song("a")));
        fixture.Provider.Remote = Snapshot() with { IsComplete = false };
        await fixture.Sync.SynchronizeAsync(playlist.Id);
        fixture.Provider.Remote = Snapshot();
        Assert.True((await fixture.Sync.SynchronizeAsync(playlist.Id)).Succeeded);
        Assert.Empty(playlist.Items);
        Assert.Null(fixture.Sync.GetMirror(playlist.Id)!.LastError);
    }

    [Fact]
    public async Task PausesAutomaticSyncButAllowsManualSyncAndPreservesIndividualPauseAcrossGlobalResume()
    {
        using var fixture = new Fixture();
        var first = fixture.Import(Snapshot(Song("a")));
        var second = fixture.Import(Snapshot(Song("a")) with { Id = "second" });
        fixture.Provider.Read = id => Task.FromResult<RemotePlaylist?>(Snapshot(Song("b")) with { Id = id });
        await fixture.Sync.SetPausedAsync(first.Id, true);
        await fixture.Sync.SetPausedAsync(null, true);
        Assert.All(await fixture.Sync.SynchronizeAllAsync(automatic: true), result => Assert.True(result.Skipped));
        Assert.Equal(0, fixture.Provider.Requests);
        Assert.All(await fixture.Sync.SynchronizeAllAsync(), result => Assert.True(result.Succeeded));
        Assert.True(fixture.Library.Current.IsPlaylistSyncPaused);
        await fixture.Sync.SetPausedAsync(null, false);
        Assert.True(fixture.Sync.GetMirror(first.Id)!.IsPaused);
        Assert.False(fixture.Sync.GetMirror(second.Id)!.IsPaused);
        var results = await fixture.Sync.SynchronizeAllAsync(automatic: true);
        Assert.True(results[0].Skipped);
        Assert.True(results[1].Succeeded);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("pause-all")]
    [InlineData("detach")]
    [InlineData("delete")]
    [InlineData("switch-library")]
    [InlineData("switch-account")]
    public async Task DiscardsInFlightReadsWhenTheirDestinationOrSourceChanges(string action)
    {
        using var fixture = new Fixture();
        var playlist = fixture.Import(Snapshot(Song("a")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<RemotePlaylist?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Read = _ => { started.SetResult(); return response.Task; };
        var pending = fixture.Sync.SynchronizeAsync(playlist.Id, automatic: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        switch (action)
        {
            case "pause": await fixture.Sync.SetPausedAsync(playlist.Id, true); break;
            case "pause-all": await fixture.Sync.SetPausedAsync(null, true); break;
            case "detach": await fixture.Sync.DetachAsync(playlist.Id); break;
            case "delete": fixture.Playlists.Delete(playlist.Id); break;
            case "switch-library": fixture.Library.Replace(new MusicLibrary()); break;
            case "switch-account": fixture.Provider.State = new(true, UserId: "other-account"); break;
        }
        response.SetResult(Snapshot(Song("b")));
        var result = await pending;
        Assert.False(result.Succeeded);
        Assert.Equal("a", Assert.Single(playlist.Items).Track.Id.RemoteId);
        if (action == "delete") Assert.Null(fixture.Sync.GetMirror(playlist.Id));
        if (action == "switch-account") Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RejectsChangedAccountsBeforeSendingAnyRequest()
    {
        using var fixture = new Fixture();
        var playlist = fixture.Import(Snapshot(Song("a")));
        fixture.Provider.State = new(true, UserId: "other-account");
        Assert.False((await fixture.Sync.SynchronizeAsync(playlist.Id)).Succeeded);
        Assert.Equal(0, fixture.Provider.Requests);
        Assert.Throws<InvalidOperationException>(() => fixture.Sync.ImportRemote(Snapshot(), "account"));
    }

    [Fact]
    public async Task PausingDuringManualSyncDoesNotCancelItOrLosePauseSetting()
    {
        using var fixture = new Fixture();
        var playlist = fixture.Import(Snapshot(Song("a")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<RemotePlaylist?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Read = _ => { started.SetResult(); return response.Task; };
        var pending = fixture.Sync.SynchronizeAsync(playlist.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Sync.SetPausedAsync(playlist.Id, true);
        response.SetResult(Snapshot(Song("b")));
        Assert.True((await pending).Succeeded);
        Assert.True(fixture.Sync.GetMirror(playlist.Id)!.IsPaused);
        Assert.Equal("b", Assert.Single(playlist.Items).Track.Id.RemoteId);
    }

    [Fact]
    public async Task QueuedSyncDoesNotTouchReplacementLibraryWithMatchingPlaylistIds()
    {
        using var fixture = new Fixture();
        var playlist = fixture.Import(Snapshot(Song("a")));
        using var lease = await fixture.Sync.AcquireLibraryChangeAsync();
        var pending = fixture.Sync.SynchronizeAsync(playlist.Id);
        fixture.Library.Replace(new MusicLibrary
        {
            Playlists = [new() { Id = playlist.Id, Name = "Other library" }],
            Mirrors = [fixture.Sync.GetMirror(playlist.Id)!]
        });
        lease.Dispose();
        Assert.True((await pending).Skipped);
        Assert.Equal(0, fixture.Provider.Requests);
    }

    [Fact]
    public async Task SerializesConcurrentManualAndAutomaticRequests()
    {
        using var fixture = new Fixture();
        var playlist = fixture.Import(Snapshot(Song("a")));
        var active = 0;
        var maximum = 0;
        fixture.Provider.Read = async _ =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            await Task.Delay(20);
            Interlocked.Decrement(ref active);
            return Snapshot(Song("b"));
        };
        var results = await Task.WhenAll(fixture.Sync.SynchronizeAsync(playlist.Id),
            fixture.Sync.SynchronizeAsync(playlist.Id, automatic: true));
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, maximum);
        Assert.Single(playlist.Items);
    }

    [Fact]
    public async Task ReadOnlyGuardCoversEditingWhileCopyAndDetachAllowLocalChanges()
    {
        using var fixture = new Fixture();
        var playlist = fixture.Import(Snapshot(Song("a")));
        var item = playlist.Items[0];
        Assert.Throws<InvalidOperationException>(() => fixture.Playlists.Add(playlist.Id, Song("b")));
        Assert.Throws<InvalidOperationException>(() => fixture.Playlists.AddRange(playlist.Id, [Song("b")]));
        Assert.Throws<InvalidOperationException>(() => fixture.Playlists.Remove(playlist.Id, item.Id));
        Assert.Throws<InvalidOperationException>(() => fixture.Playlists.Update(playlist.Id, "Name", null));
        Assert.Throws<InvalidOperationException>(() => fixture.Playlists.UpdateTrack(playlist.Id, item.Id, Song("b")));
        Assert.Throws<InvalidOperationException>(() => fixture.Playlists.Move(playlist.Id, item.Id, 1));
        Assert.Throws<InvalidOperationException>(() => fixture.Playlists.Reorder(playlist.Id, [item.Id], item.Id, false));
        var copy = fixture.Playlists.CreateEditableCopy(playlist.Id);
        fixture.Playlists.Add(copy.Id, Song("b"));
        Assert.Single(playlist.Items);
        Assert.Equal(2, copy.Items.Count);
        Assert.False(fixture.Playlists.IsReadOnly(copy.Id));
        await fixture.Sync.DetachAsync(playlist.Id);
        fixture.Playlists.Remove(playlist.Id, item.Id);
        Assert.Empty(playlist.Items);
    }

    [Fact]
    public async Task FailedPersistenceRestoresPreviousContentAndCanRetry()
    {
        using var fixture = new Fixture();
        var playlist = fixture.Import(Snapshot(Song("a")));
        await fixture.Library.SaveAsync();
        var before = fixture.Sync.GetMirror(playlist.Id)!;
        fixture.Provider.Remote = Snapshot(Song("b"));
        fixture.Repository.FailSave = true;
        Assert.False((await fixture.Sync.SynchronizeAsync(playlist.Id)).Succeeded);
        Assert.Equal("a", Assert.Single(playlist.Items).Track.Id.RemoteId);
        Assert.Equal(before.ContentFingerprint, fixture.Sync.GetMirror(playlist.Id)!.ContentFingerprint);
        fixture.Repository.FailSave = false;
        Assert.True((await fixture.Sync.SynchronizeAsync(playlist.Id)).Succeeded);
        Assert.Equal("b", Assert.Single(playlist.Items).Track.Id.RemoteId);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("synced")]
    [InlineData("paused")]
    public async Task MatchingAndExternalIdEditingAreAllowedInEverySyncMode(string mode)
    {
        using var fixture = new Fixture();
        var original = Song("a") with { ExternalIds = [new("native", "original", "song")] };
        var playlist = fixture.Import(Snapshot(original));
        if (mode == "local") await fixture.Sync.DetachAsync(playlist.Id);
        if (mode == "paused") await fixture.Sync.SetPausedAsync(playlist.Id, true);
        var item = Assert.Single(playlist.Items);

        fixture.Playlists.UpdateMetadata(playlist.Id, item.Id, original with { Title = "Matched title" });
        fixture.Playlists.UpdateExternalId(playlist.Id, item.Id, new("NATIVE", "corrected", "song"));
        fixture.Playlists.AddExternalId(playlist.Id, item.Id, new("isrc", "local-isrc"));
        Assert.True(fixture.Playlists.RemoveExternalId(playlist.Id, item.Id, "native", "song"));

        Assert.Equal("Matched title", item.Track.Title);
        Assert.Equal(original.Id, item.Track.Id);
        Assert.Equal([new ExternalMusicId("isrc", "local-isrc")], item.Track.ExternalIds);
        Assert.Throws<ArgumentException>(() => fixture.Playlists.UpdateMetadata(playlist.Id, item.Id, Song("different")));
        if (mode != "local")
            Assert.Throws<InvalidOperationException>(() => fixture.Playlists.UpdateTrack(playlist.Id, item.Id, Song("different")));
    }

    [Fact]
    public async Task PreservesMatchedMetadataAndEditedOrRemovedExternalIdsThroughReloadAndSourceUpdates()
    {
        using var fixture = new Fixture();
        var original = Song("a") with
        {
            ExternalIds = [new("native", "original", "song"), new("isrc", "source-isrc")]
        };
        var playlist = fixture.Import(Snapshot(original, Song("b")));
        var itemId = playlist.Items[0].Id;
        fixture.Playlists.UpdateMetadata(playlist.Id, itemId, original with
        {
            Title = "Matched title", Artists = [new() { Name = "Matched artist" }]
        });
        fixture.Playlists.UpdateExternalId(playlist.Id, itemId, new("native", "corrected", "song"));
        fixture.Playlists.RemoveExternalId(playlist.Id, itemId, "isrc", null);
        fixture.Playlists.AddExternalId(playlist.Id, itemId, new("spotify", "local-spotify"));

        var repository = new JsonLibraryRepository(() => fixture.Root);
        var persisted = new LibraryService(repository);
        persisted.Replace(fixture.Library.Current);
        await persisted.SaveAsync();
        fixture.Library.Replace(await new LibraryService(repository).LoadAsync());
        fixture.Provider.Remote = Snapshot(Song("b") with { Title = "B updated" }, original with
        {
            Title = "Source title changed",
            ExternalIds = [new("native", "source-changed", "song"), new("isrc", "source-isrc"), new("fresh", "new")]
        });

        Assert.True((await fixture.Sync.SynchronizeAsync(playlist.Id)).Succeeded);

        var updated = fixture.Playlists.GetRequired(playlist.Id);
        Assert.Equal("B updated", updated.Items[0].Track.Title);
        var matched = updated.Items[1];
        Assert.Equal(itemId, matched.Id);
        Assert.Equal(original.Id, matched.Track.Id);
        Assert.Equal("Matched title", matched.Track.Title);
        Assert.Equal("Matched artist", Assert.Single(matched.Track.Artists).Name);
        Assert.Contains(new ExternalMusicId("native", "corrected", "song"), matched.Track.ExternalIds);
        Assert.Contains(new ExternalMusicId("spotify", "local-spotify"), matched.Track.ExternalIds);
        Assert.Contains(new ExternalMusicId("fresh", "new"), matched.Track.ExternalIds);
        Assert.DoesNotContain(matched.Track.ExternalIds, id => id.Namespace == "isrc");
        fixture.Playlists.EnrichExternalIds(playlist.Id, matched.Id, [new("isrc", "rediscovered")]);
        Assert.DoesNotContain(matched.Track.ExternalIds, id => id.Namespace == "isrc");
    }

    [Fact]
    public async Task ExternalIdOnlyEditsDoNotFreezeSourceMetadataOrLoseChangesDuringSync()
    {
        using var fixture = new Fixture();
        var original = Song("a") with { ExternalIds = [new("native", "original")] };
        var playlist = fixture.Import(Snapshot(original));
        var itemId = playlist.Items[0].Id;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<RemotePlaylist?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Read = _ => { started.SetResult(); return response.Task; };
        var pending = fixture.Sync.SynchronizeAsync(playlist.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Playlists.UpdateExternalIds(playlist.Id, itemId, [new("native", "corrected")]);
        response.SetResult(Snapshot(original with { Title = "New source title" }));

        Assert.True((await pending).Succeeded);
        var item = Assert.Single(playlist.Items);
        Assert.Equal("New source title", item.Track.Title);
        Assert.False(item.HasMetadataOverride);
        Assert.Equal([new ExternalMusicId("native", "corrected")], item.Track.ExternalIds);
    }

    [Fact]
    public async Task PersistsSourcesPausesAndLastSnapshotAcrossReload()
    {
        using var fixture = new Fixture();
        var repository = new JsonLibraryRepository(() => fixture.Root);
        var library = new LibraryService(repository);
        var mirror = new PlaylistMirror
        {
            PlaylistId = Guid.NewGuid(),
            SourceKind = PlaylistSourceKind.Folder,
            DirectoryPath = fixture.Root,
            Recursive = false,
            IsPaused = true,
            ContentFingerprint = "snapshot",
            LastSynchronizedAt = DateTimeOffset.UtcNow,
            LastError = "Offline"
        };
        library.Replace(new MusicLibrary
        {
            Playlists =
            [
                new()
                {
                    Id = mirror.PlaylistId,
                    Name = "Persisted",
                    DecodeProfile = new() { Speed = 1.1 },
                    Items = [new() { Track = Song("a"), SortKey = "1000", DecodeProfile = new() { Speed = 1.25 } }]
                }
            ],
            Mirrors = [mirror],
            IsPlaylistSyncPaused = true
        });
        await library.SaveAsync();
        var loaded = await new LibraryService(repository).LoadAsync();
        Assert.True(loaded.IsPlaylistSyncPaused);
        Assert.Equal(mirror, Assert.Single(loaded.Mirrors));
        var playlist = Assert.Single(loaded.Playlists);
        Assert.Equal("Persisted", playlist.Name);
        Assert.Equal(1.1, playlist.DecodeProfile?.Speed);
        Assert.Equal(1.25, Assert.Single(playlist.Items).DecodeProfile?.Speed);
        // Flags are index-only changes and must persist without touching playlist files.
        library.Current.IsPlaylistSyncPaused = false;
        library.TouchIndex();
        await library.SaveAsync();
        Assert.False((await repository.LoadAsync()).IsPlaylistSyncPaused);
    }

    [Fact]
    public async Task FolderSyncAddsRemovesAndPreservesFilesAndSurvivingIds()
    {
        using var fixture = new Fixture();
        var music = Path.Combine(fixture.Root, "music");
        Directory.CreateDirectory(music);
        var first = Path.Combine(music, "a.wav");
        var second = Path.Combine(music, "b.wav");
        await File.WriteAllBytesAsync(first, []);
        await File.WriteAllBytesAsync(second, []);
        var scan = await fixture.Folders.ScanAsync(music, recursive: false);
        var playlist = fixture.Sync.ImportFolder(scan, "Folder", null);
        var survivor = playlist.Items[1].Id;
        File.Delete(first);
        var third = Path.Combine(music, "c.wav");
        await File.WriteAllBytesAsync(third, []);
        Assert.True((await fixture.Sync.SynchronizeAsync(playlist.Id)).Succeeded);
        Assert.Equal(2, playlist.Items.Count);
        Assert.Equal(survivor, playlist.Items[0].Id);
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(third));
        Directory.Move(music, Path.Combine(fixture.Root, "disconnected"));
        Assert.False((await fixture.Sync.SynchronizeAsync(playlist.Id)).Succeeded);
        Assert.Equal(2, playlist.Items.Count);
    }

    [Fact]
    public async Task RefusesFolderSyncWhenNoDecoderOrIncompleteScanIsAvailable()
    {
        using var fixture = new Fixture();
        var scan = new FolderScanResult(fixture.Root, "Folder", [], ["unreadable.wav"]);
        Assert.Throws<InvalidOperationException>(() => fixture.Sync.ImportFolder(scan, "Folder", null));
        Assert.Empty(fixture.Library.Current.Playlists);
        var folders = new FolderImportService(new PlayerRegistry([]), fixture.Playlists,
            () => fixture.Root, NullLogger<FolderImportService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => folders.ScanAsync(fixture.Root));
    }

    private static Track Song(string id) => new() { Id = new("test", id), Title = id };
    private static RemotePlaylist Snapshot(params Track[] tracks) => new()
    {
        Id = "playlist",
        ProviderId = "test",
        Name = "Source",
        Tracks = tracks,
        IsComplete = true
    };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LemonNeo-sync-tests", Guid.NewGuid().ToString("N"));
        public MemoryRepository Repository { get; } = new();
        public TestProvider Provider { get; } = new();
        public LibraryService Library { get; }
        public PlaylistService Playlists { get; }
        public FolderImportService Folders { get; }
        public PlaylistSyncService Sync { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            Library = new(Repository);
            Playlists = new(Library, new FractionalPlaylistOrderingService());
            var registry = new ProviderRegistry(Provider);
            Folders = new(new PlayerRegistry([new Decoder()]), Playlists, () => Root,
                NullLogger<FolderImportService>.Instance);
            Sync = new(Library, Playlists, new(registry, Playlists), Folders, registry);
        }
        public Playlist Import(RemotePlaylist remote) => Sync.ImportRemote(remote, "account");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class MemoryRepository : ILibraryRepository
    {
        public bool FailSave { get; set; }
        public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MusicLibrary> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MusicLibrary());
        public Task SaveAsync(LibrarySaveSnapshot snapshot, CancellationToken cancellationToken = default) =>
            FailSave ? Task.FromException(new IOException("Disk unavailable")) : Task.CompletedTask;
    }

    private sealed class ProviderRegistry(TestProvider provider) : IProviderRegistry
    {
        public IReadOnlyCollection<IMusicProvider> Providers => [provider];
        public IMusicProvider? Get(string providerId) => providerId == provider.Id ? provider : null;
    }

    private sealed class TestProvider : IMusicProvider, IPlaylistProvider, IAuthenticationProvider
    {
        public string Id => "test";
        public ProviderDescriptor Descriptor => new("Test", "Test");
        public AuthenticationState State { get; set; } = new(true, UserId: "account");
        public RemotePlaylist? Remote { get; set; } = Snapshot();
        public Func<string, Task<RemotePlaylist?>>? Read { get; set; }
        public int Requests { get; private set; }
        public Task<RemotePlaylist?> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
        {
            Requests++;
            return Read?.Invoke(playlistId) ?? Task.FromResult(Remote);
        }
        public IAsyncEnumerable<RemotePlaylistSummary> GetUserPlaylistsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<AuthMethod>> GetAvailableMethodsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AuthMethod>>([]);
        public Task<AuthenticationState> AuthenticateAsync(AuthenticationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(State);
        public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class PlayerRegistry(IReadOnlyCollection<IPlayerProvider> players) : IPlayerRegistry
    {
        public IReadOnlyCollection<IPlayerProvider> Players => players;
        public IPlayerProvider? Get(string playerId) => players.FirstOrDefault(player => player.Id == playerId);
        public IPlayerProvider GetRequiredPlayer(PlaybackSource source, string? preferredPlayerId = null) =>
            players.First(player => player.CanPlay(source));
    }

    private sealed class Decoder : IPlayerProvider
    {
        public string Id => "test";
        public PlayerDescriptor Descriptor => new("Test", "Test");
        public IReadOnlyCollection<string> SupportedExtensions => [".wav"];
        public IPlayer Player => throw new NotSupportedException();
        public bool CanPlay(PlaybackSource source) => source is FilePlaybackSource;
    }
}
