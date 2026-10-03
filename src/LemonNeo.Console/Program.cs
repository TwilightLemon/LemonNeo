using System.Text;
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

namespace LemonNeo.ConsoleApp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        global::System.Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (HighlightingTextWriter.CanUseColor)
        {
            global::System.Console.SetOut(new HighlightingTextWriter(global::System.Console.Out));
        }

        var appPaths = AppPathProvider.CreateDefault();
        var options = ConsoleOptions.Parse(args, appPaths);
        using var cancellation = new CancellationTokenSource();
        global::System.Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var collection = new ServiceCollection();
        collection.AddLogging(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
            }));
        collection.AddHttpClient();
        collection.AddSingleton(options);
        collection.AddSingleton(new StartupCommands(options.StartupFile));
        collection.AddSingleton<IAppPathProvider>(appPaths);
        collection.AddSingleton<IAppConfigurationStore, JsonAppConfigurationStore>();
        collection.AddSingleton<IPluginDataStoreFactory, PluginDataStoreFactory>();
        collection.AddSingleton<ProviderRegistry>();
        collection.AddSingleton<IProviderRegistry>(services => services.GetRequiredService<ProviderRegistry>());
        collection.AddSingleton<IPlaybackSourceProviderRegistry>(
            services => services.GetRequiredService<ProviderRegistry>());
        collection.AddSingleton<IPlayerProvider>(_ => new ConsolePlayerProvider(global::System.Console.Out));
        collection.AddSingleton<PlayerRegistry>();
        collection.AddSingleton<IPlayerRegistry>(services => services.GetRequiredService<PlayerRegistry>());
        collection.AddSingleton<PluginLoader>();
        collection.AddSingleton<ILibraryRepository>(_ => new JsonLibraryRepository(options.LibraryDirectory));
        collection.AddSingleton<IPlaybackStateRepository>(
            _ => new JsonPlaybackStateRepository(options.LibraryDirectory));
        collection.AddSingleton<LibraryService>();
        collection.AddSingleton<IPlaylistOrderingService, FractionalPlaylistOrderingService>();
        collection.AddSingleton<PlaylistService>();
        collection.AddSingleton<SearchService>();
        collection.AddSingleton<TrackLookupService>();
        collection.AddSingleton<MetadataService>();
        collection.AddSingleton(services => new LyricsCache(
            appPaths.CacheDirectory, services.GetRequiredService<ILogger<LyricsCache>>()));
        collection.AddSingleton<LyricsService>();
        collection.AddSingleton<ImportService>();
        collection.AddSingleton<FolderImportService>();
        collection.AddSingleton<IPlaybackQueue, PlaybackQueue>();
        collection.AddSingleton(services => new PlaybackAudioCache(
            appPaths.CacheDirectory, services.GetRequiredService<ILogger<PlaybackAudioCache>>()));
        collection.AddSingleton<PlaybackSourceDiscoveryService>();
        collection.AddSingleton<PlaybackResolver>();
        collection.AddSingleton<PlaybackSessionService>();
        collection.AddSingleton<IPlaybackInteraction, PlaybackInteractionService>();
        collection.AddSingleton<ILibraryMergeService, LibraryMergeService>();
        collection.AddSingleton<IGitBackend, MockGitBackend>();
        collection.AddSingleton<ILibrarySyncProvider, MockGitSyncProvider>();
        collection.AddSingleton<SyncService>();
        collection.AddSingleton<CommandCompletionService>();
        collection.AddSingleton<CommandHistory>();
        collection.AddSingleton<CommandProcessor>();

        await using var services = collection.BuildServiceProvider();
        var output = global::System.Console.Out;
        try
        {
            // Restore playback before Interactive plugins subscribe. The shared
            // session also stays alive until the loader has shut its plugins down.
            var playbackSession = services.GetRequiredService<PlaybackSessionService>();
            await playbackSession.InitializeAsync(cancellation.Token);
            var loader = services.GetRequiredService<PluginLoader>();
            await loader.LoadFromDirectoryAsync(options.PluginDirectory, cancellation.Token);

            var library = services.GetRequiredService<LibraryService>();
            await library.LoadAsync(cancellation.Token);
            var commands = services.GetRequiredService<CommandProcessor>();
            if (await commands.RunStartupAsync(cancellation.Token))
            {
                await commands.RunInteractiveAsync(cancellation.Token);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            output.WriteLine("Operation cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            output.WriteLine($"Fatal error: {exception.Message}");
            return 1;
        }
    }

}

internal sealed record ConsoleOptions(string PluginDirectory, string LibraryDirectory, string StartupFile)
{
    public static ConsoleOptions Parse(string[] args, IAppPathProvider appPaths)
    {
        var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
        var libraryDirectory = appPaths.MusicLibraryDirectory;
        var startupFile = Path.Combine(appPaths.ConfigurationDirectory, "startup.txt");
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--plugins" when index + 1 < args.Length:
                    pluginDirectory = Path.GetFullPath(args[++index]);
                    break;
                case "--library" when index + 1 < args.Length:
                    libraryDirectory = Path.GetFullPath(args[++index]);
                    break;
                case "--startup" when index + 1 < args.Length:
                    startupFile = Path.GetFullPath(args[++index]);
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete option '{args[index]}'.");
            }
        }

        return new ConsoleOptions(pluginDirectory, libraryDirectory, startupFile);
    }
}
