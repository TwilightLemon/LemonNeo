namespace LemonNeo.Hosting;

/// <summary>
///     The host-owned locations of user-selectable storage. Services read the current
///     value when starting an operation, so changing a location does not recreate the
///     service provider or any plugin.
/// </summary>
public sealed class HostStorageLocations
{
    private string _cacheDirectory;
    private string _libraryDirectory;

    internal HostStorageLocations(string libraryDirectory, string cacheDirectory)
    {
        _libraryDirectory = Path.GetFullPath(libraryDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public string LibraryDirectory => Volatile.Read(ref _libraryDirectory);
    public string CacheDirectory => Volatile.Read(ref _cacheDirectory);
    public event EventHandler? Changed;

    internal void SetLibraryDirectory(string directory)
    {
        var value = Path.GetFullPath(directory);
        if (PathEquals(LibraryDirectory, value)) return;
        Volatile.Write(ref _libraryDirectory, value);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void SetCacheDirectory(string directory)
    {
        var value = Path.GetFullPath(directory);
        if (PathEquals(CacheDirectory, value)) return;
        Volatile.Write(ref _cacheDirectory, value);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool PathEquals(string first, string second)
    {
        return string.Equals(first, second, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }
}