using LemonNeo.Abstractions.Providers;
using LemonNeo.Application.Providers;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Application.Playback;

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
    private const double MinimumMetadataConfidence = 0.85;

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
                minimumConfidence: MinimumMetadataConfidence,
                cancellationToken: cancellationToken)
            : new MetadataLookupResult([], new Dictionary<string, string>());
        foreach (var error in metadataLookup.SourceErrors)
        {
            errors[$"metadata/{error.Key}"] = error.Value;
        }

        var matches = metadataLookup.Matches
            .Where(match => match.Confidence >= MinimumMetadataConfidence)
            .OrderByDescending(match => match.Confidence)
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
                    var direct = await provider.ResolvePlaybackAsync(track.Id, request, cancellationToken);
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

                if (provider.AcceptedIdSchemes.Count > 0)
                {
                    var resolved = await ResolveDeclaredIdsAsync(
                        musicProvider.Id,
                        provider,
                        track,
                        confidence: 0.98,
                        request,
                        cancellationToken);
                    if (resolved is not null)
                    {
                        candidates.Add(resolved);
                        continue;
                    }

                    var bestMatch = matches.FirstOrDefault(match =>
                        match.Track.ExternalIds.Any(identifier =>
                            provider.AcceptedIdSchemes.Any(scheme => scheme.Accepts(identifier))));
                    if (bestMatch is not null)
                    {
                        var matched = await ResolveDeclaredIdsAsync(
                            musicProvider.Id,
                            provider,
                            bestMatch.Track,
                            bestMatch.Confidence,
                            request,
                            cancellationToken);
                        if (matched is not null)
                            candidates.Add(matched with
                            {
                                ResolutionMethod = $"metadata-match:{bestMatch.MetadataProviderId}/{bestMatch.SourceNamespace}"
                            });
                    }
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

    private static async Task<PlaybackSourceCandidate?> ResolveDeclaredIdsAsync(
        string providerId,
        IPlaybackProvider playbackProvider,
        Track track,
        double confidence,
        PlaybackRequest request,
        CancellationToken cancellationToken)
    {
        foreach (var identifier in track.ExternalIds)
        {
            if (!playbackProvider.AcceptedIdSchemes.Any(scheme => scheme.Accepts(identifier)))
            {
                continue;
            }

            var trackId = new MusicItemId(providerId, identifier.Value);
            var source = await playbackProvider.ResolvePlaybackAsync(trackId, request, cancellationToken);
            if (source is not null)
            {
                return new PlaybackSourceCandidate(
                    providerId,
                    track with { Id = trackId },
                    source,
                    confidence,
                    $"declared-id:{identifier.Namespace}/{identifier.Kind}");
            }
        }

        return null;
    }
}
