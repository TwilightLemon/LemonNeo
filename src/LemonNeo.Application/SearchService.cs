using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed class SearchService(IProviderRegistry registry)
{
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
