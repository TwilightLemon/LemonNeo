using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Application.Library;
using LemonNeo.Application.Playback;
using LemonNeo.Hosting.Registration;
using LemonNeo.Infrastructure.Paths;
using LemonNeo.Infrastructure.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace LemonNeo.Hosting;

/// <summary>The shared Desktop/Console owner of configuration, plugins, playback and shutdown.</summary>
public class AppHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly HostOptions _options;
    private readonly ServiceProvider _services;
    private readonly object _stateGate = new();
    private Task? _disposeTask;
    private bool _initializationStarted;
    private bool _initialized;
    private bool _libraryLoaded;
    private bool _pluginsLoaded;

    public AppHost(IAppPathProvider paths, HostOptions options, Action<IServiceCollection>? configure = null)
    {
        Paths = paths;
        _options = options;
        StorageLocations = new HostStorageLocations(options.LibraryDirectory, options.CacheDirectory);
        var services = new ServiceCollection();
        services.AddInfrastructure(paths, StorageLocations);
        services.AddApplication(StorageLocations);
        configure?.Invoke(services);
        _services = services.BuildServiceProvider();
    }

    public IPluginDiagnostics Plugins => Get<IPluginDiagnostics>();
    public IAppPathProvider Paths { get; }
    public HostStorageLocations StorageLocations { get; }
    public IAppSettingsService Settings => Get<IAppSettingsService>();

    public ValueTask DisposeAsync()
    {
        return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    public static IAppPathProvider CreateDefaultPaths()
    {
        return AppPathProvider.CreateDefault();
    }

    public T Get<T>() where T : notnull
    {
        return _services.GetRequiredService<T>();
    }

    /// <summary>Sets host storage after bootstrap configuration is loaded and before runtime initialization begins.</summary>
    public void ConfigureInitialStorage(string libraryDirectory, string cacheDirectory)
    {
        lock (_stateGate)
        {
            if (_initializationStarted)
                throw new InvalidOperationException("Initial storage must be configured before host initialization.");
            StorageLocations.SetLibraryDirectory(libraryDirectory);
            StorageLocations.SetCacheDirectory(cacheDirectory);
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            lock (_stateGate)
            {
                _initializationStarted = true;
            }

            await Settings.LoadAsync(cancellationToken);
            var playbackTask = Get<PlaybackSessionService>().InitializeAsync(cancellationToken);
            var libraryTask = InitializeLibraryAsync(cancellationToken);
            try
            {
                await Task.WhenAll(playbackTask, libraryTask);
            }
            catch
            {
                // Preserve the relevant failure when both independent branches were active.
                await playbackTask;
                await libraryTask;
                throw;
            }

            if (!_pluginsLoaded)
            {
                await Get<PluginLoader>().LoadFromDirectoryAsync(_options.PluginDirectory, cancellationToken);
                _pluginsLoaded = true;
            }

            Settings.Start();
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private async Task InitializeLibraryAsync(CancellationToken cancellationToken)
    {
        if (_libraryLoaded) return;
        var library = Get<LibraryService>();
        await library.LoadAsync(cancellationToken);
        await library.EnsureCreatedAsync(cancellationToken);
        _libraryLoaded = true;
    }

    /// <summary>Switches the backing Library while keeping the host and all plugins alive.</summary>
    public async Task ChangeLibraryDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var next = Path.GetFullPath(directory);
        var previous = StorageLocations.LibraryDirectory;
        if (PathEquals(previous, next)) return;

        var library = Get<LibraryService>();
        await library.SaveAsync(cancellationToken);
        StorageLocations.SetLibraryDirectory(next);
        try
        {
            await library.LoadAsync(cancellationToken);
            await library.EnsureCreatedAsync(cancellationToken);
            _libraryLoaded = true;
        }
        catch (Exception changeError)
        {
            StorageLocations.SetLibraryDirectory(previous);
            try
            {
                await library.LoadAsync(CancellationToken.None);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException("Could not change the music library or restore the previous library.",
                    changeError, restoreError);
            }

            throw;
        }
    }

    /// <summary>Changes where subsequent cache operations persist their values.</summary>
    public void ChangeCacheDirectory(string directory)
    {
        StorageLocations.SetCacheDirectory(directory);
    }

    private static bool PathEquals(string first, string second)
    {
        return string.Equals(first, second, OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private async Task DisposeCoreAsync()
    {
        List<Exception> errors = [];

        async Task Finish(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }

        await Finish(Settings.StopAsync);
        await Finish(Get<PlaybackSessionService>().PrepareForShutdownAsync);
        await Finish(Get<PluginLoader>().ShutdownAsync);
        if (_libraryLoaded) await Finish(() => Get<LibraryService>().SaveAsync());
        await Finish(() => Settings.SaveAsync());
        await Finish(() => _services.DisposeAsync().AsTask());
        _initializeGate.Dispose();
        if (errors.Count > 0) throw new AggregateException("Host shutdown failed.", errors);
    }
}