using LemonNeo.Abstractions.Playback;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Playback;

namespace LemonNeo.Infrastructure.Plugins;

internal sealed class PlayerRegistry : IPlayerRegistry
{
    private readonly Dictionary<string, IPlayerProvider> _players =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PlayerRegistry(IEnumerable<IPlayerProvider> hostPlayers)
    {
        foreach (var player in hostPlayers)
        {
            Register(player);
        }
    }

    public IReadOnlyCollection<IPlayerProvider> Players
    {
        get
        {
            lock (_gate)
            {
                return _players.Values
                    .OrderByDescending(player => player.Priority)
                    .ThenBy(player => player.Id, StringComparer.Ordinal)
                    .ToArray();
            }
        }
    }

    public IPlayerProvider? Get(string playerId)
    {
        lock (_gate)
        {
            return _players.GetValueOrDefault(playerId);
        }
    }

    public IPlayerProvider GetRequiredPlayer(PlaybackSource source)
    {
        lock (_gate)
        {
            return _players.Values
                       .Where(player => player.CanPlay(source))
                       .OrderByDescending(player => player.Priority)
                       .ThenBy(player => player.Id, StringComparer.Ordinal)
                       .FirstOrDefault()
                   ?? throw new PlaybackResolutionException(
                       $"No registered player supports source '{source.GetType().Name}'.");
        }
    }

    internal void Register(IPlayerProvider player)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(player.Id);
        lock (_gate)
        {
            if (!_players.TryAdd(player.Id, player))
            {
                throw new PluginLoadException($"Player '{player.Id}' is already registered.");
            }
        }
    }

    internal void Remove(IEnumerable<IPlayerProvider> players)
    {
        lock (_gate)
        {
            foreach (var player in players)
            {
                if (_players.TryGetValue(player.Id, out var current) && ReferenceEquals(current, player))
                {
                    _players.Remove(player.Id);
                }
            }
        }
    }
}
