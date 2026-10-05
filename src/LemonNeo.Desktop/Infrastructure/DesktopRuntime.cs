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

namespace LemonNeo.Desktop.Infrastructure;

public sealed record DesktopOptions(string PluginDirectory, string LibraryDirectory, string? StartupFile = null)
{
    public static DesktopOptions Parse(string[] args, IAppPathProvider paths)
    {
        var plugins = Path.Combine(AppContext.BaseDirectory, "plugins");
        var library = paths.MusicLibraryDirectory;
        var startup = Path.Combine(paths.ConfigurationDirectory, "startup.txt");
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--plugins" when i + 1 < args.Length: plugins = Path.GetFullPath(args[++i]); break;
                case "--library" when i + 1 < args.Length: library = Path.GetFullPath(args[++i]); break;
                case "--startup" when i + 1 < args.Length: startup = Path.GetFullPath(args[++i]); break;
                default: throw new ArgumentException($"Unknown or incomplete option '{args[i]}'.");
            }
        }
        return new(plugins, library, startup);
    }
}

public sealed class DesktopRuntime : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly DesktopOptions _options;
    private bool _pluginsLoaded;

    public DesktopRuntime(IAppPathProvider paths, DesktopOptions options, IEnumerable<IPlayerProvider>? hostPlayers = null,
        IEnumerable<IMusicProvider>? hostProviders = null)
    {
        Paths = paths;
        _options = options;
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton(new StartupCommands(options.StartupFile ?? Path.Combine(paths.ConfigurationDirectory, "startup.txt")));
        services.AddLogging(builder => builder.AddDebug());
        services.AddHttpClient();
        services.AddSingleton(p => new ImageCache(paths.CacheDirectory,
            p.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ImageCache)), p.GetRequiredService<ILogger<ImageCache>>()));
        services.AddSingleton<ArtworkService>();
        services.AddSingleton(paths);
        services.AddSingleton<IAppConfigurationStore, JsonAppConfigurationStore>();
        services.AddSingleton<IPluginDataStoreFactory, PluginDataStoreFactory>();
        foreach (var provider in hostProviders ?? []) services.AddSingleton(provider);
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<IProviderRegistry>(p => p.GetRequiredService<ProviderRegistry>());
        services.AddSingleton<IPlaybackSourceProviderRegistry>(p => p.GetRequiredService<ProviderRegistry>());
        foreach (var player in hostPlayers ?? []) services.AddSingleton(player);
        services.AddSingleton<PlayerRegistry>();
        services.AddSingleton<IPlayerRegistry>(p => p.GetRequiredService<PlayerRegistry>());
        services.AddSingleton<ILibraryRepository>(new JsonLibraryRepository(options.LibraryDirectory));
        services.AddSingleton<IPlaybackStateRepository>(new JsonPlaybackStateRepository(options.LibraryDirectory));
        services.AddSingleton<LibraryService>();
        services.AddSingleton<IPlaylistOrderingService, FractionalPlaylistOrderingService>();
        services.AddSingleton<PlaylistService>();
        services.AddSingleton<FolderImportService>();
        services.AddSingleton<ImportService>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton(p => new LyricsCache(paths.CacheDirectory, p.GetRequiredService<ILogger<LyricsCache>>()));
        services.AddSingleton<LyricsService>();
        services.AddSingleton<IPlaybackQueue, PlaybackQueue>();
        services.AddSingleton(p => new PlaybackAudioCache(paths.CacheDirectory, p.GetRequiredService<ILogger<PlaybackAudioCache>>()));
        services.AddSingleton<PlaybackSourceDiscoveryService>();
        services.AddSingleton<PlaybackResolver>();
        services.AddSingleton<PlaybackSessionService>();
        services.AddSingleton<IPlaybackInteraction, PlaybackInteractionService>();
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<ILibraryMergeService, LibraryMergeService>();
        services.AddSingleton<IGitBackend, MockGitBackend>();
        services.AddSingleton<ILibrarySyncProvider, MockGitSyncProvider>();
        services.AddSingleton<SyncService>();
        _services = services.BuildServiceProvider();
    }

    public IAppPathProvider Paths { get; }
    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Resolve the loader last so DI shuts down Interactive plugins before the session.
        await Get<PlaybackSessionService>().InitializeAsync(cancellationToken);
        if (!_pluginsLoaded)
        {
            var loader = Get<PluginLoader>();
            try
            {
                await loader.LoadFromDirectoryAsync(_options.PluginDirectory, cancellationToken);
                _pluginsLoaded = true;
            }
            catch (OperationCanceledException)
            {
                await loader.DisposeAsync();
                throw;
            }
        }
        await Get<LibraryService>().LoadAsync(cancellationToken);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();
}
