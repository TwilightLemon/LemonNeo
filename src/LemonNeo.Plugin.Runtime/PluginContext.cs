using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.Runtime;

internal sealed class PluginContext(
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    ProviderRegistry registry,
    PlayerRegistry playerRegistry,
    IPluginStorage storage,
    ICredentialVault credentials,
    string cacheDirectory) : IPluginContext
{
    private readonly List<IMusicProvider> _registeredProviders = [];
    private readonly List<IPlayerProvider> _registeredPlayers = [];

    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public IHttpClientFactory HttpClientFactory { get; } = httpClientFactory;
    public IPluginStorage Storage { get; } = storage;
    public ICredentialVault Credentials { get; } = credentials;
    public string CacheDirectory { get; } = cacheDirectory;
    public IReadOnlyList<IMusicProvider> RegisteredProviders => _registeredProviders;
    public IReadOnlyList<IPlayerProvider> RegisteredPlayers => _registeredPlayers;

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
