using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Desktop.Infrastructure;

public sealed record DesktopOptions(string PluginDirectory, string LibraryDirectory, bool EnableMockSync = false)
{
    public static DesktopOptions Parse(string[] args, IAppPathProvider paths)
    {
        var plugins = Path.Combine(AppContext.BaseDirectory, "plugins");
        var library = paths.MusicLibraryDirectory;
        var mockSync = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--plugins" when i + 1 < args.Length: plugins = Path.GetFullPath(args[++i]); break;
                case "--library" when i + 1 < args.Length: library = Path.GetFullPath(args[++i]); break;
                case "--mock-sync": mockSync = true; break;
                default: throw new ArgumentException($"Unknown or incomplete option '{args[i]}'.");
            }
        }
        return new(plugins, library, mockSync);
    }
}

public sealed class DesktopHost(IAppPathProvider paths, DesktopOptions options,
    IEnumerable<IPlayerProvider>? hostPlayers = null, IEnumerable<IMusicProvider>? hostProviders = null)
    : AppHost(paths, new(options.PluginDirectory, options.LibraryDirectory, options.EnableMockSync), services =>
    {
        services.AddSingleton(options);
        services.AddLogging(builder => builder.AddDebug());
        services.AddSingleton<ArtworkService>();
        foreach (var provider in hostProviders ?? []) services.AddSingleton(provider);
        foreach (var player in hostPlayers ?? []) services.AddSingleton(player);
    });
