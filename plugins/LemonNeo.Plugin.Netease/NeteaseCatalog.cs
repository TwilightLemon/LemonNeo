using System.Text.Json;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;

namespace LemonNeo.Plugin.Netease;

public sealed partial class NeteaseUserPlugin
{
    public IReadOnlyCollection<string> SearchNamespaces { get; } = [MusicIdNamespaces.NeteaseMusic];

    public async Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
        new(ProviderId, await SearchCatalogAsync(request, 1, "songs", TryMapTrack, cancellationToken));

    public Task<IReadOnlyList<Album>> SearchAlbumsAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
        SearchCatalogAsync(request, 10, "albums", TryMapAlbum, cancellationToken);

    public Task<IReadOnlyList<Artist>> SearchArtistsAsync(SearchRequest request, CancellationToken cancellationToken = default) =>
        SearchCatalogAsync(request, 100, "artists", TryMapArtist, cancellationToken);

    public async Task<AlbumDetail?> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default)
    {
        if (albumId.Length == 0 || !albumId.All(char.IsAsciiDigit)) return null;
        using var document = await RequestJsonAsync($"api/v1/album/{albumId}", "", cancellationToken);
        var root = document.RootElement;
        if (ReadInt32(root, "code") == 404) return null;
        EnsureApiSuccess(root);
        if (!root.TryGetProperty("album", out var value) || TryMapAlbum(value) is not { } album) return null;
        var songs = GetItems(root, "songs").Concat(GetItems(value, "songs"));
        var tracks = songs.Select(TryMapTrack).OfType<Track>().DistinctBy(track => track.Id).Select(track => track with
        {
            Album = new AlbumRef { Id = album.Id, Title = album.Title },
            ArtworkUri = track.ArtworkUri ?? album.ArtworkUri
        }).ToArray();
        return new AlbumDetail(album, tracks, ReadString(value, "description"));
    }

    public async Task<Artist?> GetArtistAsync(string artistId, CancellationToken cancellationToken = default)
    {
        if (artistId.Length == 0 || !artistId.All(char.IsAsciiDigit)) return null;
        using var document = await RequestJsonAsync($"api/artist/{artistId}", "", cancellationToken);
        var root = document.RootElement;
        if (ReadInt32(root, "code") == 404) return null;
        EnsureApiSuccess(root);
        return root.TryGetProperty("artist", out var value) ? TryMapArtist(value) : null;
    }

    private async Task<IReadOnlyList<T>> SearchCatalogAsync<T>(SearchRequest request, int type, string resultKey,
        Func<JsonElement, T?> map, CancellationToken cancellationToken) where T : class
    {
        if (request.SourceNamespace is { } source &&
            !source.Equals(MusicIdNamespaces.NeteaseMusic, StringComparison.OrdinalIgnoreCase)) return [];
        var keyword = Uri.EscapeDataString(request.Keyword);
        var limit = Math.Clamp(request.Limit, 1, 100);
        using var document = await RequestJsonAsync($"api/search/get?s={keyword}&type={type}&limit={limit}", "", cancellationToken);
        EnsureApiSuccess(document.RootElement);
        return document.RootElement.TryGetProperty("result", out var result)
            ? GetItems(result, resultKey).Select(map).OfType<T>().ToArray() : [];
    }

    private static IEnumerable<JsonElement> GetItems(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var array) &&
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];

    private static MusicItemId? ReadMusicId(JsonElement value) =>
        TryReadId(value, "id", out var id) && id != "0" ? new MusicItemId(ProviderId, id) : null;

    private static IReadOnlyList<ArtistRef> MapArtists(JsonElement value) =>
        GetItems(value, "ar").Concat(GetItems(value, "artists"))
            .Where(artist => artist.ValueKind == JsonValueKind.Object && ReadString(artist, "name") is { Length: > 0 })
            .Select(artist => new ArtistRef { Name = ReadString(artist, "name")!, Id = ReadMusicId(artist) })
            .ToArray();

    private static Album? TryMapAlbum(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && ReadMusicId(value) is { } id &&
        ReadString(value, "name") is { Length: > 0 } title
            ? new Album { Id = id, Title = title, Artists = MapArtists(value), ArtworkUri = TryCreateAvatarUri(ReadString(value, "picUrl")) }
            : null;

    private static Artist? TryMapArtist(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object && ReadMusicId(value) is { } id &&
        ReadString(value, "name") is { Length: > 0 } name
            ? new Artist { Id = id, Name = name, ArtworkUri = TryCreateAvatarUri(ReadString(value, "picUrl") ?? ReadString(value, "img1v1Url")) }
            : null;
}
