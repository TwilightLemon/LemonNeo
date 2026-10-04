using LemonNeo.Core;
using LemonNeo.Playback;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed class PlaybackResolver(
    IProviderRegistry registry,
    IPlaybackSourceProviderRegistry playbackSources,
    IPlayerRegistry players,
    PlaybackSourceDiscoveryService sourceDiscovery,
    PlaybackAudioCache audioCache)
{
    private IPlaybackDownloadSink? _download;
    public IPlayerProvider? ActivePlayer { get; private set; }
    public Track? CurrentQueueTrack { get; private set; }

    public async Task<PlaybackSource> PlayAsync(
        Track track,
        PlaybackRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var playbackRequest = request ?? new PlaybackRequest();
            // A persisted queue snapshot is sufficient for offline playback.
            // Check before provider validation, ID discovery, and URL resolution.
            if (await TryPlayCachedAsync(track, playbackRequest, cancellationToken) is { } cachedSource)
                return cachedSource;
            ValidateRequiredSourceProvider(playbackRequest);
            var resolvedTrack = track;
            PlaybackSource? source = null;
            if ((playbackRequest.RequiredSourceProviderId is null ||
                 string.Equals(
                     playbackRequest.RequiredSourceProviderId,
                     track.Id.ProviderId,
                     StringComparison.OrdinalIgnoreCase)) &&
                playbackSources.Get(track.Id.ProviderId) is { } nativeSource)
            {
                source = await nativeSource.PlaybackProvider.ResolvePlaybackAsync(
                    track,
                    playbackRequest,
                    cancellationToken);

                if (source is null && playbackRequest.RequiredSourceProviderId is not null)
                {
                    throw new PlaybackResolutionException(
                        $"Required source provider '{playbackRequest.RequiredSourceProviderId}' reported that " +
                        $"'{track.Title}' is unavailable.");
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
                            ? $"No playable source could be found for '{track.Title}'."
                            : $"Required source provider '{playbackRequest.RequiredSourceProviderId}' " +
                              $"could not resolve '{track.Title}'.");
                source = candidate.Source;
                resolvedTrack = candidate.Track;
            }

            var cacheTrack = track with
            {
                ExternalIds = MusicCacheIdentity.GetIdentifiers(track.ExternalIds.Concat(resolvedTrack.ExternalIds))
            };
            // Discovery may reveal an ID whose audio was already cached by another snapshot.
            if (!track.ExternalIds.SequenceEqual(cacheTrack.ExternalIds) &&
                await TryPlayCachedAsync(cacheTrack, playbackRequest, cancellationToken) is { } discoveredCache)
                return discoveredCache;
            await PlaySourceAsync(resolvedTrack, source, cacheTrack, playbackRequest, cancellationToken);
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

    private async Task<FilePlaybackSource?> TryPlayCachedAsync(
        Track track, PlaybackRequest request, CancellationToken cancellationToken)
    {
        var source = await audioCache.TryGetAsync(track, request, cancellationToken);
        if (source is null) return null;
        try
        {
            await PlaySourceAsync(track, source, track, request, cancellationToken);
            return source;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A verified byte stream can still be rejected by a decoder.
            audioCache.Invalidate(track, request);
            return null;
        }
    }

    private async Task PlaySourceAsync(
        Track resolvedTrack,
        PlaybackSource source,
        Track queueTrack,
        PlaybackRequest request,
        CancellationToken cancellationToken)
    {
        var player = players.GetRequiredPlayer(source);
        await StopAsync(cancellationToken);
        try
        {
            if (source is HttpPlaybackSource { IsDirectAudio: true } http && player.Player is IStreamingPlayer streaming)
            {
                // Original and discovered external IDs are aliases for this download.
                _download = audioCache.Start(queueTrack, request, http);
                if (_download is not null)
                    await streaming.PlayAsync(resolvedTrack, http, _download, cancellationToken);
                else
                    await player.Player.PlayAsync(resolvedTrack, source, cancellationToken);
            }
            else
            {
                await player.Player.PlayAsync(resolvedTrack, source, cancellationToken);
            }
            ActivePlayer = player;
            CurrentQueueTrack = queueTrack;
        }
        catch
        {
            try { await player.Player.StopAsync(CancellationToken.None); }
            finally
            {
                if (_download is not null) audioCache.Invalidate(queueTrack, request);
                _download?.Dispose();
                _download = null;
            }
            throw;
        }
    }

    private void ValidateRequiredSourceProvider(PlaybackRequest request)
    {
        if (request.RequiredSourceProviderId is null)
        {
            return;
        }

        if (playbackSources.Get(request.RequiredSourceProviderId) is not null)
        {
            return;
        }

        if (registry.Get(request.RequiredSourceProviderId) is null)
        {
            throw new PlaybackResolutionException(
                $"Required source provider '{request.RequiredSourceProviderId}' was not found.");
        }

        throw new PlaybackResolutionException(
            $"Provider '{request.RequiredSourceProviderId}' does not supply playback sources.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (ActivePlayer is not null)
            await ActivePlayer.Player.StopAsync(cancellationToken);
        _download?.Dispose();
        _download = null;
        ActivePlayer = null;
        CurrentQueueTrack = null;
    }
}
