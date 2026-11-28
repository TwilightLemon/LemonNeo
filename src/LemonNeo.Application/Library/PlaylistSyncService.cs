using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;

namespace LemonNeo.Application.Library;

public sealed record PlaylistSyncResult(Guid PlaylistId, bool Succeeded, bool Changed = false, string? Error = null,
    bool Skipped = false);

/// <summary>
/// Source reads run in the background; library mutations resume on the caller's context, like other library commands.
/// Desktop callers must use the UI context. A single gate also serializes requests to providers.
/// </summary>
public sealed class PlaylistSyncService(
    LibraryService library,
    PlaylistService playlists,
    ImportService importer,
    FolderImportService folders,
    IProviderRegistry registry)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _pauseVersion;
    public event EventHandler? Changed;

    /// <summary>Prevents a sync commit while the host changes the repository's backing directory.</summary>
    public async Task<IDisposable> AcquireLibraryChangeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        return new GateLease(_gate);
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }

    public PlaylistMirror? GetMirror(Guid playlistId)
    {
        lock (library.SyncRoot)
            return library.Current.Mirrors.FirstOrDefault(mirror => mirror.PlaylistId == playlistId);
    }

    public string? GetAccountId(string providerId)
    {
        var provider = registry.Get(providerId)
                       ?? throw new InvalidOperationException($"Provider '{providerId}' is unavailable.");
        if (provider is not IAuthenticationProvider authentication) return null;
        if (!authentication.State.IsAuthenticated || string.IsNullOrWhiteSpace(authentication.State.UserId))
            throw new InvalidOperationException("Sign in to the source account before synchronizing this playlist.");
        return authentication.State.UserId;
    }

    public Playlist ImportRemote(RemotePlaylist remote, string? accountId)
    {
        using var mutation = library.BeginMutation();
        Validate(remote);
        if (GetAccountId(remote.ProviderId) != accountId)
            throw new InvalidOperationException("The source account changed. Import this playlist again.");
        var existing = library.Current.Mirrors.FirstOrDefault(mirror =>
            mirror.SourceKind == PlaylistSourceKind.Provider && mirror.ProviderId == remote.ProviderId &&
            mirror.RemotePlaylistId == remote.Id && mirror.AccountId == accountId);
        if (existing is not null) return playlists.GetRequired(existing.PlaylistId);
        var playlist = importer.ImportRemote(remote);
        library.Current.Mirrors.Add(new PlaylistMirror
        {
            PlaylistId = playlist.Id,
            ProviderId = remote.ProviderId,
            RemotePlaylistId = remote.Id,
            AccountId = accountId,
            LastSynchronizedAt = DateTimeOffset.UtcNow,
            ContentFingerprint = Fingerprint(remote)
        });
        library.TouchIndex();
        return playlist;
    }

    public Playlist ImportFolder(FolderScanResult scan, string name, string? description)
    {
        using var mutation = library.BeginMutation();
        if (!scan.IsComplete) throw new InvalidOperationException("The folder scan is incomplete. No sync playlist was created.");
        var existing = library.Current.Mirrors.FirstOrDefault(mirror =>
            mirror.SourceKind == PlaylistSourceKind.Folder && mirror.Recursive == scan.Recursive &&
            string.Equals(mirror.DirectoryPath, scan.DirectoryPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (existing is not null) return playlists.GetRequired(existing.PlaylistId);
        var playlist = folders.ImportScan(scan, name).Playlist;
        playlists.Update(playlist.Id, name, description);
        var remote = FolderSnapshot(scan, playlist);
        library.Current.Mirrors.Add(new PlaylistMirror
        {
            PlaylistId = playlist.Id,
            SourceKind = PlaylistSourceKind.Folder,
            DirectoryPath = scan.DirectoryPath,
            Recursive = scan.Recursive,
            LastSynchronizedAt = DateTimeOffset.UtcNow,
            ContentFingerprint = Fingerprint(remote)
        });
        library.TouchIndex();
        return playlist;
    }

    public async Task SetPausedAsync(Guid? playlistId, bool paused, CancellationToken cancellationToken = default)
    {
        using (library.BeginMutation())
        {
            if (playlistId is { } id)
            {
                if (GetMirror(id) is not { } mirror) return;
                ReplaceMirror(mirror, mirror with { IsPaused = paused });
            }
            else
            {
                _pauseVersion++;
                library.Current.IsPlaylistSyncPaused = paused;
                library.TouchIndex();
            }
        }
        await library.SaveAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DetachAsync(Guid playlistId, CancellationToken cancellationToken = default)
    {
        using (library.BeginMutation())
        {
            library.Current.Mirrors.RemoveAll(mirror => mirror.PlaylistId == playlistId);
            library.TouchIndex();
        }
        await library.SaveAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<PlaylistSyncResult>> SynchronizeAllAsync(bool automatic = false,
        CancellationToken cancellationToken = default)
    {
        var sourceLibrary = library.Current;
        var ids = sourceLibrary.Mirrors.Select(mirror => mirror.PlaylistId).ToArray();
        var results = new List<PlaylistSyncResult>();
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(sourceLibrary, library.Current)) break;
            results.Add(await SynchronizeAsync(id, automatic, cancellationToken));
        }
        return results;
    }

    public async Task<PlaylistSyncResult> SynchronizeAsync(Guid playlistId, bool automatic = false,
        CancellationToken cancellationToken = default)
    {
        var requestedLibrary = library.Current;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(requestedLibrary, library.Current)) return new(playlistId, false, Skipped: true);
            var sourceLibrary = library.Current;
            var mirror = GetMirror(playlistId);
            if (mirror is null || automatic && (mirror.IsPaused || sourceLibrary.IsPlaylistSyncPaused))
                return new(playlistId, false, Skipped: true);
            var playlist = playlists.GetRequired(playlistId);
            var pauseVersion = _pauseVersion;
            bool StillCurrent()
            {
                lock (library.SyncRoot)
                    return ReferenceEquals(sourceLibrary, library.Current) &&
                GetMirror(playlistId) is { } latest && SameSource(mirror, latest) &&
                library.Current.Playlists.Any(value => ReferenceEquals(value, playlist)) &&
                (!automatic || ReferenceEquals(mirror, latest) && pauseVersion == _pauseVersion &&
                    !sourceLibrary.IsPlaylistSyncPaused && !latest.IsPaused);
            }

            RemotePlaylist remote;
            try
            {
                if (mirror.SourceKind == PlaylistSourceKind.Folder)
                {
                    var scan = await Task.Run(() => folders.ScanAsync(mirror.DirectoryPath!, mirror.Recursive,
                        cancellationToken), cancellationToken);
                    remote = FolderSnapshot(scan, playlist);
                }
                else
                {
                    if (GetAccountId(mirror.ProviderId) != mirror.AccountId)
                        throw new InvalidOperationException("The source account changed. Sign in to the original account to synchronize.");
                    remote = await Task.Run(() => importer.GetRemotePlaylistAsync(mirror.ProviderId,
                        mirror.RemotePlaylistId, cancellationToken), cancellationToken);
                    if (GetAccountId(mirror.ProviderId) != mirror.AccountId)
                        throw new InvalidOperationException("The source account changed while reading the playlist.");
                    if (remote.ProviderId != mirror.ProviderId || remote.Id != mirror.RemotePlaylistId)
                        throw new InvalidOperationException("The provider returned a different playlist.");
                }
                Validate(remote);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                using (library.BeginMutation())
                {
                    if (!StillCurrent()) return new(playlistId, false, Skipped: true);
                    var latest = GetMirror(playlistId)!;
                    ReplaceMirror(latest, latest with { LastError = exception.Message });
                }
                await library.SaveAsync(cancellationToken);
                Changed?.Invoke(this, EventArgs.Empty);
                return new(playlistId, false, Error: exception.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = Fingerprint(remote);
            var changed = fingerprint != mirror.ContentFingerprint;
            string previousName;
            string? previousDescription;
            Uri? previousArtwork;
            DateTimeOffset previousUpdatedAt;
            PlaylistItem[] previousItems;
            using (library.BeginMutation())
            {
                if (!StillCurrent()) return new(playlistId, false, Skipped: true);
                previousName = playlist.Name;
                previousDescription = playlist.Description;
                previousArtwork = playlist.ArtworkUri;
                previousUpdatedAt = playlist.UpdatedAt;
                previousItems = playlist.Items.ToArray();
                if (changed)
                {
                    // Queues preserve duplicate occurrences and stable item IDs when membership/order changes.
                    var existing = playlist.Items.OrderBy(item => FractionalPlaylistOrderingService.Parse(item.SortKey))
                        .ThenBy(item => item.Id).GroupBy(item => item.Track.Id)
                        .ToDictionary(group => group.Key, group => new Queue<PlaylistItem>(group));
                    var next = remote.Tracks.Select((track, index) =>
                    {
                        var previous = existing.TryGetValue(track.Id, out var queue) && queue.TryDequeue(out var item)
                            ? item : null;
                        return new PlaylistItem
                        {
                            Id = previous?.Id ?? Guid.NewGuid(),
                            AddedAt = previous?.AddedAt ?? DateTimeOffset.UtcNow,
                            DecodeProfile = previous?.DecodeProfile,
                            HasMetadataOverride = previous?.HasMetadataOverride ?? false,
                            ExternalIdOverrides = previous?.ExternalIdOverrides ?? [],
                            SortKey = ((index + 1) * 1000m).ToString(CultureInfo.InvariantCulture),
                            Track = previous is null ? track : Merge(track, previous)
                        };
                    }).ToArray();
                    playlist.Name = remote.Name;
                    playlist.Description = remote.Description;
                    playlist.ArtworkUri = remote.ArtworkUri ?? playlist.ArtworkUri;
                    playlist.Items.Clear();
                    playlist.Items.AddRange(next);
                    playlist.UpdatedAt = DateTimeOffset.UtcNow;
                    library.Touch(playlistId);
                }
                var currentMirror = GetMirror(playlistId)!;
                ReplaceMirror(currentMirror, currentMirror with
                {
                    ContentFingerprint = fingerprint,
                    LastSynchronizedAt = DateTimeOffset.UtcNow,
                    LastError = null
                });
            }
            // Once applied, finish persistence even if the caller cancels while the save is underway.
            try { await library.SaveAsync(CancellationToken.None); }
            catch (Exception exception)
            {
                using (library.BeginMutation())
                {
                    if (ReferenceEquals(sourceLibrary, library.Current) && GetMirror(playlistId) is { } latest)
                    {
                        playlist.Name = previousName;
                        playlist.Description = previousDescription;
                        if (mirror.SourceKind == PlaylistSourceKind.Provider) playlist.ArtworkUri = previousArtwork;
                        playlist.UpdatedAt = previousUpdatedAt;
                        playlist.Items.Clear();
                        playlist.Items.AddRange(previousItems);
                        library.Touch(playlistId);
                        ReplaceMirror(latest, latest with
                        {
                            ContentFingerprint = mirror.ContentFingerprint,
                            LastSynchronizedAt = mirror.LastSynchronizedAt,
                            LastError = exception.Message
                        });
                    }
                }
                Changed?.Invoke(this, EventArgs.Empty);
                return new(playlistId, false, Error: exception.Message);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return new(playlistId, true, changed);
        }
        finally { _gate.Release(); }
    }

    private void ReplaceMirror(PlaylistMirror previous, PlaylistMirror next)
    {
        var index = library.Current.Mirrors.IndexOf(previous);
        if (index < 0) return;
        library.Current.Mirrors[index] = next;
        library.TouchIndex();
    }

    private static RemotePlaylist FolderSnapshot(FolderScanResult scan, Playlist playlist) => new()
    {
        Id = scan.DirectoryPath,
        ProviderId = "local",
        Name = playlist.Name,
        Description = playlist.Description,
        Tracks = scan.Tracks,
        IsComplete = scan.IsComplete
    };

    private static bool SameSource(PlaylistMirror first, PlaylistMirror second) =>
        first.PlaylistId == second.PlaylistId && first.SourceKind == second.SourceKind &&
        first.ProviderId == second.ProviderId && first.RemotePlaylistId == second.RemotePlaylistId &&
        first.AccountId == second.AccountId && first.DirectoryPath == second.DirectoryPath && first.Recursive == second.Recursive;

    private static void Validate(RemotePlaylist remote)
    {
        if (!remote.IsComplete)
            throw new InvalidOperationException("The source did not confirm a complete playlist. The previous snapshot was kept.");
        ArgumentException.ThrowIfNullOrWhiteSpace(remote.Name);
        foreach (var track in remote.Tracks)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(track.Id.ProviderId);
            ArgumentException.ThrowIfNullOrWhiteSpace(track.Id.RemoteId);
            ArgumentException.ThrowIfNullOrWhiteSpace(track.Title);
            if (track.Duration < TimeSpan.Zero) throw new InvalidOperationException("Invalid track duration.");
        }
    }

    private static string Fingerprint(RemotePlaylist remote) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { remote.Name, remote.Description, remote.ArtworkUri, remote.Tracks })));

    private static Track Merge(Track source, PlaylistItem item)
    {
        var previous = item.Track;
        bool IsOverridden(ExternalMusicId id) => item.ExternalIdOverrides.Any(scheme => scheme.Accepts(id));
        return source with
        {
            Title = item.HasMetadataOverride ? previous.Title : source.Title,
            Artists = item.HasMetadataOverride || source.Artists.Count == 0 ? previous.Artists : source.Artists,
            Album = item.HasMetadataOverride ? previous.Album : source.Album ?? previous.Album,
            Duration = item.HasMetadataOverride ? previous.Duration : source.Duration ?? previous.Duration,
            ArtworkUri = item.HasMetadataOverride ? previous.ArtworkUri : source.ArtworkUri ?? previous.ArtworkUri,
            ExternalIds = source.ExternalIds.Where(id => !IsOverridden(id)).Concat(previous.ExternalIds.Where(id =>
                IsOverridden(id) || !source.ExternalIds.Any(value =>
                    new MusicIdScheme(value.Namespace, value.Kind).Accepts(id)))).ToArray()
        };
    }
}
