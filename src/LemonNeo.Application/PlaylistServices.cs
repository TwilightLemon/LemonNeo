using System.Globalization;
using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed class FractionalPlaylistOrderingService : IPlaylistOrderingService
{
    private const decimal Step = 1000m;
    private const decimal MinimumGap = 0.000001m;

    public string GetKeyForEnd(IReadOnlyCollection<PlaylistItem> items)
    {
        var last = items.Count == 0
            ? 0m
            : items.Max(item => Parse(item.SortKey));
        return Format(last + Step);
    }

    public string GetKeyBetween(PlaylistItem? before, PlaylistItem? after)
    {
        var lower = before is null ? 0m : Parse(before.SortKey);
        var upper = after is null ? lower + (Step * 2) : Parse(after.SortKey);
        if (upper - lower <= MinimumGap)
        {
            throw new InvalidOperationException("The playlist must be normalized before inserting between these items.");
        }

        return Format((lower + upper) / 2m);
    }

    public void Normalize(Playlist playlist)
    {
        var index = 1;
        foreach (var item in playlist.Items.OrderBy(item => Parse(item.SortKey)).ThenBy(item => item.Id))
        {
            item.SortKey = Format(Step * index++);
        }
    }

    public static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static string Format(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}

public sealed class PlaylistService(
    LibraryService library,
    IPlaylistOrderingService ordering)
{
    public Playlist Create(string name, string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var playlist = new Playlist { Name = name.Trim(), Description = description };
        library.Current.Playlists.Add(playlist);
        library.Touch();
        return playlist;
    }

    public PlaylistItem Add(Guid playlistId, Track track)
    {
        var playlist = GetRequired(playlistId);
        var item = new PlaylistItem
        {
            Track = track,
            SortKey = ordering.GetKeyForEnd(playlist.Items)
        };
        playlist.Items.Add(item);
        playlist.UpdatedAt = DateTimeOffset.UtcNow;
        library.Touch();
        return item;
    }

    public bool Remove(Guid playlistId, Guid itemId)
    {
        var playlist = GetRequired(playlistId);
        var removed = playlist.Items.RemoveAll(item => item.Id == itemId) > 0;
        if (removed)
        {
            playlist.UpdatedAt = DateTimeOffset.UtcNow;
            library.Touch();
        }

        return removed;
    }

    public Playlist GetRequired(Guid playlistId) =>
        library.Current.Playlists.FirstOrDefault(playlist => playlist.Id == playlistId)
        ?? throw new ArgumentException($"Playlist '{playlistId}' was not found.");
}

public sealed class ImportService(
    IProviderRegistry registry,
    PlaylistService playlists)
{
    public async Task<Playlist> ImportAsync(
        string providerId,
        string remotePlaylistId,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(providerId)
            ?? throw new ProviderException($"Provider '{providerId}' was not found.");
        if (provider is not IPlaylistProvider playlistProvider)
        {
            throw new ProviderException($"Provider '{providerId}' does not support playlists.");
        }

        RemotePlaylist remote;
        try
        {
            remote = await playlistProvider.GetPlaylistAsync(remotePlaylistId, cancellationToken)
                ?? throw new ProviderException(
                    $"Remote playlist '{remotePlaylistId}' was not found on provider '{providerId}'.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ProviderException)
        {
            throw new ProviderException($"Could not import playlist '{remotePlaylistId}'.", exception);
        }

        var local = playlists.Create(remote.Name, remote.Description);
        foreach (var track in remote.Tracks)
        {
            playlists.Add(local.Id, track);
        }

        return local;
    }
}
