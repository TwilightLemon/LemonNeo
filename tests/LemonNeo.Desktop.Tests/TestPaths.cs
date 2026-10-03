using LemonNeo.Configuration;

namespace LemonNeo.Desktop.Tests;

internal sealed class TestPaths : IAppPathProvider, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LemonNeo.Desktop.Tests", Guid.NewGuid().ToString("N"));
    public string ConfigurationDirectory => Path.Combine(_root, "Config");
    public string DataDirectory => Path.Combine(_root, "Data");
    public string CacheDirectory => Path.Combine(_root, "Cache");
    public string MusicLibraryDirectory => Path.Combine(_root, "Library");
    public string AppConfigurationFile => Path.Combine(ConfigurationDirectory, "appsettings.json");
    public string DataProtectionKeysDirectory => Path.Combine(DataDirectory, "Keys");
    public string GetPluginConfigurationDirectory(string pluginId) => Path.Combine(ConfigurationDirectory, "Plugins", pluginId);
    public string GetPluginCredentialDirectory(string pluginId) => Path.Combine(DataDirectory, "Credentials", pluginId);
    public string GetPluginCacheDirectory(string pluginId) => Path.Combine(CacheDirectory, "Plugins", pluginId);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
