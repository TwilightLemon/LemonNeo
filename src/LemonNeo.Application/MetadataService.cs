using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed class MetadataService(IProviderRegistry registry)
{
    public async Task<MetadataLookupResult> LookupAsync(
        TrackReference track,
        IReadOnlyList<string>? targetNamespaces = null,
        int limitPerSource = 3,
        double minimumConfidence = 0.7,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<MetadataMatch>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var request = new MetadataLookupRequest
        {
            Track = track,
            TargetNamespaces = targetNamespaces ?? [],
            LimitPerSource = limitPerSource,
            MinimumConfidence = minimumConfidence
        };

        foreach (var provider in registry.Providers.OfType<IMetadataProvider>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var providerId = ((IMusicProvider)provider).Id;
            try
            {
                var result = await provider.LookupMetadataAsync(request, cancellationToken);
                matches.AddRange(result.Matches);
                foreach (var error in result.SourceErrors)
                {
                    errors[$"{providerId}/{error.Key}"] = error.Value;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors[providerId] = exception.Message;
            }
        }

        return new MetadataLookupResult(
            matches.OrderByDescending(match => match.Confidence)
                .ThenBy(match => match.SourceNamespace, StringComparer.Ordinal)
                .ToArray(),
            errors);
    }

    public async Task<(Track Track, MetadataLookupResult Lookup)> EnrichAsync(
        Track track,
        CancellationToken cancellationToken = default)
    {
        var lookup = await LookupAsync(track.ToReference(), cancellationToken: cancellationToken);
        var reliable = lookup.Matches.Where(match => match.Confidence >= 0.9).ToArray();
        if (reliable.Length == 0)
        {
            return (track, lookup);
        }

        var best = reliable[0].Metadata;
        var externalIds = track.ExternalIds.Concat(reliable.SelectMany(match => match.Metadata.ExternalIds))
            .DistinctBy(id => new
            {
                Namespace = id.Namespace.ToUpperInvariant(),
                id.Value,
                Authority = id.Authority?.ToUpperInvariant(),
                Kind = id.Kind?.ToUpperInvariant()
            })
            .ToArray();
        var enriched = track with
        {
            Title = string.IsNullOrWhiteSpace(best.Title) ? track.Title : best.Title,
            Artists = best.Artists.Count == 0
                ? track.Artists
                : best.Artists.Select(name => new ArtistRef { Name = name }).ToArray(),
            Album = string.IsNullOrWhiteSpace(best.Album)
                ? track.Album
                : new AlbumRef { Title = best.Album },
            Duration = best.Duration ?? track.Duration,
            ArtworkUri = best.ArtworkUri ?? track.ArtworkUri,
            ExternalIds = externalIds
        };
        return (enriched, lookup);
    }
}

public sealed record LyricsLookupResult(
    Lyrics? Lyrics,
    string? ProviderId,
    IReadOnlyDictionary<string, string> ProviderErrors);

public sealed class LyricsService(IProviderRegistry registry)
{
    public async Task<LyricsLookupResult> GetLyricsAsync(
        TrackReference track,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var providers = registry.Providers
            .Where(provider => provider is ILyricsProvider)
            .OrderByDescending(provider => provider.Id == track.Id.ProviderId)
            .ThenByDescending(provider => provider is IMetadataProvider);

        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lyrics = await ((ILyricsProvider)provider).GetLyricsAsync(track, cancellationToken);
                if (lyrics is not null)
                {
                    return new LyricsLookupResult(lyrics, provider.Id, errors);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors[provider.Id] = exception.Message;
            }
        }

        return new LyricsLookupResult(null, null, errors);
    }
}
