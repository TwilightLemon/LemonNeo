using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Abstractions.Playback;

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
