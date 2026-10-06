using LemonNeo.Core.Library;
namespace LemonNeo.Abstractions.Storage;

public interface ILibraryRepository
{
    Task<MusicLibrary> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(MusicLibrary library, CancellationToken cancellationToken = default);
}
