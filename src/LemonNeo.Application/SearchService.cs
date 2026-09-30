using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Application;

public sealed class SearchService(IProviderRegistry registry, LibraryService library)
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
                var result = await searchProvider.SearchAsync(new SearchRequest(keyword), cancellationToken);
                if (searchProvider is IFolderImportProvider && searchProvider is IMusicProvider musicProvider)
                {
                    var persistedTracks = library.Current.Tracks
                        .Where(track => string.Equals(
                                            track.Id.ProviderId,
                                            musicProvider.Id,
                                            StringComparison.OrdinalIgnoreCase) &&
                                        Matches(track, keyword));
                    result = result with
                    {
                        Tracks = result.Tracks.Concat(persistedTracks)
                            .GroupBy(track => track.Id)
                            .Select(group => group.First())
                            .OrderBy(track => track.Title, StringComparer.OrdinalIgnoreCase)
                            .Take(50)
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

    private static bool Matches(Track track, string keyword) =>
        string.IsNullOrWhiteSpace(keyword) ||
        track.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
        track.Id.RemoteId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
        track.Artists.Any(artist => artist.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    public async Task<Track?> FindTrackAsync(
        string providerId,
        string trackId,
        CancellationToken cancellationToken = default)
    {
        var results = await SearchAsync(trackId, providerId, cancellationToken);
        return results.SelectMany(result => result.Tracks)
            .FirstOrDefault(track => string.Equals(track.Id.RemoteId, trackId, StringComparison.OrdinalIgnoreCase));
    }
}
