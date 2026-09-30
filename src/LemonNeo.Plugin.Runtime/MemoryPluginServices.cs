using System.Collections.Concurrent;
using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.Runtime;

internal sealed class MemoryPluginStorage : IPluginStorage
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal sealed class MemoryCredentialVault : ICredentialVault
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.GetValueOrDefault(key));
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

internal sealed class PluginContext(
    ILoggerFactory loggerFactory,
    IHttpClientFactory httpClientFactory,
    ProviderRegistry registry,
    PlayerRegistry playerRegistry) : IPluginContext
{
    private readonly List<IMusicProvider> _registeredProviders = [];
    private readonly List<IPlayerProvider> _registeredPlayers = [];

    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public IHttpClientFactory HttpClientFactory { get; } = httpClientFactory;
    public IPluginStorage Storage { get; } = new MemoryPluginStorage();
    public ICredentialVault Credentials { get; } = new MemoryCredentialVault();
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
