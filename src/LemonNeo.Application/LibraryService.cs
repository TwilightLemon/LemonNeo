using LemonNeo.Core;

namespace LemonNeo.Application;

public sealed class LibraryService(ILibraryRepository repository)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MusicLibrary Current { get; private set; } = new();

    public async Task<MusicLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Current = await repository.LoadAsync(cancellationToken);
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await repository.SaveAsync(Current, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Replace(MusicLibrary library) => Current = library;

    public Track? FindTrack(MusicItemId id) => Current.Tracks.FirstOrDefault(track => track.Id == id);

    public void UpsertTrack(Track track)
    {
        var index = Current.Tracks.FindIndex(candidate => candidate.Id == track.Id);
        if (index < 0)
        {
            Current.Tracks.Add(track);
        }
        else
        {
            Current.Tracks[index] = track;
        }

        Touch();
    }

    public void Touch() => Current.UpdatedAt = DateTimeOffset.UtcNow;
}
