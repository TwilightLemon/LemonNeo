using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Infrastructure.Plugins;

internal sealed class PluginContext(
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    ProviderRegistry registry,
    PlayerRegistry playerRegistry,
    ISettingsScope settings,
    ICredentialVault credentials,
    Func<string> getCacheDirectory,
    IPlaybackInteraction? playback) : IPluginContext
{
    private readonly List<IPlayerProvider> _registeredPlayers = [];
    private readonly List<IMusicProvider> _registeredProviders = [];
    public IReadOnlyList<IMusicProvider> RegisteredProviders => _registeredProviders;
    public IReadOnlyList<IPlayerProvider> RegisteredPlayers => _registeredPlayers;

    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public IHttpClientFactory HttpClientFactory { get; } = httpClientFactory;
    public ISettingsScope Settings { get; } = settings;
    public ICredentialVault Credentials { get; } = credentials;
    public string CacheDirectory => getCacheDirectory();

    public IPlaybackInteraction Playback =>
        playback ?? throw new NotSupportedException("This host does not expose playback interaction.");

    public void RegisterProvider(IMusicProvider provider)
    {
        registry.Register(provider);
        _registeredProviders.Add(provider);
    }

    public void RegisterPlayer(IPlayerProvider player)
    {
        playerRegistry.Register(player);
        _registeredPlayers.Add(player);
    }
}