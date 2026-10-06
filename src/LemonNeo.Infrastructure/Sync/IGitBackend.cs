namespace LemonNeo.Infrastructure.Sync;

internal interface IGitBackend
{
    Task FetchAsync(CancellationToken cancellationToken = default);
    Task PullAsync(CancellationToken cancellationToken = default);
    Task CommitAsync(string message, CancellationToken cancellationToken = default);
    Task PushAsync(CancellationToken cancellationToken = default);
}
