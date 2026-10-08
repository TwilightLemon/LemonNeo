using LemonNeo.Core.Library;
namespace LemonNeo.Abstractions.Storage;

public interface ILibraryRepository
{
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);
    Task<MusicLibrary> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LibrarySaveSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed record LibrarySaveSnapshot
{
    public required int FormatVersion { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required IReadOnlyList<Guid> PlaylistIds { get; init; }
    public required IReadOnlyList<PlaylistMirror> Mirrors { get; init; }
    public required IReadOnlyList<Playlist> ChangedPlaylists { get; init; }
    public required IReadOnlyList<Guid> DeletedPlaylistIds { get; init; }
    public required bool WriteIndex { get; init; }
}
