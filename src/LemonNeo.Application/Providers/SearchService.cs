using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;

namespace LemonNeo.Application.Providers;

public sealed record OnlineSearchResult(
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<Album> Albums,
    IReadOnlyList<Artist> Artists,
    IReadOnlyList<string> FailedSources)
{
    public bool AllSourcesFailed { get; init; }
}

public sealed class SearchService(IProviderRegistry registry, IPlaybackSourceProviderRegistry playbackSources)
{
    public async Task<OnlineSearchResult> SearchOnlineAsync(string keyword, SearchKind kind, string? providerId,
        string? sourceNamespace, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new SearchRequest(keyword, SourceNamespace: sourceNamespace);
        var providers = registry.Providers.Where(provider =>
            (providerId is null || string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase)) &&
            (kind == SearchKind.Track ? provider is ISearchProvider : provider is ICatalogProvider) &&
            (sourceNamespace is null || provider is ISearchNamespaceProvider namespaces &&
                namespaces.SearchNamespaces.Contains(sourceNamespace, StringComparer.OrdinalIgnoreCase)));
        var batches = await Task.WhenAll(providers.Select(async provider =>
        {
            try
            {
                return kind switch
                {
                    SearchKind.Album => new OnlineSearchResult([], await ((ICatalogProvider)provider)
                        .SearchAlbumsAsync(request, cancellationToken), [], []),
                    SearchKind.Artist => new OnlineSearchResult([], [], await ((ICatalogProvider)provider)
                        .SearchArtistsAsync(request, cancellationToken), []),
                    _ => new OnlineSearchResult((await SearchTracksAsync((ISearchProvider)provider, request,
                        cancellationToken)).Tracks, [], [], [])
                };
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return new OnlineSearchResult([], [], [], [provider.Descriptor.Name]);
            }
        }));
        cancellationToken.ThrowIfCancellationRequested();
        return new OnlineSearchResult(
            batches.SelectMany(result => result.Tracks).DistinctBy(track => track.Id).ToArray(),
            batches.SelectMany(result => result.Albums).DistinctBy(album => album.Id).ToArray(),
            batches.SelectMany(result => result.Artists).DistinctBy(artist => artist.Id).ToArray(),
            batches.SelectMany(result => result.FailedSources).ToArray())
        {
            AllSourcesFailed = batches.Length > 0 && batches.All(result => result.FailedSources.Count > 0)
        };
    }

    /// <summary>Searches every provider within the source capabilities of the loaded playback providers.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchForPlaybackAsync(
        string keyword,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = playbackSources.Sources.ToArray();
        if (sources.Length == 0) return [];
        var namespaces = sources.SelectMany(source => source.AcceptedIdSchemes.Select(scheme => scheme.Namespace))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool Supported(Track track)
        {
            return sources.Any(source =>
                string.Equals(source.ProviderId, track.Id.ProviderId, StringComparison.OrdinalIgnoreCase) ||
                track.ExternalIds.Any(id => source.AcceptedIdSchemes.Any(scheme => scheme.Accepts(id))));
        }

        var results = new List<SearchResult>();
        foreach (var provider in registry.Providers.Where(provider => provider is ISearchProvider))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scopes = provider is ISearchNamespaceProvider scoped
                ? scoped.SearchNamespaces.Where(namespaces.Contains).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase).Cast<string?>().ToArray()
                : [null];
            if (scopes.Length == 0) continue;
            var found = await Task.WhenAll(scopes.Select(scope =>
                SearchAsync(keyword, provider.Id, scope, cancellationToken)));
            var tracks = found.SelectMany(batch => batch).SelectMany(result => result.Tracks)
                .Where(Supported).DistinctBy(track => track.Id).ToArray();
            results.Add(new SearchResult(provider.Id, tracks));
        }

        return results;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string keyword,
        string? providerId,
        string? sourceNamespace,
        CancellationToken cancellationToken = default)
    {
        sourceNamespace = string.IsNullOrWhiteSpace(sourceNamespace)
            ? null
            : sourceNamespace.Trim();
        var providers = providerId is null
            ? registry.Providers
            : registry.Get(providerId) is { } selectedProvider
                ? [selectedProvider]
                : throw new ProviderException($"Provider '{providerId}' was not found.");
        var results = new List<SearchResult>();

        foreach (var searchProvider in providers.OfType<ISearchProvider>())
            try
            {
                var result = await SearchTracksAsync(searchProvider,
                    new SearchRequest(keyword, SourceNamespace: sourceNamespace), cancellationToken);
                results.Add(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not ProviderException)
            {
                throw new ProviderException($"Search failed for provider '{((IMusicProvider)searchProvider).Id}'.",
                    exception);
            }

        return results;
    }

    private static async Task<SearchResult> SearchTracksAsync(ISearchProvider provider, SearchRequest request,
        CancellationToken cancellationToken)
    {
        var result = await provider.SearchAsync(request, cancellationToken);
        return request.SourceNamespace is null ? result : result with
        {
            Tracks = result.Tracks.Where(track => track.ExternalIds.Any(id =>
                string.Equals(id.Namespace, request.SourceNamespace, StringComparison.OrdinalIgnoreCase))).ToArray()
        };
    }
}
