using LemonNeo.Core.Music;

namespace LemonNeo.Abstractions.Providers;

public sealed record AlbumDetail(Album Album, IReadOnlyList<Track> Tracks, string? Description = null);

public enum SearchKind { Track, Album, Artist }

public interface ICatalogProvider
{
    Task<IReadOnlyList<Album>> SearchAlbumsAsync(SearchRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Artist>> SearchArtistsAsync(SearchRequest request, CancellationToken cancellationToken = default);
    Task<AlbumDetail?> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default);
    Task<Artist?> GetArtistAsync(string artistId, CancellationToken cancellationToken = default);
}
