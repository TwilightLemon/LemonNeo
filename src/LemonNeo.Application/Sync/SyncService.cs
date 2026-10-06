using LemonNeo.Abstractions.Sync;
using LemonNeo.Application.Library;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Library;

namespace LemonNeo.Application.Sync;

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
        var result = await FetchRemoteAsync(providerId, cancellationToken);
        return await ApplyRemoteAsync(providerId, result, cancellationToken);
    }

    /// <summary>Fetches the remote snapshot without replacing the local library.</summary>
    public async Task<SyncResult> FetchRemoteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        try { return await GetRequired(providerId).PullAsync(cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException and not SyncException)
        {
            throw new SyncException($"Sync pull failed for provider '{providerId}'.", exception);
        }
    }

    public async Task<PullResult> ApplyRemoteAsync(
        string providerId,
        SyncResult result,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            return await PushRemoteAsync(providerId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not SyncException)
        {
            throw new SyncException($"Sync push failed for provider '{providerId}'.", exception);
        }
    }

    /// <summary>Pushes an already saved library without accessing its mutable state.</summary>
    public async Task<SyncResult> PushRemoteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        try
        {
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
