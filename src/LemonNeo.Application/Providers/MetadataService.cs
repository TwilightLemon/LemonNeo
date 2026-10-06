using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Music;

namespace LemonNeo.Application.Providers;

public sealed class MetadataService(IProviderRegistry registry)
{
    public async Task<MetadataLookupResult> LookupAsync(
        Track track,
        IReadOnlyList<string>? targetNamespaces = null,
        int limitPerSource = 3,
        double minimumConfidence = 0.7,
        CancellationToken cancellationToken = default,
        string? searchKeyword = null)
    {
        var matches = new List<MetadataMatch>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var request = new MetadataLookupRequest
        {
            Track = track,
            SearchKeyword = string.IsNullOrWhiteSpace(searchKeyword) ? null : searchKeyword.Trim(),
            TargetNamespaces = targetNamespaces ?? [],
            LimitPerSource = limitPerSource,
            MinimumConfidence = minimumConfidence
        };

        foreach (var provider in registry.Providers.OfType<IMetadataProvider>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.TargetNamespaces.Count > 0 && !provider.MetadataNamespaces.Any(ns =>
                request.TargetNamespaces.Contains(ns, StringComparer.OrdinalIgnoreCase))) continue;
            var providerId = ((IMusicProvider)provider).Id;
            try
            {
                var result = await provider.LookupMetadataAsync(request, cancellationToken);
                matches.AddRange(result.Matches.Where(match => request.TargetNamespaces.Count == 0 ||
                    request.TargetNamespaces.Contains(match.SourceNamespace, StringComparer.OrdinalIgnoreCase)));
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

        cancellationToken.ThrowIfCancellationRequested();
        return new MetadataLookupResult(
            matches.OrderByDescending(match => match.Confidence)
                .ThenBy(match => match.SourceNamespace, StringComparer.Ordinal)
                .ToArray(),
            errors);
    }

    public Task<(Track Track, MetadataLookupResult Lookup)> EnrichAsync(
        Track track,
        CancellationToken cancellationToken = default) =>
        EnrichAsync(track, null, cancellationToken);

    public async Task<(Track Track, MetadataLookupResult Lookup)> EnrichAsync(
        Track track,
        IReadOnlyList<string>? targetNamespaces,
        CancellationToken cancellationToken = default)
    {
        var lookup = await LookupAsync(
            track,
            targetNamespaces,
            cancellationToken: cancellationToken);
        var reliable = lookup.Matches.Where(match => match.Confidence >= 0.6).ToArray();
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
