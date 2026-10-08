using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using LemonNeo.Desktop.Config;
using LemonNeo.Desktop.Services;

namespace LemonNeo.Desktop;

public sealed class DesktopHost(IAppPathProvider paths, string libraryDirectory, string cacheDirectory,
    IDesktopStorageService storageService,
    IEnumerable<IPlayerProvider>? hostPlayers = null, IEnumerable<IMusicProvider>? hostProviders = null)
    : AppHost(paths, new(paths.PluginDirectory, libraryDirectory, cacheDirectory), services =>
    {
        services.AddSingleton(storageService);
        services.AddLogging(builder => builder.AddDebug());
        services.AddSingleton<ArtworkService>();
        foreach (var provider in hostProviders ?? []) services.AddSingleton(provider);
        foreach (var player in hostPlayers ?? []) services.AddSingleton(player);
    });
