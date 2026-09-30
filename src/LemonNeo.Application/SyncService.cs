using LemonNeo.Core;
using LemonNeo.Sync.Abstractions;

namespace LemonNeo.Application;

public sealed record PullResult(SyncResult ProviderResult, MergeResult? MergeResult);

public sealed class SyncService(
    IEnumerable<ILibrarySyncProvider> providers,
    ILibraryMergeService mergeService,
    LibraryService library)
{
    private readonly IReadOnlyDictionary<string, ILibrarySyncProvider> _providers =
        providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> ProviderIds => _providers.Keys.ToArray();

    public Task<SyncStatus> GetStatusAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        GetRequired(providerId).GetStatusAsync(cancellationToken);

    public async Task<PullResult> PullAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await GetRequired(providerId).PullAsync(cancellationToken);
            if (!result.Success || result.RemoteLibrary is null)
            {
                return new PullResult(result, null);
            }

            var merged = mergeService.Merge(
                result.BaseLibrary ?? new MusicLibrary(),
                library.Current,
                result.RemoteLibrary);
            library.Replace(merged.Merged);
            await library.SaveAsync(cancellationToken);
            return new PullResult(result, merged);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not SyncException)
        {
            throw new SyncException($"Sync pull failed for provider '{providerId}'.", exception);
        }
    }

    public async Task<SyncResult> PushAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await library.SaveAsync(cancellationToken);
            return await GetRequired(providerId).PushAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not SyncException)
        {
            throw new SyncException($"Sync push failed for provider '{providerId}'.", exception);
        }
    }

    private ILibrarySyncProvider GetRequired(string providerId) =>
        _providers.GetValueOrDefault(providerId)
        ?? throw new SyncException($"Sync provider '{providerId}' was not found.");
}
