namespace LemonNeo.Abstractions.Storage;

public interface IAppPathProvider
{
    string ConfigurationDirectory { get; }
    string DataDirectory { get; }
    string CacheDirectory { get; }
    string MusicLibraryDirectory { get; }
    string DataProtectionKeysDirectory { get; }
    string GetPackageConfigurationDirectory(string packageId);
    string GetEntityConfigurationFile(string packageId, string entityName);
    string GetPluginCredentialDirectory(string pluginId);
    string GetPluginCacheDirectory(string pluginId);
}
