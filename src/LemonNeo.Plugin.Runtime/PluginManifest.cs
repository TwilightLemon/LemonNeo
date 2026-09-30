using System.Text.Json.Serialization;

namespace LemonNeo.Plugin.Runtime;

public sealed record PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string EntryAssembly { get; init; }
    public required string MinimumHostVersion { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

public sealed record PluginLoadFailure(string ManifestPath, string Error);

public sealed record PluginLoadReport(
    IReadOnlyList<LoadedPlugin> Loaded,
    IReadOnlyList<PluginLoadFailure> Failures);
