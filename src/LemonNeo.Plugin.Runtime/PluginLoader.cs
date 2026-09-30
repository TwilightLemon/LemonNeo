using System.Reflection;
using System.Text.Json;
using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.Runtime;

public sealed class LoadedPlugin : IAsyncDisposable
{
    private readonly PluginLoadContext _loadContext;
    private readonly ProviderRegistry _registry;
    private readonly PlayerRegistry _playerRegistry;
    private bool _disposed;

    internal LoadedPlugin(
        PluginManifest manifest,
        ILemonPlugin instance,
        IReadOnlyList<IMusicProvider> providers,
        IReadOnlyList<IPlayerProvider> players,
        PluginLoadContext loadContext,
        ProviderRegistry registry,
        PlayerRegistry playerRegistry)
    {
        Manifest = manifest;
        Instance = instance;
        Providers = providers;
        Players = players;
        _loadContext = loadContext;
        _registry = registry;
        _playerRegistry = playerRegistry;
    }

    public PluginManifest Manifest { get; }
    public ILemonPlugin Instance { get; }
    public IReadOnlyList<IMusicProvider> Providers { get; }
    public IReadOnlyList<IPlayerProvider> Players { get; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await Instance.ShutdownAsync(CancellationToken.None);
        }
        finally
        {
            _registry.Remove(Providers);
            _playerRegistry.Remove(Players);
            _loadContext.Unload();
        }
    }
}

public sealed class PluginLoader(
    ProviderRegistry registry,
    PlayerRegistry playerRegistry,
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<PluginLoader> logger) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly List<LoadedPlugin> _loaded = [];

    public IReadOnlyList<LoadedPlugin> Loaded => _loaded;

    public async Task<PluginLoadReport> LoadFromDirectoryAsync(
        string rootDirectory,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<PluginLoadFailure>();
        if (!Directory.Exists(rootDirectory))
        {
            Directory.CreateDirectory(rootDirectory);
            return new PluginLoadReport([], []);
        }

        var manifests = Directory
            .EnumerateFiles(rootDirectory, "plugin.json", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var plugin = await LoadOneAsync(manifestPath, cancellationToken);
                _loaded.Add(plugin);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Failed to load plugin manifest {ManifestPath}", manifestPath);
                failures.Add(new PluginLoadFailure(manifestPath, exception.Message));
            }
        }

        return new PluginLoadReport(_loaded.ToArray(), failures);
    }

    private async Task<LoadedPlugin> LoadOneAsync(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(
            stream,
            JsonOptions,
            cancellationToken) ?? throw new PluginLoadException($"Invalid manifest: {manifestPath}");

        ValidateManifest(manifest, manifestPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var assemblyPath = Path.GetFullPath(Path.Combine(directory, manifest.EntryAssembly));
        if (!assemblyPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(assemblyPath))
        {
            throw new PluginLoadException($"Entry assembly is missing or outside its plugin directory: {manifest.EntryAssembly}");
        }

        var loadContext = new PluginLoadContext(assemblyPath);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var pluginTypes = assembly.GetExportedTypes()
                .Where(type => typeof(ILemonPlugin).IsAssignableFrom(type) &&
                               type is { IsAbstract: false, IsInterface: false })
                .ToArray();

            if (pluginTypes.Length != 1)
            {
                throw new PluginLoadException(
                    $"Plugin '{manifest.Id}' must export exactly one ILemonPlugin implementation; found {pluginTypes.Length}.");
            }

            var instance = Activator.CreateInstance(pluginTypes[0]) as ILemonPlugin
                ?? throw new PluginLoadException($"Could not create plugin '{manifest.Id}'. A public parameterless constructor is required.");
            if (!string.Equals(instance.Descriptor.Id, manifest.Id, StringComparison.Ordinal))
            {
                throw new PluginLoadException(
                    $"Manifest id '{manifest.Id}' does not match descriptor id '{instance.Descriptor.Id}'.");
            }

            var context = new PluginContext(loggerFactory, httpClientFactory, registry, playerRegistry);
            try
            {
                await instance.InitializeAsync(context, cancellationToken);
            }
            catch
            {
                registry.Remove(context.RegisteredProviders);
                playerRegistry.Remove(context.RegisteredPlayers);
                try
                {
                    await instance.ShutdownAsync(CancellationToken.None);
                }
                catch (Exception shutdownException)
                {
                    logger.LogWarning(
                        shutdownException,
                        "Plugin {PluginId} also failed while rolling back initialization.",
                        manifest.Id);
                }

                throw;
            }

            return new LoadedPlugin(
                manifest,
                instance,
                context.RegisteredProviders,
                context.RegisteredPlayers,
                loadContext,
                registry,
                playerRegistry);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static void ValidateManifest(PluginManifest manifest, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) ||
            string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new PluginLoadException($"Manifest has missing required fields: {manifestPath}");
        }

        if (!Version.TryParse(manifest.Version, out _))
        {
            throw new PluginLoadException($"Plugin version '{manifest.Version}' is invalid.");
        }

        if (!Version.TryParse(manifest.MinimumHostVersion, out var minimumHostVersion) ||
            minimumHostVersion > new Version(0, 1, 0))
        {
            throw new PluginLoadException(
                $"Plugin '{manifest.Id}' requires unsupported host version '{manifest.MinimumHostVersion}'.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        for (var index = _loaded.Count - 1; index >= 0; index--)
        {
            await _loaded[index].DisposeAsync();
        }

        _loaded.Clear();
    }
}
