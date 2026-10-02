using System.Text.Json;
using System.Text.Json.Serialization;
using LemonNeo.Core;

namespace LemonNeo.Persistence;

public sealed class JsonLibraryRepository(string libraryDirectory) : ILibraryRepository
{
    private const string IndexFileName = "library.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _root = Path.GetFullPath(libraryDirectory);

    public async Task<MusicLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        var indexPath = Path.Combine(_root, IndexFileName);
        if (!File.Exists(indexPath))
        {
            return new MusicLibrary();
        }

        try
        {
            var index = await ReadAsync<LibraryIndex>(indexPath, cancellationToken)
                ?? throw new LibraryException($"Library index is empty: {indexPath}");
            var playlists = new List<Playlist>();

            foreach (var relativePath in index.PlaylistFiles.Order(StringComparer.Ordinal))
            {
                var path = ResolveLibraryPath(relativePath);
                var playlist = await ReadAsync<Playlist>(path, cancellationToken)
                    ?? throw new LibraryException($"Playlist file is empty: {path}");
                playlists.Add(playlist);
            }

            return new MusicLibrary
            {
                FormatVersion = index.FormatVersion,
                UpdatedAt = index.UpdatedAt,
                Playlists = playlists.OrderBy(playlist => playlist.Id).ToList(),
                Mirrors = index.Mirrors.OrderBy(mirror => mirror.PlaylistId).ToList()
            };
        }
        catch (LibraryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new LibraryException($"Could not load library from '{_root}'.", exception);
        }
    }

    public async Task SaveAsync(MusicLibrary library, CancellationToken cancellationToken = default)
    {
        try
        {
            var playlistsDirectory = Path.Combine(_root, "playlists");
            Directory.CreateDirectory(playlistsDirectory);

            var playlistFiles = new List<string>();
            foreach (var playlist in library.Playlists.OrderBy(item => item.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.Combine("playlists", $"{playlist.Id:N}.json").Replace('\\', '/');
                playlistFiles.Add(relativePath);
                await WriteAtomicAsync(
                    ResolveLibraryPath(relativePath),
                    StablePlaylist(playlist),
                    cancellationToken);
            }

            var index = new LibraryIndex
            {
                FormatVersion = library.FormatVersion,
                UpdatedAt = library.UpdatedAt,
                PlaylistFiles = playlistFiles,
                Mirrors = library.Mirrors.OrderBy(mirror => mirror.PlaylistId).ToArray()
            };
            await WriteAtomicAsync(Path.Combine(_root, IndexFileName), index, cancellationToken);

            // Publish the new index before removing playlist files it no longer references.
            var retainedIds = library.Playlists.Select(playlist => playlist.Id).ToHashSet();
            foreach (var path in Directory.EnumerateFiles(playlistsDirectory, "*.json"))
            {
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) &&
                    !retainedIds.Contains(id))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new LibraryException($"Could not save library to '{_root}'.", exception);
        }
    }

    private static Playlist StablePlaylist(Playlist playlist) => new()
    {
        Id = playlist.Id,
        Name = playlist.Name,
        Description = playlist.Description,
        CreatedAt = playlist.CreatedAt,
        UpdatedAt = playlist.UpdatedAt,
        Items = playlist.Items
            .OrderBy(item => decimal.Parse(item.SortKey, System.Globalization.CultureInfo.InvariantCulture))
            .ThenBy(item => item.Id)
            .ToList()
    };

    private string ResolveLibraryPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new LibraryException($"Library contains an absolute path: {relativePath}");
        }

        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new LibraryException($"Library path escapes its root: {relativePath}");
        }

        return path;
    }

    private static async Task<T?> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
    }

    private static async Task WriteAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed record LibraryIndex
    {
        public int FormatVersion { get; init; } = 1;
        public DateTimeOffset UpdatedAt { get; init; }
        public IReadOnlyList<string> PlaylistFiles { get; init; } = [];
        public IReadOnlyList<PlaylistMirror> Mirrors { get; init; } = [];
    }
}
