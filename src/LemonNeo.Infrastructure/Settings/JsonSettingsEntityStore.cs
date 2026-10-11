using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;

namespace LemonNeo.Infrastructure.Settings;

internal sealed class JsonSettingsEntityStore(IAppPathProvider paths) : ISettingsEntityStore
{
    public async Task<T?> LoadAsync<T>(string packageId, string entityName,
        CancellationToken cancellationToken = default) where T : class
    {
        var path = paths.GetEntityConfigurationFile(packageId, entityName);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, CreateOptions(), cancellationToken)
               ?? throw new JsonException($"Configuration entity is empty: {path}");
    }

    public async Task SaveAsync<T>(string packageId, string entityName, T value,
        CancellationToken cancellationToken = default) where T : class
    {
        var path = paths.GetEntityConfigurationFile(packageId, entityName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                         FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(stream, value, CreateOptions(), cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        }

        File.Move(temporaryPath, path, true);
    }

    // Keep serializer metadata local to each operation so collectible plugin types are not cached forever.
    private static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
    }
}