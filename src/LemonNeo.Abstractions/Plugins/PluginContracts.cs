using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Abstractions.Settings;
using LemonNeo.Abstractions.Storage;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Abstractions.Plugins;

public sealed record PluginDescriptor(string Id, string Name, string Version);

public interface ILemonPlugin
{
    PluginDescriptor Descriptor { get; }

    ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

    ValueTask ShutdownAsync(CancellationToken cancellationToken);
}

public interface IPluginContext
{
    ILoggerFactory LoggerFactory { get; }
    IHttpClientFactory HttpClientFactory { get; }
    ISettingsScope Settings { get; }
    ICredentialVault Credentials { get; }

    /// <summary>
    ///     Gets the plugin's current private cache directory. Plugins should read this
    ///     property when starting an operation instead of retaining the returned path.
    /// </summary>
    string CacheDirectory { get; }

    /// <summary>Access to the host session for Interactive plugins.</summary>
    IPlaybackInteraction Playback => throw new NotSupportedException("This host does not expose playback interaction.");

    void RegisterProvider(IMusicProvider provider);
    void RegisterPlayer(IPlayerProvider player);
}