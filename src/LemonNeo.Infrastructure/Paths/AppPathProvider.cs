using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;

namespace LemonNeo.Infrastructure.Paths;

internal sealed class AppPathProvider : IAppPathProvider
{
    private const string AppName = "LemonNeo";

    private AppPathProvider(
        string configurationDirectory,
        string dataDirectory,
        string cacheDirectory,
        string musicLibraryDirectory)
    {
        ConfigurationDirectory = Path.GetFullPath(configurationDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        CacheDirectory = Path.GetFullPath(cacheDirectory);
        MusicLibraryDirectory = Path.GetFullPath(musicLibraryDirectory);
        DataProtectionKeysDirectory = Path.Combine(DataDirectory, "Keys");
    }

    public string ConfigurationDirectory { get; }
    public string DataDirectory { get; }
    public string CacheDirectory { get; }
    public string MusicLibraryDirectory { get; }
    public string PluginDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "plugins");
    public string DataProtectionKeysDirectory { get; }

    public string GetPackageConfigurationDirectory(string packageId)
    {
        PackageNames.Validate(packageId);
        return Path.Combine(ConfigurationDirectory, packageId);
    }

    public string GetEntityConfigurationFile(string packageId, string entityName)
    {
        if (!PackageNames.IsIdentifier(entityName))
            throw new ArgumentException("Invalid configuration entity name.", nameof(entityName));
        return Path.Combine(GetPackageConfigurationDirectory(packageId), entityName + ".json");
    }

    public string GetPluginCredentialDirectory(string pluginId)
    {
        return Path.Combine(DataDirectory, "Credentials", SafeSegment(pluginId));
    }

    public static AppPathProvider CreateDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("The current user's home directory could not be determined.");

        string configurationRoot;
        string dataRoot;
        string cacheRoot;
        if (OperatingSystem.IsWindows())
        {
            configurationRoot = RequiredSpecialFolder(Environment.SpecialFolder.ApplicationData);
            dataRoot = RequiredSpecialFolder(Environment.SpecialFolder.LocalApplicationData);
            cacheRoot = dataRoot;
        }
        else if (OperatingSystem.IsMacOS())
        {
            configurationRoot = Path.Combine(home, "Library", "Application Support");
            dataRoot = configurationRoot;
            cacheRoot = Path.Combine(home, "Library", "Caches");
        }
        else
        {
            configurationRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                                ?? Path.Combine(home, ".config");
            dataRoot = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                       ?? Path.Combine(home, ".local", "share");
            cacheRoot = Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
                        ?? Path.Combine(home, ".cache");
        }

        var configurationDirectory = Environment.GetEnvironmentVariable("LEMONNEO_CONFIG_HOME")
                                     ?? Path.Combine(configurationRoot, AppName);
        var dataDirectory = Environment.GetEnvironmentVariable("LEMONNEO_DATA_HOME")
                            ?? Path.Combine(dataRoot, AppName);
        var cacheDirectory = Environment.GetEnvironmentVariable("LEMONNEO_CACHE_HOME")
                             ?? Path.Combine(cacheRoot, AppName, "Cache");
        var libraryDirectory = Environment.GetEnvironmentVariable("LEMONNEO_LIBRARY_HOME")
                               ?? Path.Combine(dataDirectory, "Library");
        return new AppPathProvider(
            configurationDirectory,
            dataDirectory,
            cacheDirectory,
            libraryDirectory);
    }

    private static string RequiredSpecialFolder(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder);
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException($"Special folder '{folder}' could not be determined.")
            : path;
    }

    private static string SafeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("The identifier is not a safe path segment.", nameof(value));

        return value;
    }
}