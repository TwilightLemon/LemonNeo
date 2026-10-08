namespace LemonNeo.Desktop.Services;

public interface IDesktopStorageService
{
    Task ChangeLibraryAsync(string directory, CancellationToken cancellationToken = default);
    Task ChangeCacheAsync(string directory, CancellationToken cancellationToken = default);
}
