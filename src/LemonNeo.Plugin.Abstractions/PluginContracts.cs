using LemonNeo.Core;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.Abstractions;

public sealed record PluginDescriptor(string Id, string Name, string Version);

public sealed record ProviderDescriptor(string Name, string Description);

public interface ILemonPlugin
{
    PluginDescriptor Descriptor { get; }

    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

    ValueTask ShutdownAsync(CancellationToken cancellationToken);
}

public interface IMusicProvider
{
    string Id { get; }
    ProviderDescriptor Descriptor { get; }
}

public interface IProviderRegistry
{
    IReadOnlyCollection<IMusicProvider> Providers { get; }
    IMusicProvider? Get(string providerId);
}

public sealed record PlayerDescriptor(string Name, string Description);

public interface IPlayerProvider
{
    string Id { get; }
    PlayerDescriptor Descriptor { get; }
    int Priority { get; }
    IPlayer Player { get; }
    bool CanPlay(PlaybackSource source);
}

public interface IPlayerRegistry
{
    IReadOnlyCollection<IPlayerProvider> Players { get; }
    IPlayerProvider? Get(string playerId);
    IPlayerProvider GetRequiredPlayer(PlaybackSource source);
}

public interface IPluginContext
{
    ILoggerFactory LoggerFactory { get; }
    IHttpClientFactory HttpClientFactory { get; }
    IPluginStorage Storage { get; }
    ICredentialVault Credentials { get; }
    void RegisterProvider(IMusicProvider provider);
    void RegisterPlayer(IPlayerProvider player);
}

public interface IPluginStorage
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface ICredentialVault
{
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
    Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default);
}
