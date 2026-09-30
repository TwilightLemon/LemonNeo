using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed class PlaybackResolver(IProviderRegistry registry, IPlayerRegistry players)
{
    public IPlayerProvider? ActivePlayer { get; private set; }

    public async Task<PlaybackSource> PlayAsync(
        TrackReference track,
        PlaybackRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.Get(track.Id.ProviderId)
            ?? throw new PlaybackResolutionException(
                $"Provider '{track.Id.ProviderId}' was not found for track '{track.Id}'.");
        if (provider is not IPlaybackProvider playbackProvider)
        {
            throw new PlaybackResolutionException(
                $"Provider '{track.Id.ProviderId}' does not support playback.");
        }

        try
        {
            var source = await playbackProvider.ResolvePlaybackAsync(
                track,
                request ?? new PlaybackRequest(),
                cancellationToken) ?? throw new PlaybackResolutionException(
                    $"Provider '{track.Id.ProviderId}' returned no source for '{track.Id.RemoteId}'.");
            var player = players.GetRequiredPlayer(source);
            if (ActivePlayer is { } active && !ReferenceEquals(active, player))
            {
                await active.Player.StopAsync(cancellationToken);
            }

            await player.Player.PlayAsync(track, source, cancellationToken);
            ActivePlayer = player;
            return source;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not PlaybackResolutionException)
        {
            throw new PlaybackResolutionException($"Could not resolve playback for '{track.Id}'.", exception);
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        ActivePlayer?.Player.PauseAsync(cancellationToken) ?? Task.CompletedTask;

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        ActivePlayer?.Player.ResumeAsync(cancellationToken) ?? Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (ActivePlayer is null)
        {
            return;
        }

        await ActivePlayer.Player.StopAsync(cancellationToken);
        ActivePlayer = null;
    }
}
