using System.Text.Json;
using System.Text.Json.Serialization;
using LemonNeo.Core;

namespace LemonNeo.Persistence;

public sealed class JsonPlaybackStateRepository(string libraryDirectory) : IPlaybackStateRepository
{
    private const string FileName = "playback.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path = Path.Combine(Path.GetFullPath(libraryDirectory), FileName);

    public async Task<PlaybackQueueState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new PlaybackQueueState();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<PlaybackQueueState>(
                       stream,
                       JsonOptions,
                       cancellationToken)
                   ?? throw new LibraryException($"Playback state is empty: {_path}");
        }
        catch (LibraryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new LibraryException($"Could not load playback state from '{_path}'.", exception);
        }
    }

    public async Task SaveAsync(
        PlaybackQueueState state,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = _path + ".tmp";
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new LibraryException($"Could not save playback state to '{_path}'.", exception);
        }
    }
}
