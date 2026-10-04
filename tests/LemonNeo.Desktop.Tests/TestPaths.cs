using LemonNeo.Configuration;

namespace LemonNeo.Desktop.Tests;

internal sealed class TestPaths : IAppPathProvider, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LemonNeo-ThemeTests", Guid.NewGuid().ToString("N"));
    public string ConfigurationDirectory => Path.Combine(_root, "config");
    public string DataDirectory => Path.Combine(_root, "data");
    public string CacheDirectory => Path.Combine(_root, "cache");
    public string MusicLibraryDirectory => Path.Combine(_root, "library");
    public string AppConfigurationFile => Path.Combine(ConfigurationDirectory, "appsettings.json");
    public string DataProtectionKeysDirectory => Path.Combine(DataDirectory, "keys");
    public string GetPluginConfigurationDirectory(string pluginId) => Path.Combine(ConfigurationDirectory, pluginId);
    public string GetPluginCredentialDirectory(string pluginId) => Path.Combine(DataDirectory, pluginId);
    public string GetPluginCacheDirectory(string pluginId) => Path.Combine(CacheDirectory, pluginId);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
