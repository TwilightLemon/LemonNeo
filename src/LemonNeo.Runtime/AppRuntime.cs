using LemonNeo.Application;
using LemonNeo.Configuration;
using LemonNeo.Core;
using LemonNeo.Persistence;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;
using LemonNeo.Plugin.Runtime;
using LemonNeo.Sync.Abstractions;
using LemonNeo.Sync.MockGit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Runtime;

public sealed record RuntimeOptions(string PluginDirectory, string LibraryDirectory);

/// <summary>The shared Desktop/Console owner of configuration, plugins, playback and shutdown.</summary>
public class AppRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly RuntimeOptions _options;
    private bool _pluginsLoaded;
    private bool _libraryLoaded;
    private Task? _disposeTask;

    public AppRuntime(IAppPathProvider paths, RuntimeOptions options, Action<IServiceCollection>? configure = null)
    {
        Paths = paths;
        _options = options;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(paths);
        services.AddSingleton<ISettingsEntityStore, JsonSettingsEntityStore>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<IAppSettingsService>(provider => provider.GetRequiredService<AppSettingsService>());
        services.AddSingleton<IPluginDataStoreFactory, PluginDataStoreFactory>();
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<IProviderRegistry>(provider => provider.GetRequiredService<ProviderRegistry>());
        services.AddSingleton<IPlaybackSourceProviderRegistry>(provider => provider.GetRequiredService<ProviderRegistry>());
        services.AddSingleton<PlayerRegistry>();
        services.AddSingleton<IPlayerRegistry>(provider => provider.GetRequiredService<PlayerRegistry>());
        services.AddSingleton<ILibraryRepository>(new JsonLibraryRepository(options.LibraryDirectory));
        services.AddSingleton<LibraryService>();
        services.AddSingleton<IPlaylistOrderingService, FractionalPlaylistOrderingService>();
        services.AddSingleton<PlaylistService>();
        services.AddSingleton<FolderImportService>();
        services.AddSingleton<ImportService>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<TrackLookupService>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton(provider => new ImageCache(paths.CacheDirectory,
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ImageCache)), provider.GetRequiredService<ILogger<ImageCache>>()));
        services.AddSingleton(provider => new LyricsCache(paths.CacheDirectory, provider.GetRequiredService<ILogger<LyricsCache>>()));
        services.AddSingleton<LyricsService>();
        services.AddSingleton<IPlaybackQueue, PlaybackQueue>();
        services.AddSingleton(provider => new PlaybackAudioCache(paths.CacheDirectory, provider.GetRequiredService<ILogger<PlaybackAudioCache>>()));
        services.AddSingleton<PlaybackSourceDiscoveryService>();
        services.AddSingleton<PlaybackResolver>();
        services.AddSingleton<PlaybackSessionService>();
        services.AddSingleton<IPlaybackInteraction, PlaybackInteractionService>();
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<ILibraryMergeService, LibraryMergeService>();
        services.AddSingleton<IGitBackend, MockGitBackend>();
        services.AddSingleton<ILibrarySyncProvider, MockGitSyncProvider>();
        services.AddSingleton<SyncService>();
        configure?.Invoke(services);
        _services = services.BuildServiceProvider();
    }

    public IAppPathProvider Paths { get; }
    public IAppSettingsService Settings => Get<IAppSettingsService>();
    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Settings.LoadAsync(cancellationToken);
        await Get<PlaybackSessionService>().InitializeAsync(cancellationToken);
        if (!_pluginsLoaded)
        {
            await Get<PluginLoader>().LoadFromDirectoryAsync(_options.PluginDirectory, cancellationToken);
            _pluginsLoaded = true;
        }
        await Get<LibraryService>().LoadAsync(cancellationToken);
        _libraryLoaded = true;
        Settings.Start();
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        List<Exception> errors = [];
        async Task Finish(Func<Task> action)
        {
            try { await action(); }
            catch (Exception exception) { errors.Add(exception); }
        }
        await Finish(Settings.StopAsync);
        await Finish(Get<PlaybackSessionService>().PrepareForShutdownAsync);
        await Finish(Get<PluginLoader>().ShutdownAsync);
        if (_libraryLoaded) await Finish(() => Get<LibraryService>().SaveAsync());
        await Finish(() => Settings.SaveAsync());
        await Finish(() => _services.DisposeAsync().AsTask());
        if (errors.Count > 0) throw new AggregateException("Runtime shutdown failed.", errors);
    }
}
