using LemonNeo.Abstractions.Providers;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Music;

namespace LemonNeo.Application.Providers;

public sealed class SearchService(IProviderRegistry registry, IPlaybackSourceProviderRegistry playbackSources)
{
    /// <summary>Searches every provider within the source capabilities of the loaded playback providers.</summary>
    public async Task<IReadOnlyList<SearchResult>> SearchForPlaybackAsync(
        string keyword,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = playbackSources.Sources.ToArray();
        if (sources.Length == 0) return [];
        var namespaces = sources.SelectMany(source => source.AcceptedIdSchemes.Select(scheme => scheme.Namespace))
            .Concat(sources.Select(source => source.ProviderId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Supported(Track track) => sources.Any(source =>
            string.Equals(source.ProviderId, track.Id.ProviderId, StringComparison.OrdinalIgnoreCase) ||
            track.ExternalIds.Any(id => source.AcceptedIdSchemes.Any(scheme => scheme.Accepts(id))));
        var results = new List<SearchResult>();
        foreach (var provider in registry.Providers.Where(provider => provider is ISearchProvider))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string?[] scopes = provider is ISearchNamespaceProvider scoped
                ? scoped.SearchNamespaces.Where(namespaces.Contains).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase).Cast<string?>().ToArray()
                : [null];
            if (scopes.Length == 0) continue;
            var found = await Task.WhenAll(scopes.Select(scope => SearchAsync(keyword, provider.Id, scope, cancellationToken)));
            var tracks = found.SelectMany(batch => batch).SelectMany(result => result.Tracks)
                .Where(Supported).DistinctBy(track => track.Id).ToArray();
            results.Add(new(provider.Id, tracks));
        }
        return results;
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string keyword,
        string? providerId = null,
        CancellationToken cancellationToken = default) =>
        SearchAsync(keyword, providerId, null, cancellationToken);

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
        {
            try
            {
                var result = await searchProvider.SearchAsync(
                    new SearchRequest(keyword, SourceNamespace: sourceNamespace),
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(sourceNamespace))
                {
                    result = result with
                    {
                        Tracks = result.Tracks
                            .Where(track => track.ExternalIds.Any(id =>
                                string.Equals(
                                    id.Namespace,
                                    sourceNamespace,
                                    StringComparison.OrdinalIgnoreCase)))
                            .ToArray()
                    };
                }

                results.Add(result);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not ProviderException)
            {
                throw new ProviderException($"Search failed for provider '{((IMusicProvider)searchProvider).Id}'.", exception);
            }
        }

        return results;
    }
}
