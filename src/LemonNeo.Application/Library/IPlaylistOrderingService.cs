using LemonNeo.Core.Library;
namespace LemonNeo.Application.Library;

public interface IPlaylistOrderingService
{
    string GetKeyForEnd(IReadOnlyCollection<PlaylistItem> items);
    string GetKeyBetween(PlaylistItem? before, PlaylistItem? after);
    void Normalize(Playlist playlist);
}
