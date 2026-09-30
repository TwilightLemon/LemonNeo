using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Plugin.Runtime;

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IMusicProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public IReadOnlyCollection<IMusicProvider> Providers
    {
        get
        {
            lock (_gate)
            {
                return _providers.Values.OrderBy(provider => provider.Id).ToArray();
            }
        }
    }

    public IMusicProvider? Get(string providerId)
    {
        lock (_gate)
        {
            return _providers.GetValueOrDefault(providerId);
        }
    }

    internal void Register(IMusicProvider provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);

        lock (_gate)
        {
            if (!_providers.TryAdd(provider.Id, provider))
            {
                throw new PluginLoadException($"Provider '{provider.Id}' is already registered.");
            }
        }
    }

    internal void Remove(IEnumerable<IMusicProvider> providers)
    {
        lock (_gate)
        {
            foreach (var provider in providers)
            {
                if (_providers.TryGetValue(provider.Id, out var current) && ReferenceEquals(current, provider))
                {
                    _providers.Remove(provider.Id);
                }
            }
        }
    }
}

public sealed class PluginLoadException(string message, Exception? innerException = null)
    : Exception(message, innerException);
