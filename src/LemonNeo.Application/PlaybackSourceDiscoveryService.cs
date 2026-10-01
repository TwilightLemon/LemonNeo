using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed record PlaybackSourceCandidate(
    string ProviderId,
    Track Track,
    PlaybackSource Source,
    double Confidence,
    string ResolutionMethod);

public sealed record PlaybackSourceDiscoveryResult(
    IReadOnlyList<PlaybackSourceCandidate> Candidates,
    IReadOnlyDictionary<string, string> ProviderErrors);

public sealed class PlaybackSourceDiscoveryService(
    IPlaybackSourceProviderRegistry playbackSources,
    MetadataService metadataService)
{
    public Task<PlaybackSourceDiscoveryResult> FindKnownAsync(
        Track track,
        PlaybackRequest? request = null,
        CancellationToken cancellationToken = default) =>
        FindCoreAsync(track, request, expandMetadata: false, cancellationToken);

    public async Task<PlaybackSourceDiscoveryResult> FindAsync(
        Track track,
        PlaybackRequest? request = null,
        CancellationToken cancellationToken = default) =>
        await FindCoreAsync(track, request, expandMetadata: true, cancellationToken);

    private async Task<PlaybackSourceDiscoveryResult> FindCoreAsync(
        Track track,
        PlaybackRequest? request,
        bool expandMetadata,
        CancellationToken cancellationToken)
    {
        request ??= new PlaybackRequest();
        var candidates = new List<PlaybackSourceCandidate>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var selectedSources = playbackSources.Sources
            .Where(source => request.RequiredSourceProviderId is null ||
                             string.Equals(
                                 source.ProviderId,
                                 request.RequiredSourceProviderId,
                                 StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var targetNamespaces = selectedSources
            .SelectMany(source => source.AcceptedIdSchemes)
            .Select(scheme => scheme.Namespace)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var shouldExpandMetadata = expandMetadata && targetNamespaces.Length > 0;
        var metadataLookup = shouldExpandMetadata
            ? await metadataService.LookupAsync(
                track,
                targetNamespaces,
                limitPerSource: 2,
                minimumConfidence: 0.85,
                cancellationToken: cancellationToken)
            : new MetadataLookupResult([], new Dictionary<string, string>());
        foreach (var error in metadataLookup.SourceErrors)
        {
            errors[$"metadata/{error.Key}"] = error.Value;
        }

        var metadata = new[] { FromTrack(track) }
            .Concat(metadataLookup.Matches.Select(match => match.Metadata))
            .ToArray();

        foreach (var sourceProvider in selectedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var musicProvider = sourceProvider.MusicProvider;
            var provider = sourceProvider.PlaybackProvider;
            try
            {
                if (string.Equals(track.Id.ProviderId, musicProvider.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var direct = await provider.ResolvePlaybackAsync(track, request, cancellationToken);
                    if (direct is not null)
                    {
                        candidates.Add(new PlaybackSourceCandidate(
                            musicProvider.Id,
                            track,
                            direct,
                            1,
                            "provider-native-id"));
                        continue;
                    }
                }

                if (provider is ITrackIdResolverProvider idResolver)
                {
                    var resolved = await ResolveDeclaredIdsAsync(
                        musicProvider.Id,
                        provider,
                        idResolver,
                        metadata,
                        request,
                        cancellationToken);
                    if (resolved is not null)
                    {
                        candidates.Add(resolved);
                        continue;
                    }
                }

                var searchMetadata = SelectSearchMetadata(
                    musicProvider,
                    provider as ITrackIdResolverProvider,
                    metadataLookup.Matches,
                    metadata[0]);
                var searched = await SearchProviderAsync(
                    musicProvider,
                    provider,
                    searchMetadata,
                    request,
                    cancellationToken);
                if (searched is not null)
                {
                    candidates.Add(searched);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors[musicProvider.Id] = exception.Message;
            }
        }

        return new PlaybackSourceDiscoveryResult(
            candidates.OrderByDescending(candidate => candidate.Confidence).ToArray(),
            errors);
    }

    private static TrackMetadata SelectSearchMetadata(
        IMusicProvider provider,
        ITrackIdResolverProvider? idResolver,
        IReadOnlyList<MetadataMatch> matches,
        TrackMetadata fallback)
    {
        if (idResolver is not null)
        {
            var targetMatch = matches.FirstOrDefault(match =>
                match.Metadata.ExternalIds.Any(identifier =>
                    idResolver.AcceptedIdSchemes.Any(scheme =>
                        string.Equals(scheme.Namespace, identifier.Namespace, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(scheme.Kind, identifier.Kind, StringComparison.OrdinalIgnoreCase))));
            if (targetMatch is not null)
            {
                return targetMatch.Metadata;
            }
        }

        return matches.FirstOrDefault(match =>
                   string.Equals(match.SourceNamespace, provider.Id, StringComparison.OrdinalIgnoreCase))?.Metadata
               ?? fallback;
    }

    private static async Task<PlaybackSourceCandidate?> ResolveDeclaredIdsAsync(
        string providerId,
        IPlaybackProvider playbackProvider,
        ITrackIdResolverProvider resolver,
        IReadOnlyList<TrackMetadata> metadata,
        PlaybackRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var item in metadata)
        {
            foreach (var identifier in item.ExternalIds)
            {
                if (!resolver.AcceptedIdSchemes.Any(scheme => scheme.Accepts(identifier)))
                {
                    continue;
                }

                var resolvedTrack = await resolver.ResolveTrackIdAsync(identifier, item, cancellationToken);
                if (resolvedTrack is null)
                {
                    continue;
                }

                var source = await playbackProvider.ResolvePlaybackAsync(resolvedTrack, request, cancellationToken);
                if (source is not null)
                {
                    return new PlaybackSourceCandidate(
                        providerId,
                        resolvedTrack,
                        source,
                        0.98,
                        $"declared-id:{identifier.Namespace}/{identifier.Kind}");
                }
            }
        }

        return null;
    }

    private async Task<PlaybackSourceCandidate?> SearchProviderAsync(
        IMusicProvider musicProvider,
        IPlaybackProvider playbackProvider,
        TrackMetadata metadata,
        PlaybackRequest request,
        CancellationToken cancellationToken)
    {
        var tracks = new List<Track>();
        if (musicProvider is ISearchProvider searchProvider)
        {
            var query = string.Join(' ', new[] { metadata.Title }.Concat(metadata.Artists));
            var result = await searchProvider.SearchAsync(new SearchRequest(query, 20), cancellationToken);
            tracks.AddRange(result.Tracks);
        }

        var best = tracks.DistinctBy(candidate => candidate.Id)
            .Select(candidate => new { Track = candidate, Score = Score(metadata, candidate) })
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        if (best is null || best.Score < 0.72)
        {
            return null;
        }

        var source = await playbackProvider.ResolvePlaybackAsync(best.Track, request, cancellationToken);
        return source is null
            ? null
            : new PlaybackSourceCandidate(
                musicProvider.Id,
                best.Track,
                source,
                best.Score,
                "provider-search");
    }

    private static TrackMetadata FromTrack(Track track) => new()
    {
        Title = track.Title,
        Artists = track.Artists.Select(artist => artist.Name).ToArray(),
        Album = track.Album?.Title,
        Duration = track.Duration,
        ArtworkUri = track.ArtworkUri,
        ExternalIds = track.ExternalIds
    };

    private static double Score(TrackMetadata expected, Track candidate)
    {
        var title = Normalize(expected.Title) == Normalize(candidate.Title) ? 0.6 : 0;
        var expectedArtists = expected.Artists.Select(Normalize).Where(value => value.Length > 0).ToHashSet();
        var candidateArtists = candidate.Artists.Select(artist => Normalize(artist.Name)).ToHashSet();
        var artist = expectedArtists.Count == 0
            ? 0.2
            : expectedArtists.Overlaps(candidateArtists) ? 0.3 : 0;
        var duration = expected.Duration is null || candidate.Duration is null
            ? 0.1
            : Math.Abs((expected.Duration.Value - candidate.Duration.Value).TotalSeconds) <= 3 ? 0.1 : 0;
        return title + artist + duration;
    }

    private static string Normalize(string? value) => new(
        (value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
