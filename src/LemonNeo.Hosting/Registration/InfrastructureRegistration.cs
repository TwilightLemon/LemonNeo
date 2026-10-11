using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using LemonNeo.Infrastructure.Credentials;
using LemonNeo.Infrastructure.Library;
using LemonNeo.Infrastructure.Plugins;
using LemonNeo.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace LemonNeo.Hosting.Registration;

internal static class InfrastructureRegistration
{
    public static void AddInfrastructure(this IServiceCollection services, IAppPathProvider paths,
        HostStorageLocations storage)
    {
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(paths);
        services.AddSingleton<ISettingsEntityStore, JsonSettingsEntityStore>();
        services.AddSingleton<AppSettingsService>();
        services.AddSingleton<IAppSettingsService>(provider => provider.GetRequiredService<AppSettingsService>());
        services.AddSingleton<IPluginDataStoreFactory>(_ =>
            new PluginDataStoreFactory(paths, () => storage.CacheDirectory));
        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<IProviderRegistry>(provider => provider.GetRequiredService<ProviderRegistry>());
        services.AddSingleton<IPlaybackSourceProviderRegistry>(provider =>
            provider.GetRequiredService<ProviderRegistry>());
        services.AddSingleton<PlayerRegistry>();
        services.AddSingleton<IPlayerRegistry>(provider => provider.GetRequiredService<PlayerRegistry>());
        services.AddSingleton<ILibraryRepository>(new JsonLibraryRepository(() => storage.LibraryDirectory));
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<IPluginDiagnostics>(provider => provider.GetRequiredService<PluginLoader>());
    }
}