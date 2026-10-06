namespace LemonNeo.Abstractions.Plugins;

public sealed record PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string EntryAssembly { get; init; }
    public required string MinimumHostVersion { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public IReadOnlyList<string> Platforms { get; init; } = [];
}

public sealed record PluginLoadFailure(string ManifestPath, string Error);

public sealed record PluginLoadReport(
    IReadOnlyList<PluginInfo> Loaded,
    IReadOnlyList<PluginLoadFailure> Failures);

/// <summary>Host-visible metadata without plugin instances or load contexts.</summary>
public sealed record PluginInfo(
    PluginManifest Manifest,
    bool IsInteractive,
    IReadOnlyList<string> ProviderIds,
    IReadOnlyList<string> PlayerIds);

public interface IPluginDiagnostics
{
    IReadOnlyList<PluginInfo> Loaded { get; }
    PluginLoadReport LastReport { get; }
}
