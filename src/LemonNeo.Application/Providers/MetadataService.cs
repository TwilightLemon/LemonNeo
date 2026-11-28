using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;

namespace LemonNeo.Application.Providers;

public sealed class MetadataService(IProviderRegistry registry, TrackLookupService trackLookup)
{
    private const int MinimumSearchCandidates = 10;

    public static IReadOnlyCollection<string> GetSourceNamespaces(IMusicProvider provider) =>
        provider is ISearchProvider scoped && provider is ICatalogProvider
            ? scoped.SearchNamespaces
            : [];

    public static string GetTrackSearchKeyword(Track track)
    {
        return string.Join(" ", new[] { track.Title }.Concat(track.Artists.Select(artist => artist.Name))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
    }

    public async Task<MetadataLookupResult> LookupAsync(
        Track track,
        IReadOnlyList<string>? targetNamespaces = null,
        int limitPerSource = 3,
        double minimumConfidence = 0.8,
        string? searchKeyword = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keyword = string.IsNullOrWhiteSpace(searchKeyword) ? GetTrackSearchKeyword(track) : searchKeyword.Trim();
        if (limitPerSource <= 0 || keyword.Length == 0) return new MetadataLookupResult([], errors);

        var sources = registry.Providers.SelectMany(provider => GetSourceNamespaces(provider)
            .Where(ns => !string.IsNullOrWhiteSpace(ns))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(ns => targetNamespaces is not { Count: > 0 } ||
                         targetNamespaces.Contains(ns, StringComparer.OrdinalIgnoreCase))
            .Select(ns => (Provider: provider, Namespace: ns)));
        var matches = new List<MetadataMatch>();
        foreach (var (provider, sourceNamespace) in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = new SearchRequest(keyword, Math.Max(MinimumSearchCandidates, limitPerSource), sourceNamespace);
                var results = await ((ISearchProvider)provider).SearchAsync(request, cancellationToken);
                foreach (var candidate in results.Tracks.DistinctBy(candidate => candidate.Id))
                {
                    var (confidence, level) = TrackMatchScorer.Compare(track, candidate);
                    if (confidence >= minimumConfidence)
                        matches.Add(new MetadataMatch(provider.Id, sourceNamespace, candidate, confidence, level));
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                errors[$"{provider.Id}/{sourceNamespace}"] = exception.Message;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new MetadataLookupResult(
            matches.GroupBy(match => match.SourceNamespace, StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group.OrderByDescending(match => match.Confidence).Take(limitPerSource))
                .OrderByDescending(match => match.Confidence)
                .ThenBy(match => match.SourceNamespace, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            errors);
    }

    public async Task<(Track Track, MetadataLookupResult Lookup)> MatchAndReplaceAsync(
        Track track,
        IReadOnlyList<string>? targetNamespaces,
        CancellationToken cancellationToken = default)
    {
        var lookup = await LookupAsync(
            track,
            targetNamespaces,
            1,
            cancellationToken: cancellationToken);
        var reliable = lookup.Matches.ToArray();
        if (reliable.Length == 0) return (track, lookup);

        var best = reliable[0].Track;
        var album = await ResolveAlbumAsync(track.Album, best.Album, reliable, cancellationToken);
        // An external-ID scheme is unique by namespace and kind. Metadata matching may
        // fill a missing scheme, but must never replace (or add a competing value for)
        // a scheme already carried by the source track.
        var externalIds = track.ExternalIds.ToList();
        foreach (var candidate in reliable.SelectMany(match => match.Track.ExternalIds))
        {
            if (externalIds.Any(existing => SameScheme(existing, candidate))) continue;
            externalIds.Add(candidate);
        }

        var enriched = track with
        {
            Title = string.IsNullOrWhiteSpace(best.Title) ? track.Title : best.Title,
            Artists = best.Artists.Count == 0
                ? track.Artists
                : best.Artists,
            Album = album,
            Duration = best.Duration ?? track.Duration,
            ArtworkUri = best.ArtworkUri ?? track.ArtworkUri,
            ExternalIds = externalIds.ToArray()
        };
        return (enriched, lookup);
    }

    private async Task<AlbumRef?> ResolveAlbumAsync(AlbumRef? original, AlbumRef? matched,
        IReadOnlyList<MetadataMatch> reliable, CancellationToken cancellationToken)
    {
        if (matched is null || string.IsNullOrWhiteSpace(matched.Title)) return original;
        if (matched.Id is not null) return matched;
        if (original is { Id: { } originalId } && SameAlbumTitle(matched, original))
            return matched with { Id = originalId };

        foreach (var match in reliable)
        {
            if (match.Track.Album is not { } candidate || !SameAlbumTitle(matched, candidate)) continue;
            if (candidate.Id is { } candidateId) return matched with { Id = candidateId };
            if (registry.Get(match.MetadataProviderId) is not { } provider ||
                provider is not (ICatalogProvider and ITrackLookupProvider)) continue;

            var songIds = match.Track.ExternalIds.Where(id =>
                    string.Equals(id.Namespace, match.SourceNamespace, StringComparison.OrdinalIgnoreCase) &&
                    id.Kind is "song-mid" or "song-id")
                .OrderBy(id => id.Kind == "song-mid" ? 0 : 1);
            foreach (var songId in songIds)
            {
                try
                {
                    var track = await trackLookup.GetTrackAsync(provider.Id, songId.Value, cancellationToken);
                    if (track?.Album is { Id: { } id } album && SameAlbumTitle(matched, album))
                        return matched with { Id = id };
                }
                catch (ProviderException)
                {
                    // Keep the metadata match even when the source cannot resolve the album.
                }
            }
        }
        return matched;
    }

    private static bool SameAlbumTitle(AlbumRef left, AlbumRef right) =>
        left.Title.Trim().Equals(right.Title.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameScheme(ExternalMusicId left, ExternalMusicId right)
    {
        return string.Equals(left.Namespace, right.Namespace, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase);
    }
}
