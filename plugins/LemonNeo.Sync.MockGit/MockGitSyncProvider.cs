using LemonNeo.Core;
using LemonNeo.Sync.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Sync.MockGit;

public sealed class MockGitBackend(ILogger<MockGitBackend> logger) : IGitBackend
{
    public Task FetchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Fetching remote library...");
        return Task.CompletedTask;
    }

    public Task PullAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Reading remote revision mock-13...");
        return Task.CompletedTask;
    }

    public Task CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Creating mock commit: {Message}", message);
        return Task.CompletedTask;
    }

    public Task PushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Pushing mock revision...");
        return Task.CompletedTask;
    }
}

public sealed class MockGitSyncProvider(IGitBackend backend) : ILibrarySyncProvider
{
    private static readonly Guid RemotePlaylistId = Guid.Parse("88888888-8888-4888-8888-888888888888");
    private static readonly Guid RemoteItemId = Guid.Parse("99999999-9999-4999-8999-999999999999");

    public string Id => "mock-git";

    public async Task<SyncResult> PullAsync(CancellationToken cancellationToken = default)
    {
        await backend.FetchAsync(cancellationToken);
        await backend.PullAsync(cancellationToken);
        return new SyncResult
        {
            Success = true,
            Message = "Fetched mock remote library.",
            LocalRevision = "mock-12",
            RemoteRevision = "mock-13",
            BaseLibrary = new MusicLibrary(),
            RemoteLibrary = CreateRemoteLibrary()
        };
    }

    public async Task<SyncResult> PushAsync(CancellationToken cancellationToken = default)
    {
        await backend.CommitAsync("Update Lemon Neo library", cancellationToken);
        await backend.PushAsync(cancellationToken);
        return new SyncResult
        {
            Success = true,
            Message = "Mock library committed and pushed.",
            LocalRevision = "mock-14",
            RemoteRevision = "mock-14"
        };
    }

    public Task<SyncStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SyncStatus(
            SyncState.LocalChanges,
            "mock-12",
            "mock-13",
            "Mock remote has one demonstration playlist."));
    }

    private static MusicLibrary CreateRemoteLibrary()
    {
        var track = new Track
        {
            Id = new MusicItemId("mock", "track-003"),
            Title = "Midnight Compile",
            Artists = [new ArtistRef { Name = "Digital Cat" }],
            ExternalIds = [new ExternalMusicId("isrc", "MOCK00000003")],
            IsPlayable = true
        };
        var playlist = new Playlist
        {
            Id = RemotePlaylistId,
            Name = "Remote Discoveries",
            Description = "Added by the mock Git remote.",
            CreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Items =
            [
                new PlaylistItem
                {
                    Id = RemoteItemId,
                    Track = track.ToReference(),
                    SortKey = "1000",
                    AddedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
                }
            ]
        };

        return new MusicLibrary { Tracks = [track], Playlists = [playlist] };
    }
}
