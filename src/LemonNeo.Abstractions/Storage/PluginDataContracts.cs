using Microsoft.Extensions.Logging;

namespace LemonNeo.Abstractions.Storage;

public interface ICredentialVault
{
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default);
}

public interface IPluginDataStoreFactory
{
    ICredentialVault CreateCredentialVault(string pluginId);
    string GetCacheDirectory(string pluginId);
}
