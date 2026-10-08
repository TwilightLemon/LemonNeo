using LemonNeo.Abstractions.Storage;
using LemonNeo.Core.Exceptions;
using LemonNeo.Core.Library;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LemonNeo.Infrastructure.Library;

internal sealed class JsonLibraryRepository(Func<string> getLibraryDirectory) : ILibraryRepository
{
    private const string IndexFileName = "library.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        var root = GetRoot();
        var indexPath = Path.Combine(root, IndexFileName);
        if (File.Exists(indexPath)) return Task.CompletedTask;
        return SaveAsync(new LibrarySaveSnapshot
        {
            FormatVersion = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            PlaylistIds = [],
            Mirrors = [],
            ChangedPlaylists = [],
            DeletedPlaylistIds = [],
            WriteIndex = true
        }, cancellationToken);
    }

    public async Task<MusicLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        var root = GetRoot();
        var indexPath = Path.Combine(root, IndexFileName);
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
                var path = ResolveLibraryPath(root, relativePath);
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
            throw new LibraryException($"Could not load library from '{root}'.", exception);
        }
    }

    public async Task SaveAsync(LibrarySaveSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var root = GetRoot();
        try
        {
            var playlistsDirectory = Path.Combine(root, "playlists");
            Directory.CreateDirectory(playlistsDirectory);

            foreach (var playlist in snapshot.ChangedPlaylists.OrderBy(item => item.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.Combine("playlists", $"{playlist.Id:N}.json").Replace('\\', '/');
                await WriteAtomicAsync(
                    ResolveLibraryPath(root, relativePath),
                    StablePlaylist(playlist),
                    cancellationToken);
            }

            if (snapshot.WriteIndex)
            {
                var index = new LibraryIndex
                {
                    FormatVersion = snapshot.FormatVersion,
                    UpdatedAt = snapshot.UpdatedAt,
                    PlaylistFiles = snapshot.PlaylistIds.Order()
                        .Select(id => $"playlists/{id:N}.json").ToArray(),
                    Mirrors = snapshot.Mirrors.OrderBy(mirror => mirror.PlaylistId).ToArray()
                };
                await WriteAtomicAsync(Path.Combine(root, IndexFileName), index, cancellationToken);
            }

            // Publish the new index before removing playlist files it no longer references.
            foreach (var id in snapshot.DeletedPlaylistIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ResolveLibraryPath(root, $"playlists/{id:N}.json");
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new LibraryException($"Could not save library to '{root}'.", exception);
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

    private string GetRoot() => Path.TrimEndingDirectorySeparator(Path.GetFullPath(getLibraryDirectory()));

    private static string ResolveLibraryPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new LibraryException($"Library contains an absolute path: {relativePath}");
        }

        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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
