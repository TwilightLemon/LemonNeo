using LemonNeo.Abstractions.Storage;
using LemonNeo.Core.Library;

namespace LemonNeo.Application.Library;

public sealed class LibraryService(ILibraryRepository repository)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _changesGate = new();
    private readonly Dictionary<Guid, long> _changedPlaylists = [];
    private readonly Dictionary<Guid, long> _deletedPlaylists = [];
    private long _changeVersion;
    private long _indexVersion;

    public MusicLibrary Current { get; private set; } = new();

    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default) =>
        repository.EnsureCreatedAsync(cancellationToken);

    public async Task<MusicLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var loaded = await repository.LoadAsync(cancellationToken);
            lock (_changesGate)
            {
                Current = loaded;
                _changedPlaylists.Clear();
                _deletedPlaylists.Clear();
                _indexVersion = 0;
                return Current;
            }
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
            LibrarySaveSnapshot snapshot;
            Dictionary<Guid, long> changedVersions;
            Dictionary<Guid, long> deletedVersions;
            long indexVersion;
            lock (_changesGate)
            {
                if (_changedPlaylists.Count == 0 && _deletedPlaylists.Count == 0 && _indexVersion == 0)
                {
                    return;
                }

                changedVersions = new Dictionary<Guid, long>(_changedPlaylists);
                deletedVersions = new Dictionary<Guid, long>(_deletedPlaylists);
                indexVersion = _indexVersion;
                var changedIds = changedVersions.Keys.ToHashSet();
                snapshot = new LibrarySaveSnapshot
                {
                    FormatVersion = Current.FormatVersion,
                    UpdatedAt = Current.UpdatedAt,
                    PlaylistIds = Current.Playlists.Select(playlist => playlist.Id).Order().ToArray(),
                    Mirrors = Current.Mirrors.OrderBy(mirror => mirror.PlaylistId).ToArray(),
                    ChangedPlaylists = Current.Playlists
                        .Where(playlist => changedIds.Contains(playlist.Id))
                        .OrderBy(playlist => playlist.Id)
                        .Select(Clone)
                        .ToArray(),
                    DeletedPlaylistIds = deletedVersions.Keys.Order().ToArray(),
                    WriteIndex = indexVersion != 0
                };
            }

            await repository.SaveAsync(snapshot, cancellationToken);

            lock (_changesGate)
            {
                foreach (var (id, version) in changedVersions)
                {
                    if (_changedPlaylists.GetValueOrDefault(id) == version) _changedPlaylists.Remove(id);
                }
                foreach (var (id, version) in deletedVersions)
                {
                    if (_deletedPlaylists.GetValueOrDefault(id) == version) _deletedPlaylists.Remove(id);
                }
                if (_indexVersion == indexVersion) _indexVersion = 0;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Replace(MusicLibrary library)
    {
        lock (_changesGate)
        {
            var previousIds = Current.Playlists.Select(playlist => playlist.Id).ToHashSet();
            Current = library;
            var version = ++_changeVersion;
            _changedPlaylists.Clear();
            _deletedPlaylists.Clear();
            foreach (var playlist in library.Playlists) _changedPlaylists[playlist.Id] = version;
            foreach (var id in previousIds.Except(library.Playlists.Select(playlist => playlist.Id)))
                _deletedPlaylists[id] = version;
            _indexVersion = version;
        }
    }

    public void Touch(Guid playlistId)
    {
        lock (_changesGate)
        {
            Current.UpdatedAt = DateTimeOffset.UtcNow;
            var version = ++_changeVersion;
            _deletedPlaylists.Remove(playlistId);
            _changedPlaylists[playlistId] = version;
            _indexVersion = version;
        }
    }

    public void TouchDeleted(Guid playlistId)
    {
        lock (_changesGate)
        {
            Current.UpdatedAt = DateTimeOffset.UtcNow;
            var version = ++_changeVersion;
            _changedPlaylists.Remove(playlistId);
            _deletedPlaylists[playlistId] = version;
            _indexVersion = version;
        }
    }

    /// <summary>Marks every playlist changed when a caller cannot identify a narrower scope.</summary>
    public void Touch()
    {
        lock (_changesGate)
        {
            Current.UpdatedAt = DateTimeOffset.UtcNow;
            var version = ++_changeVersion;
            foreach (var playlist in Current.Playlists) _changedPlaylists[playlist.Id] = version;
            _indexVersion = version;
        }
    }

    private static Playlist Clone(Playlist playlist) => new()
    {
        Id = playlist.Id,
        Name = playlist.Name,
        Description = playlist.Description,
        CreatedAt = playlist.CreatedAt,
        UpdatedAt = playlist.UpdatedAt,
        Items = playlist.Items.Select(item => new PlaylistItem
        {
            Id = item.Id,
            SortKey = item.SortKey,
            AddedAt = item.AddedAt,
            Track = item.Track with
            {
                Artists = item.Track.Artists.ToArray(),
                Album = item.Track.Album is { } album ? album with { } : null,
                ExternalIds = item.Track.ExternalIds.ToArray()
            }
        }).ToList()
    };
}
