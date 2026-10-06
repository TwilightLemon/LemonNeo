using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Library;
using LemonNeo.Core.Music;
using System.Globalization;

namespace LemonNeo.Application.Library;

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
        ValidateTrack(track);
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

    public Playlist Update(Guid playlistId, string name, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var playlist = GetRequired(playlistId);
        playlist.Name = name.Trim();
        playlist.Description = description;
        Touch(playlist);
        return playlist;
    }

    public bool Delete(Guid playlistId)
    {
        if (library.Current.Playlists.RemoveAll(playlist => playlist.Id == playlistId) == 0)
        {
            return false;
        }

        library.Current.Mirrors.RemoveAll(mirror => mirror.PlaylistId == playlistId);
        library.Touch();
        return true;
    }

    public PlaylistItem GetItemRequired(Guid playlistId, Guid itemId) =>
        GetRequired(playlistId).Items.FirstOrDefault(item => item.Id == itemId)
        ?? throw new ArgumentException($"Playlist item '{itemId}' was not found.");

    public PlaylistItem UpdateTrack(Guid playlistId, Guid itemId, Track track)
    {
        ValidateTrack(track);
        var playlist = GetRequired(playlistId);
        var item = GetItemRequired(playlistId, itemId);
        item.Track = track;
        Touch(playlist);
        return item;
    }

    public void Move(Guid playlistId, Guid itemId, int position)
    {
        var playlist = GetRequired(playlistId);
        var item = GetItemRequired(playlistId, itemId);
        if (position < 1 || position > playlist.Items.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(position),
                $"Position must be between 1 and {playlist.Items.Count}.");
        }

        var items = playlist.Items.OrderBy(value => FractionalPlaylistOrderingService.Parse(value.SortKey))
            .ThenBy(value => value.Id).ToList();
        items.Remove(item);
        items.Insert(position - 1, item);
        // Assign temporary distinct keys in the requested order before normalizing.
        for (var index = 0; index < items.Count; index++)
        {
            items[index].SortKey = (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        ordering.Normalize(playlist);
        Touch(playlist);
    }

    public IReadOnlyList<ExternalMusicId> GetExternalIds(Guid playlistId, Guid itemId) =>
        GetItemRequired(playlistId, itemId).Track.ExternalIds;

    public void AddExternalId(Guid playlistId, Guid itemId, ExternalMusicId id)
    {
        ValidateExternalId(id);
        var item = GetItemRequired(playlistId, itemId);
        if (item.Track.ExternalIds.Any(value => SameScheme(value, id)))
        {
            throw new ArgumentException($"External ID scheme '{id.Namespace}:{id.Kind ?? "-"}' already exists. Use update.");
        }

        UpdateTrack(playlistId, itemId, item.Track with { ExternalIds = [.. item.Track.ExternalIds, id] });
    }

    public void UpdateExternalId(Guid playlistId, Guid itemId, ExternalMusicId id)
    {
        ValidateExternalId(id);
        var item = GetItemRequired(playlistId, itemId);
        if (!item.Track.ExternalIds.Any(value => SameScheme(value, id)))
        {
            throw new ArgumentException($"External ID scheme '{id.Namespace}:{id.Kind ?? "-"}' was not found.");
        }

        UpdateTrack(playlistId, itemId, item.Track with
        {
            ExternalIds = item.Track.ExternalIds.Select(value => SameScheme(value, id) ? id : value).ToArray()
        });
    }

    public bool RemoveExternalId(Guid playlistId, Guid itemId, string namespaceName, string? kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        var item = GetItemRequired(playlistId, itemId);
        var scheme = new MusicIdScheme(namespaceName, kind);
        var ids = item.Track.ExternalIds.Where(id => !scheme.Accepts(id)).ToArray();
        if (ids.Length == item.Track.ExternalIds.Count)
        {
            return false;
        }

        UpdateTrack(playlistId, itemId, item.Track with { ExternalIds = ids });
        return true;
    }

    private static bool SameScheme(ExternalMusicId left, ExternalMusicId right) =>
        new MusicIdScheme(left.Namespace, left.Kind).Accepts(right);

    private static void ValidateExternalId(ExternalMusicId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
        if (id.Kind is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id.Kind);
        }
    }

    private static void ValidateTrack(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentException.ThrowIfNullOrWhiteSpace(track.Title);
        ArgumentException.ThrowIfNullOrWhiteSpace(track.Id.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(track.Id.RemoteId);
        if (track.Duration < TimeSpan.Zero)
        {
            throw new ArgumentException("Track duration cannot be negative.");
        }
    }

    private void Touch(Playlist playlist)
    {
        playlist.UpdatedAt = DateTimeOffset.UtcNow;
        library.Touch();
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
        var remote = await GetRemotePlaylistAsync(providerId, remotePlaylistId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return ImportRemote(remote);
    }

    /// <summary>Reads provider data without changing the shared library.</summary>
    public async Task<RemotePlaylist> GetRemotePlaylistAsync(
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

        return remote;
    }

    public Playlist ImportRemote(RemotePlaylist remote)
    {
        var local = playlists.Create(remote.Name, remote.Description);
        foreach (var track in remote.Tracks)
        {
            playlists.Add(local.Id, track);
        }

        return local;
    }
}
