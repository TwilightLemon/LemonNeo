using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Configuration;

public interface IAppConfigurationStore
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class JsonAppConfigurationStore(IAppPathProvider paths) : IAppConfigurationStore
{
    private readonly JsonKeyValueStore _store = new(paths.AppConfigurationFile);

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) =>
        _store.GetAsync<T>(key, cancellationToken);

    public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) =>
        _store.SetAsync(key, value, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(key, cancellationToken);
}

internal sealed class JsonPluginStorage(string path) : IPluginStorage
{
    private readonly JsonKeyValueStore _store = new(path);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        _store.GetAsync<string>(key, cancellationToken);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        _store.SetAsync(key, value, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        _store.DeleteAsync(key, cancellationToken);
}

internal sealed class JsonKeyValueStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.GetFullPath(path);

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAsync(cancellationToken);
            return values.TryGetValue(key, out var value)
                ? value.Deserialize<T>(JsonOptions)
                : default;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = await ReadAsync(cancellationToken);
            values[key] = JsonSerializer.SerializeToElement(value, JsonOptions);
            await WriteAsync(values, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
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

    private async Task<Dictionary<string, JsonElement>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
    }

    private async Task WriteAsync(
        Dictionary<string, JsonElement> values,
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
    }

    private static void ValidateKey(string key) => ArgumentException.ThrowIfNullOrWhiteSpace(key);
}
