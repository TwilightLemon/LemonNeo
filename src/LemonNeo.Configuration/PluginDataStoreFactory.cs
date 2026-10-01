using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using LemonNeo.Plugin.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace LemonNeo.Configuration;

public sealed class PluginDataStoreFactory : IPluginDataStoreFactory
{
    private readonly IAppPathProvider _paths;
    private readonly IDataProtectionProvider _dataProtection;

    public PluginDataStoreFactory(IAppPathProvider paths)
    {
        _paths = paths;
        SecureDirectory(paths.DataProtectionKeysDirectory);
        _dataProtection = DataProtectionProvider.Create(
            new DirectoryInfo(paths.DataProtectionKeysDirectory),
            builder => builder.SetApplicationName("LemonNeo"));
    }

    public IPluginStorage CreateStorage(string pluginId)
    {
        var directory = _paths.GetPluginConfigurationDirectory(pluginId);
        return new JsonPluginStorage(Path.Combine(directory, "settings.json"));
    }

    public ICredentialVault CreateCredentialVault(string pluginId)
    {
        var directory = _paths.GetPluginCredentialDirectory(pluginId);
        SecureDirectory(directory);
        return new ProtectedFileCredentialVault(
            Path.Combine(directory, "credentials.json"),
            _dataProtection.CreateProtector("LemonNeo.PluginCredentials.v1", pluginId));
    }

    public string GetCacheDirectory(string pluginId)
    {
        var directory = _paths.GetPluginCacheDirectory(pluginId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

internal sealed class ProtectedFileCredentialVault(
    string path,
    IDataProtector protector) : ICredentialVault
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.GetFullPath(path);

    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAsync(cancellationToken);
            return values.TryGetValue(key, out var protectedValue)
                ? protector.Unprotect(protectedValue)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetSecretAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAsync(cancellationToken);
            values[key] = protector.Protect(value);
            await WriteAsync(values, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAsync(cancellationToken);
            if (values.Remove(key))
            {
                await WriteAsync(values, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task WriteAsync(
        Dictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, values, JsonOptions, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        }

        File.Move(temporaryPath, _path, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static void ValidateKey(string key) => ArgumentException.ThrowIfNullOrWhiteSpace(key);
}
