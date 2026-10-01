using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed class SearchService(IProviderRegistry registry)
{
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string keyword,
        string? providerId = null,
        CancellationToken cancellationToken = default)
    {
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
                results.Add(await searchProvider.SearchAsync(new SearchRequest(keyword), cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not ProviderException)
            {
                throw new ProviderException($"Search failed for provider '{((IMusicProvider)searchProvider).Id}'.", exception);
            }
        }

        return results;
    }
}
