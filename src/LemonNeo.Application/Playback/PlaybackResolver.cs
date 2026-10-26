using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Library;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Application.Playback;

/// <summary>All resources belong to this preparation, never to a later request.</summary>
public sealed class PreparedPlayback(
    IPlayerProvider player,
    IPreparedPlayback stream,
    Track track,
    PlaybackSourceDetails sourceDetails,
    IPlaybackDownloadSink? download) : IDisposable
{
    private int _disposed;
    public IPlayerProvider Player { get; } = player;
    public Track Track { get; } = track;
    public PlaybackSourceDetails SourceDetails { get; } = sourceDetails;
    public void Activate(double volume) => stream.Activate(volume);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { stream.Dispose(); }
        finally { download?.Dispose(); }
    }
}

/// <summary>Prepares independent requests; the session owns activation and active state.</summary>
public sealed class PlaybackResolver(
    IProviderRegistry registry,
    IPlaybackSourceProviderRegistry playbackSources,
    IPlayerRegistry players,
    PlaybackSourceDiscoveryService sourceDiscovery,
    PlaybackAudioCache audioCache,
    LibraryService library,
    PlaylistService playlists)
{
    public bool CanAdjustVolume => players.Players.Count > 0;

    public async Task<PreparedPlayback> PrepareAsync(Track track, PlaybackRequest request,
        CancellationToken cancellationToken = default)
    {
        var discovery = await sourceDiscovery.FindAsync(track, request, cancellationToken);
        var candidate = FirstCandidate(discovery, track, request);
        try
        {
            return await PrepareSourceAsync(candidate, discovery.UpdatedTrack ?? track, request, cancellationToken);
        }
        catch (PlaybackSourceUnavailableException) when (candidate.IsCached)
        {
            audioCache.Invalidate(candidate.SourceId, candidate.Source.Quality!.Value);
            discovery = await sourceDiscovery.FindAsync(discovery.UpdatedTrack ?? track, request, cancellationToken,
                !discovery.MetadataExpanded);
            candidate = FirstCandidate(discovery, track, request);
            return await PrepareSourceAsync(candidate, discovery.UpdatedTrack ?? track, request, cancellationToken);
        }
    }

    private PlaybackSourceCandidate FirstCandidate(PlaybackSourceDiscoveryResult discovery, Track track,
        PlaybackRequest request)
    {
        if (discovery.Candidates.FirstOrDefault() is { } candidate) return candidate;
        if (request.RequiredSourceProviderId is { } required && playbackSources.Get(required) is null)
            throw new PlaybackResolutionException(registry.Get(required) is null
                ? $"Required source provider '{required}' was not found."
                : $"Provider '{required}' does not supply playback sources.");
        throw new PlaybackSourceUnavailableException($"No playable source could be found for '{track.Title}'.");
    }

    private async Task<PreparedPlayback> PrepareSourceAsync(PlaybackSourceCandidate candidate, Track queueTrack,
        PlaybackRequest request, CancellationToken cancellationToken)
    {
        // A missing decoder is a host configuration error, not an unavailable track.
        var player = players.GetRequiredPlayer(candidate.Source, request.PreferredPlayerId);
        IPlaybackDownloadSink? download = null;
        IPreparedPlayback? stream = null;
        try
        {
            if (candidate.Source is HttpPlaybackSource { IsDirectAudio: true } http &&
                player.Player is IStreamingPlayer)
                download = audioCache.Start(candidate.SourceId, http, player.Player is IAudioInfoPlayer);
            stream = await player.Player.PrepareAsync(candidate.Track, candidate.Source, download, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var source = candidate.Source;
            if (stream.AudioInfo is { } info)
            {
                if (download is not null) audioCache.SetAudioInfo(download, info);
                if (candidate.IsCached && source is FilePlaybackSource cached)
                    await audioCache.RefreshAudioInfoAsync(candidate.SourceId, cached, info, cancellationToken);
                source = source with
                {
                    Quality = AudioQualities.Resolve(info, source.Quality),
                    BitrateKbps = info.BitrateKbps is > 0 ? info.BitrateKbps : source.BitrateKbps
                };
            }
            return new PreparedPlayback(player, stream, queueTrack,
                new PlaybackSourceDetails(source, candidate.IsCached ? null : candidate.ProviderId,
                    candidate.SourceId, candidate.IsCached), download);
        }
        catch (Exception exception)
        {
            stream?.Dispose();
            download?.Dispose();
            if (exception is OperationCanceledException) throw;
            if (exception is ObjectDisposedException or NotSupportedException or OutOfMemoryException)
                throw new PlaybackResolutionException($"Could not prepare the audio player for '{queueTrack.Title}'.", exception);
            throw new PlaybackSourceUnavailableException($"Could not open audio for '{queueTrack.Title}'.", exception);
        }
    }

    public async Task SaveDiscoveredIdsAsync(Track original, Track updated,
        CancellationToken cancellationToken = default)
    {
        if (original.ExternalIds.SequenceEqual(updated.ExternalIds)) return;
        var changed = false;
        foreach (var playlist in library.Current.Playlists.ToArray())
        foreach (var item in playlist.Items.Where(item => item.Track.Id == original.Id).ToArray())
        {
            var ids = item.Track.ExternalIds.Concat(updated.ExternalIds).DistinctBy(MusicCacheIdentity.GetKey)
                .ToArray();
            if (item.Track.ExternalIds.SequenceEqual(ids)) continue;
            playlists.UpdateTrack(playlist.Id, item.Id, item.Track with { ExternalIds = ids });
            changed = true;
        }
        if (changed) await library.SaveAsync(cancellationToken);
    }
}
