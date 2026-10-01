using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed class PlaybackResolver(
    IProviderRegistry registry,
    IPlayerRegistry players,
    PlaybackSourceDiscoveryService sourceDiscovery)
{
    public IPlayerProvider? ActivePlayer { get; private set; }

    public async Task<PlaybackSource> PlayAsync(
        TrackReference track,
        PlaybackRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var playbackRequest = request ?? new PlaybackRequest();
            ValidateRequiredSourceProvider(playbackRequest);
            var resolvedTrack = track;
            PlaybackSource? source = null;
            if ((playbackRequest.RequiredSourceProviderId is null ||
                 string.Equals(
                     playbackRequest.RequiredSourceProviderId,
                     track.Id.ProviderId,
                     StringComparison.OrdinalIgnoreCase)) &&
                registry.Get(track.Id.ProviderId) is IPlaybackProvider playbackProvider)
            {
                source = await playbackProvider.ResolvePlaybackAsync(track, playbackRequest, cancellationToken);

                if (source is null && playbackRequest.RequiredSourceProviderId is not null)
                {
                    throw new PlaybackResolutionException(
                        $"Required source provider '{playbackRequest.RequiredSourceProviderId}' reported that " +
                        $"'{track.Title ?? track.Id.ToString()}' is unavailable.");
                }
            }

            if (source is null)
            {
                var discovery = await sourceDiscovery.FindKnownAsync(track, playbackRequest, cancellationToken);
                if (discovery.Candidates.Count == 0)
                {
                    discovery = await sourceDiscovery.FindAsync(track, playbackRequest, cancellationToken);
                }

                var candidate = discovery.Candidates.FirstOrDefault()
                    ?? throw new PlaybackResolutionException(
                        playbackRequest.RequiredSourceProviderId is null
                            ? $"No playable source could be found for '{track.Title ?? track.Id.ToString()}'."
                            : $"Required source provider '{playbackRequest.RequiredSourceProviderId}' " +
                              $"could not resolve '{track.Title ?? track.Id.ToString()}'.");
                source = candidate.Source;
                resolvedTrack = candidate.Track;
            }

            var player = players.GetRequiredPlayer(source);
            if (ActivePlayer is { } active && !ReferenceEquals(active, player))
            {
                await active.Player.StopAsync(cancellationToken);
            }

            await player.Player.PlayAsync(resolvedTrack, source, cancellationToken);
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

    private void ValidateRequiredSourceProvider(PlaybackRequest request)
    {
        if (request.RequiredSourceProviderId is null)
        {
            return;
        }

        var provider = registry.Get(request.RequiredSourceProviderId)
            ?? throw new PlaybackResolutionException(
                $"Required source provider '{request.RequiredSourceProviderId}' was not found.");
        if (provider is not IPlaybackProvider)
        {
            throw new PlaybackResolutionException(
                $"Provider '{request.RequiredSourceProviderId}' does not supply playback sources.");
        }
    }

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
