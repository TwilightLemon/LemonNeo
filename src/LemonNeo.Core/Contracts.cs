namespace LemonNeo.Core;

public interface ILibraryRepository
{
    Task<MusicLibrary> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(MusicLibrary library, CancellationToken cancellationToken = default);
}

public interface IPlaylistOrderingService
{
    string GetKeyForEnd(IReadOnlyCollection<PlaylistItem> items);
    string GetKeyBetween(PlaylistItem? before, PlaylistItem? after);
    void Normalize(Playlist playlist);
}

public abstract class LemonNeoException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class LibraryException(string message, Exception? innerException = null)
    : LemonNeoException(message, innerException);

public sealed class ProviderException(string message, Exception? innerException = null)
    : LemonNeoException(message, innerException);

public sealed class PlaybackResolutionException(string message, Exception? innerException = null)
    : LemonNeoException(message, innerException);

public sealed class SyncException(string message, Exception? innerException = null)
    : LemonNeoException(message, innerException);
