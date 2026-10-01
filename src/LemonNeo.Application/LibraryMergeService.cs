using System.Text.Json;
using LemonNeo.Core;
using LemonNeo.Sync.Abstractions;

namespace LemonNeo.Application;

public sealed class LibraryMergeService : ILibraryMergeService
{
    public MergeResult Merge(MusicLibrary @base, MusicLibrary local, MusicLibrary remote)
    {
        var conflicts = new List<SyncConflict>();
        var playlists = new List<Playlist>();
        var addedItems = 0;
        var changedPlaylists = 0;
        var baseById = @base.Playlists.ToDictionary(playlist => playlist.Id);
        var localById = local.Playlists.ToDictionary(playlist => playlist.Id);
        var remoteById = remote.Playlists.ToDictionary(playlist => playlist.Id);
        var allIds = baseById.Keys.Union(localById.Keys).Union(remoteById.Keys).Order();

        foreach (var id in allIds)
        {
            baseById.TryGetValue(id, out var basePlaylist);
            localById.TryGetValue(id, out var localPlaylist);
            remoteById.TryGetValue(id, out var remotePlaylist);
            var merged = MergePlaylist(basePlaylist, localPlaylist, remotePlaylist, conflicts, ref addedItems);
            if (merged is not null)
            {
                playlists.Add(merged);
                if (!Equivalent(basePlaylist, merged))
                {
                    changedPlaylists++;
                }
            }
        }

        var mirrors = local.Mirrors.Concat(remote.Mirrors)
            .GroupBy(mirror => new { mirror.PlaylistId, mirror.ProviderId, mirror.RemotePlaylistId })
            .Select(group => group.First())
            .ToList();

        return new MergeResult(
            new MusicLibrary
            {
                FormatVersion = Math.Max(local.FormatVersion, remote.FormatVersion),
                UpdatedAt = DateTimeOffset.UtcNow,
                Playlists = playlists,
                Mirrors = mirrors
            },
            conflicts,
            addedItems,
            changedPlaylists);
    }

    private static Playlist? MergePlaylist(
        Playlist? @base,
        Playlist? local,
        Playlist? remote,
        List<SyncConflict> conflicts,
        ref int addedItems)
    {
        if (@base is null)
        {
            if (local is null)
            {
                addedItems += remote?.Items.Count ?? 0;
                return remote is null ? null : Clone(remote);
            }

            if (remote is null || Equivalent(local, remote))
            {
                return Clone(local);
            }

            return MergeExisting(new Playlist
            {
                Id = local.Id,
                Name = local.Name,
                Description = local.Description,
                CreatedAt = Min(local.CreatedAt, remote.CreatedAt)
            }, local, remote, conflicts, ref addedItems);
        }

        if (local is null && remote is null)
        {
            return null;
        }

        if (local is null)
        {
            if (Equivalent(@base, remote))
            {
                return null;
            }

            conflicts.Add(Conflict($"playlists/{@base.Id}", SyncConflictType.DeleteModifyConflict, @base, null, remote));
            return Clone(remote!);
        }

        if (remote is null)
        {
            if (Equivalent(@base, local))
            {
                return null;
            }

            conflicts.Add(Conflict($"playlists/{@base.Id}", SyncConflictType.DeleteModifyConflict, @base, local, null));
            return Clone(local);
        }

        return MergeExisting(@base, local, remote, conflicts, ref addedItems);
    }

    private static Playlist MergeExisting(
        Playlist @base,
        Playlist local,
        Playlist remote,
        List<SyncConflict> conflicts,
        ref int addedItems)
    {
        var path = $"playlists/{local.Id}";
        var name = MergeValue(
            $"{path}/name",
            SyncConflictType.PlaylistNameConflict,
            @base.Name,
            local.Name,
            remote.Name,
            conflicts);
        var description = MergeValue(
            $"{path}/description",
            SyncConflictType.PlaylistDescriptionConflict,
            @base.Description,
            local.Description,
            remote.Description,
            conflicts);
        var items = MergeItems(@base, local, remote, conflicts, ref addedItems);

        return new Playlist
        {
            Id = local.Id,
            Name = name,
            Description = description,
            CreatedAt = Min(local.CreatedAt, remote.CreatedAt),
            UpdatedAt = Max(local.UpdatedAt, remote.UpdatedAt),
            Items = items
                .OrderBy(item => FractionalPlaylistOrderingService.Parse(item.SortKey))
                .ThenBy(item => item.Id)
                .ToList()
        };
    }

    private static List<PlaylistItem> MergeItems(
        Playlist @base,
        Playlist local,
        Playlist remote,
        List<SyncConflict> conflicts,
        ref int addedItems)
    {
        var result = new List<PlaylistItem>();
        var baseById = @base.Items.ToDictionary(item => item.Id);
        var localById = local.Items.ToDictionary(item => item.Id);
        var remoteById = remote.Items.ToDictionary(item => item.Id);

        foreach (var id in baseById.Keys.Union(localById.Keys).Union(remoteById.Keys).Order())
        {
            baseById.TryGetValue(id, out var baseItem);
            localById.TryGetValue(id, out var localItem);
            remoteById.TryGetValue(id, out var remoteItem);
            var path = $"playlists/{local.Id}/items/{id}";

            if (baseItem is null)
            {
                if (localItem is null && remoteItem is not null)
                {
                    result.Add(Clone(remoteItem));
                    addedItems++;
                }
                else if (remoteItem is null && localItem is not null)
                {
                    result.Add(Clone(localItem));
                }
                else if (localItem is not null && remoteItem is not null)
                {
                    if (!Equivalent(localItem, remoteItem))
                    {
                        conflicts.Add(Conflict(path, SyncConflictType.PlaylistItemConflict, null, localItem, remoteItem));
                    }

                    result.Add(Clone(localItem));
                }

                continue;
            }

            if (localItem is null && remoteItem is null)
            {
                continue;
            }

            if (localItem is null)
            {
                if (!Equivalent(baseItem, remoteItem))
                {
                    conflicts.Add(Conflict(path, SyncConflictType.DeleteModifyConflict, baseItem, null, remoteItem));
                    result.Add(Clone(remoteItem!));
                }

                continue;
            }

            if (remoteItem is null)
            {
                if (!Equivalent(baseItem, localItem))
                {
                    conflicts.Add(Conflict(path, SyncConflictType.DeleteModifyConflict, baseItem, localItem, null));
                    result.Add(Clone(localItem));
                }

                continue;
            }

            var track = MergeValue(
                $"{path}/track",
                SyncConflictType.PlaylistItemConflict,
                baseItem.Track,
                localItem.Track,
                remoteItem.Track,
                conflicts);
            var sortKey = MergeValue(
                $"{path}/sortKey",
                SyncConflictType.OrderConflict,
                baseItem.SortKey,
                localItem.SortKey,
                remoteItem.SortKey,
                conflicts);
            result.Add(new PlaylistItem
            {
                Id = id,
                Track = track,
                SortKey = sortKey,
                AddedAt = Min(localItem.AddedAt, remoteItem.AddedAt)
            });
        }

        return result;
    }

    private static T MergeValue<T>(
        string path,
        SyncConflictType type,
        T @base,
        T local,
        T remote,
        List<SyncConflict> conflicts)
    {
        if (Equivalent(local, remote))
        {
            return local;
        }

        if (Equivalent(local, @base))
        {
            return remote;
        }

        if (Equivalent(remote, @base))
        {
            return local;
        }

        conflicts.Add(Conflict(path, type, @base, local, remote));
        return local;
    }

    private static SyncConflict Conflict(
        string path,
        SyncConflictType type,
        object? baseValue,
        object? localValue,
        object? remoteValue) => new()
        {
            Path = path,
            Type = type,
            BaseValue = baseValue,
            LocalValue = localValue,
            RemoteValue = remoteValue
        };

    private static bool Equivalent<T>(T? left, T? right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static Playlist Clone(Playlist playlist) => new()
    {
        Id = playlist.Id,
        Name = playlist.Name,
        Description = playlist.Description,
        CreatedAt = playlist.CreatedAt,
        UpdatedAt = playlist.UpdatedAt,
        Items = playlist.Items.Select(Clone).ToList()
    };

    private static PlaylistItem Clone(PlaylistItem item) => new()
    {
        Id = item.Id,
        Track = item.Track,
        SortKey = item.SortKey,
        AddedAt = item.AddedAt
    };

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;
}
