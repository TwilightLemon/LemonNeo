using LemonNeo.Abstractions.Playback;
using LemonNeo.Application;
using LemonNeo.Application.Library;
using LemonNeo.Application.Lyrics;
using LemonNeo.Application.Playback;
using LemonNeo.Application.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Hosting.Registration;

internal static class ApplicationRegistration
{
    public static void AddApplication(this IServiceCollection services, HostStorageLocations storage)
    {
        services.AddSingleton<LibraryService>();
        services.AddSingleton<IPlaylistOrderingService, FractionalPlaylistOrderingService>();
        services.AddSingleton<PlaylistService>();
        services.AddSingleton(provider => new FolderImportService(
            provider.GetRequiredService<IPlayerRegistry>(),
            provider.GetRequiredService<PlaylistService>(),
            () => storage.CacheDirectory,
            provider.GetRequiredService<ILogger<FolderImportService>>()));
        services.AddSingleton<ImportService>();
        services.AddSingleton<SearchService>();
        services.AddSingleton<TrackLookupService>();
        services.AddSingleton<MetadataService>();
        services.AddSingleton(provider => new ImageCache(() => storage.CacheDirectory,
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ImageCache)), provider.GetRequiredService<ILogger<ImageCache>>()));
        services.AddSingleton(provider => new LyricsCache(() => storage.CacheDirectory, provider.GetRequiredService<ILogger<LyricsCache>>()));
        services.AddSingleton<LyricsService>();
        services.AddSingleton<IPlaybackQueue, PlaybackQueue>();
        services.AddSingleton(provider => new PlaybackAudioCache(() => storage.CacheDirectory, provider.GetRequiredService<ILogger<PlaybackAudioCache>>()));
        services.AddSingleton<PlaybackSourceDiscoveryService>();
        services.AddSingleton<PlaybackResolver>();
        services.AddSingleton<PlaybackSessionService>();
        services.AddSingleton<IPlaybackInteraction, PlaybackInteractionService>();
    }
}
