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
        collection.AddSingleton<IAppPathProvider>(appPaths);
        collection.AddSingleton<IAppConfigurationStore, JsonAppConfigurationStore>();
        collection.AddSingleton<IPluginDataStoreFactory, PluginDataStoreFactory>();
        collection.AddSingleton<ProviderRegistry>();
        collection.AddSingleton<IProviderRegistry>(services => services.GetRequiredService<ProviderRegistry>());
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
        collection.AddSingleton<LyricsService>();
        collection.AddSingleton<ImportService>();
        collection.AddSingleton<FolderImportService>();
        collection.AddSingleton<IPlaybackQueue, PlaybackQueue>();
        collection.AddSingleton<PlaybackSourceDiscoveryService>();
        collection.AddSingleton<PlaybackResolver>();
        collection.AddSingleton<PlaybackSessionService>();
        collection.AddSingleton<ILibraryMergeService, LibraryMergeService>();
        collection.AddSingleton<IGitBackend, MockGitBackend>();
        collection.AddSingleton<ILibrarySyncProvider, MockGitSyncProvider>();
        collection.AddSingleton<SyncService>();
        collection.AddSingleton<CommandProcessor>();

        await using var services = collection.BuildServiceProvider();
        var output = global::System.Console.Out;
        output.WriteLine("Lemon Neo Prototype");
        output.WriteLine("===================");
        output.WriteLine();
        output.WriteLine("Loading plugins...");

        try
        {
            var loader = services.GetRequiredService<PluginLoader>();
            var report = await loader.LoadFromDirectoryAsync(options.PluginDirectory, cancellation.Token);
            PrintPluginReport(report, output);

            var library = services.GetRequiredService<LibraryService>();
            await library.LoadAsync(cancellation.Token);
            var playbackSession = services.GetRequiredService<PlaybackSessionService>();
            await playbackSession.InitializeAsync(cancellation.Token);
            output.WriteLine();
            output.WriteLine("Library loaded.");
            output.WriteLine($"{library.Current.Playlists.Count} playlists.");
            var trackCount = library.Current.Playlists
                .SelectMany(playlist => playlist.Items)
                .Select(item => item.Track.Id)
                .Distinct()
                .Count();
            output.WriteLine($"{trackCount} playlist tracks.");
            output.WriteLine();

            var commands = services.GetRequiredService<CommandProcessor>();
            if (options.Demo)
            {
                await commands.RunDemoAsync(cancellation.Token);
                return 0;
            }

            await commands.RunInteractiveAsync(cancellation.Token);
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

    private static void PrintPluginReport(PluginLoadReport report, TextWriter output)
    {
        foreach (var plugin in report.Loaded)
        {
            output.WriteLine();
            output.WriteLine("[Plugin]");
            output.WriteLine($"{plugin.Manifest.Name} {plugin.Manifest.Version}");
            foreach (var provider in plugin.Providers)
            {
                output.WriteLine($"Provider: {provider.Id}");
                output.WriteLine();
                output.WriteLine("Capabilities:");
                foreach (var capability in CommandProcessor.GetCapabilities(provider))
                {
                    output.WriteLine(capability);
                }
            }

            foreach (var player in plugin.Players)
            {
                output.WriteLine($"Player: {player.Id}");
            }
        }

        foreach (var failure in report.Failures)
        {
            output.WriteLine($"Plugin load failed ({failure.ManifestPath}): {failure.Error}");
        }
    }
}

internal sealed record ConsoleOptions(string PluginDirectory, string LibraryDirectory, bool Demo)
{
    public static ConsoleOptions Parse(string[] args, IAppPathProvider appPaths)
    {
        var pluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
        var libraryDirectory = appPaths.MusicLibraryDirectory;
        var demo = false;

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
                case "--demo":
                    demo = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete option '{args[index]}'.");
            }
        }

        return new ConsoleOptions(pluginDirectory, libraryDirectory, demo);
    }
}
