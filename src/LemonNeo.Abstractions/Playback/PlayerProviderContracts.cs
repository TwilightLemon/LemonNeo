using LemonNeo.Core.Playback;

namespace LemonNeo.Abstractions.Playback;

public sealed record PlayerDescriptor(string Name, string Description);

public interface IPlayerProvider
{
    string Id { get; }
    PlayerDescriptor Descriptor { get; }
    /// <summary>Audio filename extensions understood by this decoder, including the leading dot.</summary>
    IReadOnlyCollection<string> SupportedExtensions { get; }
    IPlayer Player { get; }
    bool CanPlay(PlaybackSource source);
}

public interface IPlayerRegistry
{
    IReadOnlyCollection<IPlayerProvider> Players { get; }
    IPlayerProvider? Get(string playerId);
    IPlayerProvider GetRequiredPlayer(PlaybackSource source, string? preferredPlayerId = null);
}
