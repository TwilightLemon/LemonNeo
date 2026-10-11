using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Library;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Application.Playback;

public sealed class PlaybackResolver(
    IProviderRegistry registry,
    IPlaybackSourceProviderRegistry playbackSources,
    IPlayerRegistry players,
    PlaybackSourceDiscoveryService sourceDiscovery,
    PlaybackAudioCache audioCache,
    LibraryService library,
    PlaylistService playlists)
{
    private IPlaybackDownloadSink? _download;
    public IPlayerProvider? ActivePlayer { get; private set; }
    public Track? CurrentQueueTrack { get; private set; }
    public PlaybackSourceDetails? CurrentSourceDetails { get; private set; }
    public PlaybackSource? CurrentSource => CurrentSourceDetails?.Source;
    public double Volume { get; private set; } = 1;

    public bool CanAdjustVolume => ActivePlayer is { } active
        ? active.Player is IVolumePlayer
        : players.Players.Any(player => player.Player is IVolumePlayer);

    public async Task<double> GetVolumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ActivePlayer?.Player is IVolumePlayer player)
        {
            var volume = await player.GetVolumeAsync(cancellationToken);
            ValidateVolume(volume);
            Volume = volume;
        }

        return Volume;
    }

    public async Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateVolume(volume);
        if (ActivePlayer?.Player is IVolumePlayer player)
            await player.SetVolumeAsync(volume, cancellationToken);
        // Retain the preference before a player is selected and across decoder switches.
        Volume = volume;
    }

    private static void ValidateVolume(double volume)
    {
        if (!double.IsFinite(volume) || volume is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 1.");
    }

    public async Task<PlaybackSource> PlayAsync(
        Track track,
        PlaybackRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Stop the previous track before cache reads or source resolution,
            // which may take time or fail before the next player is selected.
            await StopAsync(cancellationToken);
            var playbackRequest = request ?? new PlaybackRequest();
            var discovery = await sourceDiscovery.FindAsync(track, playbackRequest, cancellationToken);
            CurrentQueueTrack = discovery.UpdatedTrack ?? track;
            await SaveDiscoveredIdsAsync(track, CurrentQueueTrack, cancellationToken);
            var candidate = discovery.Candidates.FirstOrDefault();
            if (candidate is null)
            {
                ValidateRequiredSourceProvider(playbackRequest);
                throw new PlaybackResolutionException($"No playable source could be found for '{track.Title}'.");
            }

            try
            {
                await PlaySourceAsync(candidate, CurrentQueueTrack, playbackRequest.PreferredPlayerId,
                    cancellationToken);
            }
            catch (Exception exception) when (candidate.IsCached && exception is not OperationCanceledException)
            {
                audioCache.Invalidate(candidate.SourceId, candidate.Source.Quality!.Value);
                // Retry discovery after rejecting a corrupt or undecodable cached resource.
                discovery = await sourceDiscovery.FindAsync(CurrentQueueTrack, playbackRequest, cancellationToken,
                    !discovery.MetadataExpanded);
                await SaveDiscoveredIdsAsync(CurrentQueueTrack, discovery.UpdatedTrack ?? CurrentQueueTrack,
                    cancellationToken);
                candidate = discovery.Candidates.FirstOrDefault()
                            ?? throw new PlaybackResolutionException(
                                $"No playable source could be found for '{track.Title}'.", exception);
                await PlaySourceAsync(candidate, discovery.UpdatedTrack ?? CurrentQueueTrack,
                    playbackRequest.PreferredPlayerId, cancellationToken);
            }

            return CurrentSource!;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                              and not PlaybackResolutionException)
        {
            throw new PlaybackResolutionException($"Could not resolve playback for '{track.Id}'.", exception);
        }
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        return ActivePlayer?.Player.PauseAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        return ActivePlayer?.Player.ResumeAsync(cancellationToken) ?? Task.CompletedTask;
    }

    private async Task PlaySourceAsync(
        PlaybackSourceCandidate candidate,
        Track queueTrack,
        string? preferredPlayerId,
        CancellationToken cancellationToken)
    {
        var source = candidate.Source;
        var resolvedTrack = candidate.Track;
        var player = players.GetRequiredPlayer(source, preferredPlayerId);
        try
        {
            if (player.Player is IVolumePlayer volumePlayer)
                await volumePlayer.SetVolumeAsync(Volume, cancellationToken);
            if (source is HttpPlaybackSource { IsDirectAudio: true } http &&
                player.Player is IStreamingPlayer streaming)
            {
                _download = audioCache.Start(candidate.SourceId, http, player.Player is IAudioInfoPlayer);
                if (_download is not null)
                    await streaming.PlayAsync(resolvedTrack, http, _download, cancellationToken);
                else
                    await player.Player.PlayAsync(resolvedTrack, source, cancellationToken);
            }
            else
            {
                await player.Player.PlayAsync(resolvedTrack, source, cancellationToken);
            }

            if (player.Player is IAudioInfoPlayer audioPlayer)
            {
                var info = audioPlayer.AudioInfo;
                if (_download is not null) audioCache.SetAudioInfo(_download, info);
                if (candidate.IsCached && source is FilePlaybackSource cached)
                    await audioCache.RefreshAudioInfoAsync(candidate.SourceId, cached, info, cancellationToken);
                source = source with
                {
                    Quality = AudioQualities.Resolve(info, source.Quality),
                    BitrateKbps = info?.BitrateKbps is > 0 ? info.BitrateKbps : source.BitrateKbps
                };
            }

            ActivePlayer = player;
            CurrentQueueTrack = queueTrack;
            CurrentSourceDetails = new PlaybackSourceDetails(source, candidate.IsCached ? null : candidate.ProviderId,
                candidate.SourceId, candidate.IsCached);
        }
        catch
        {
            try
            {
                await player.Player.StopAsync(CancellationToken.None);
            }
            finally
            {
                if (_download is not null && source.Quality is { } quality)
                    audioCache.Invalidate(candidate.SourceId, quality);
                _download?.Dispose();
                _download = null;
            }

            throw;
        }
    }

    private async Task SaveDiscoveredIdsAsync(Track original, Track updated, CancellationToken cancellationToken)
    {
        if (original.ExternalIds.SequenceEqual(updated.ExternalIds)) return;
        var changed = false;
        foreach (var playlist in library.Current.Playlists)
        foreach (var item in playlist.Items.Where(item => item.Track.Id == original.Id))
        {
            var ids = item.Track.ExternalIds.Concat(updated.ExternalIds).DistinctBy(MusicCacheIdentity.GetKey)
                .ToArray();
            if (item.Track.ExternalIds.SequenceEqual(ids)) continue;
            playlists.UpdateTrack(playlist.Id, item.Id, item.Track with { ExternalIds = ids });
            changed = true;
        }

        if (changed) await library.SaveAsync(cancellationToken);
    }

    private void ValidateRequiredSourceProvider(PlaybackRequest request)
    {
        if (request.RequiredSourceProviderId is null) return;

        if (playbackSources.Get(request.RequiredSourceProviderId) is not null) return;

        if (registry.Get(request.RequiredSourceProviderId) is null)
            throw new PlaybackResolutionException(
                $"Required source provider '{request.RequiredSourceProviderId}' was not found.");

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
        CurrentSourceDetails = null;
    }
}