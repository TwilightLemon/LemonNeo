using LemonNeo.Abstractions.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application;

/// <summary>Audio indexed by one namespace-kind-id identity and its actual quality.</summary>
public sealed class PlaybackAudioCache(string cacheDirectory, ILogger<PlaybackAudioCache> logger)
{
    private const int FormatVersion = 1;
    private readonly string _directory = Path.Combine(Path.GetFullPath(cacheDirectory), "Music");

    public async Task<FilePlaybackSource?> TryGetAsync(
        ExternalMusicId identifier, AudioQuality quality, CancellationToken cancellationToken = default)
    {
        var entry = await TryGetEntryAsync(MusicCacheIdentity.Normalize(identifier), quality, cancellationToken);
        return entry is null ? null : new FilePlaybackSource(Path.Combine(_directory, entry.FileName))
        {
            Quality = entry.Quality,
            BitrateKbps = entry.BitrateKbps
        };
    }

    private async Task<CacheEntry?> TryGetEntryAsync(
        ExternalMusicId identifier, AudioQuality quality, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var indexPath = GetIndexPath(identifier, quality);
        if (!File.Exists(indexPath)) return null;
        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(
                await File.ReadAllTextAsync(indexPath, cancellationToken));
            if (entry is null || entry.FormatVersion != FormatVersion || entry.Length <= 0 ||
                entry.Identifier != identifier || entry.Quality != quality || quality == AudioQuality.Automatic ||
                !IsDigest(entry.Sha256) || string.IsNullOrEmpty(entry.FileName) ||
                entry.FileName != entry.Sha256 + AudioExtension(Path.GetExtension(entry.FileName)))
            {
                RemoveIndex(indexPath);
                return null;
            }

            var path = Path.Combine(_directory, entry.FileName);
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != entry.Length ||
                Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken)) != entry.Sha256)
            {
                RemoveIndex(indexPath);
                return null;
            }
            logger.LogInformation("Found host-cached audio at {Path}.", path);
            return entry;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Could not read the audio cache; resolving a fresh playback source.");
            RemoveIndex(indexPath);
            return null;
        }
    }

    public IPlaybackDownloadSink? Start(ExternalMusicId identifier, HttpPlaybackSource source)
    {
        // Unknown quality must never be cached under the requested quality.
        if (source.Quality is not { } quality || quality == AudioQuality.Automatic || !Enum.IsDefined(quality)) return null;
        identifier = MusicCacheIdentity.Normalize(identifier);
        try
        {
            Directory.CreateDirectory(_directory);
            return new DownloadWriter(_directory, identifier, quality, source.BitrateKbps,
                GetIndexPath(identifier, quality), AudioExtension(Path.GetExtension(source.Uri.AbsolutePath)), logger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write the audio cache. Streaming playback continues.");
            return null;
        }
    }

    public void Invalidate(ExternalMusicId identifier, AudioQuality quality) =>
        RemoveIndex(GetIndexPath(identifier, quality));

    private string GetIndexPath(ExternalMusicId identifier, AudioQuality quality)
    {
        var identity = JsonSerializer.Serialize(new { MusicId = MusicCacheIdentity.GetKey(identifier), Quality = quality });
        return Path.Combine(_directory,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    private void RemoveIndex(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove an invalid audio cache index {Path}.", path);
        }
    }

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string AudioExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp3" or ".mp2" or ".mp1" or ".flac" or ".wav" or ".ogg" or ".aiff" or
        ".aif" or ".m4a" or ".mp4" or ".aac" or ".wma" or ".opus" => extension.ToLowerInvariant(),
        _ => ".audio"
    };

    private sealed record CacheEntry(int FormatVersion, ExternalMusicId Identifier, AudioQuality Quality, int? BitrateKbps,
        string FileName, long Length, string Sha256);

    private sealed class DownloadWriter : IPlaybackDownloadSink
    {
        private readonly object _gate = new();
        private readonly string _directory;
        private readonly ExternalMusicId _identifier;
        private readonly AudioQuality _quality;
        private readonly int? _bitrateKbps;
        private readonly string _indexPath;
        private readonly string _extension;
        private readonly string _temporaryPath;
        private readonly string _temporaryIndexPath;
        private readonly ILogger _logger;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private FileStream? _file;
        private long _written;
        private long? _expectedLength;
        private bool _ended;
        private bool _closed;

        public DownloadWriter(string directory, ExternalMusicId identifier, AudioQuality quality, int? bitrateKbps,
            string indexPath, string extension, ILogger logger)
        {
            _directory = directory;
            _identifier = identifier;
            _quality = quality;
            _bitrateKbps = bitrateKbps;
            _indexPath = indexPath;
            _extension = extension;
            _logger = logger;
            var temporaryId = Guid.NewGuid().ToString("N");
            _temporaryPath = Path.Combine(directory, temporaryId + ".part");
            _temporaryIndexPath = Path.Combine(directory, temporaryId + ".json.part");
            try
            {
                _file = new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.Read, 65536, FileOptions.SequentialScan);
            }
            catch
            {
                _hash.Dispose();
                throw;
            }
        }

        public void SetExpectedLength(long? length)
        {
            lock (_gate)
            {
                if (_closed) return;
                if (length is null or <= 0 || (_expectedLength is not null && _expectedLength != length))
                {
                    Close();
                    return;
                }
                _expectedLength = length;
                if (_written > length) Close();
                else if (_ended) Publish();
            }
        }

        public void Write(ReadOnlySpan<byte> chunk)
        {
            lock (_gate)
            {
                if (_closed) return;
                if (_ended) { Close(); return; }
                try
                {
                    if (_expectedLength is { } expected && chunk.Length > expected - _written)
                    {
                        Close();
                        return;
                    }
                    _file!.Write(chunk);
                    _hash.AppendData(chunk);
                    _written += chunk.Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    Close();
                    _logger.LogWarning(exception, "Could not save downloaded audio. Streaming playback continues.");
                }
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_closed) return;
                _ended = true;
                if (_expectedLength is not null) Publish();
            }
        }

        private void Publish()
        {
            if (_written != _expectedLength) { Close(); return; }
            try
            {
                var digest = Convert.ToHexStringLower(_hash.GetHashAndReset());
                var fileName = digest + _extension;
                _file!.Flush(flushToDisk: true);
                _file.Dispose();
                _file = null;
                File.Move(_temporaryPath, Path.Combine(_directory, fileName), overwrite: true);
                var entry = JsonSerializer.Serialize(new CacheEntry(FormatVersion, _identifier, _quality, _bitrateKbps, fileName, _written, digest));
                File.WriteAllText(_temporaryIndexPath, entry);
                File.Move(_temporaryIndexPath, _indexPath, overwrite: true);
                _logger.LogInformation("Cached downloaded audio at {Path}.", Path.Combine(_directory, fileName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not finalize the audio cache. Streaming playback continues.");
            }
            finally { Close(); }
        }

        public void Abort() => Dispose();

        public void Dispose()
        {
            lock (_gate) { Close(); }
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            try { _file?.Dispose(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            _file = null;
            _hash.Dispose();
            DeleteTemporary(_temporaryPath);
            DeleteTemporary(_temporaryIndexPath);
        }

        private static void DeleteTemporary(string path)
        {
            try { File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
