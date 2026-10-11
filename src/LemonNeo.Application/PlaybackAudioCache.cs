using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Application;

/// <summary>Audio indexed by one namespace-kind-id identity and its actual quality.</summary>
public sealed class PlaybackAudioCache(Func<string> getCacheDirectory, ILogger<PlaybackAudioCache> logger)
{
    private const int FormatVersion = 1;
    public string DirectoryPath => Path.Combine(Path.GetFullPath(getCacheDirectory()), "Music");

    public async Task<FilePlaybackSource?> TryGetAsync(
        ExternalMusicId identifier, AudioQuality quality, CancellationToken cancellationToken = default)
    {
        var directory = DirectoryPath;
        var entry = await TryGetEntryAsync(directory, MusicCacheIdentity.Normalize(identifier), quality,
            cancellationToken);
        return entry is null
            ? null
            : new FilePlaybackSource(Path.Combine(directory, entry.FileName))
            {
                Quality = entry.Quality,
                BitrateKbps = entry.BitrateKbps
            };
    }

    private async Task<CacheEntry?> TryGetEntryAsync(
        string directory, ExternalMusicId identifier, AudioQuality quality, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var indexPath = GetIndexPath(directory, identifier, quality);
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

            var path = Path.Combine(directory, entry.FileName);
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

    public IPlaybackDownloadSink? Start(ExternalMusicId identifier, HttpPlaybackSource source,
        bool awaitDecoderInfo = false)
    {
        // Unknown quality must never be cached under the requested quality.
        var quality = source.Quality is { } supplied && supplied != AudioQuality.Automatic && Enum.IsDefined(supplied)
            ? source.Quality
            : null;
        if (quality is null && !awaitDecoderInfo) return null;
        identifier = MusicCacheIdentity.Normalize(identifier);
        var directory = DirectoryPath;
        try
        {
            Directory.CreateDirectory(directory);
            return new DownloadWriter(directory, identifier, quality, source.BitrateKbps,
                actual => GetIndexPath(directory, identifier, actual),
                AudioExtension(Path.GetExtension(source.Uri.AbsolutePath)), logger, awaitDecoderInfo);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write the audio cache. Streaming playback continues.");
            return null;
        }
    }

    /// <summary>Releases a download for publication once the host has obtained the decoder's measurements.</summary>
    public void SetAudioInfo(IPlaybackDownloadSink download, AudioStreamInfo? info)
    {
        if (download is DownloadWriter writer) writer.SetAudioInfo(info);
    }

    /// <summary>Corrects a previously verified cached resource's metadata without copying audio or creating aliases.</summary>
    public async Task RefreshAudioInfoAsync(ExternalMusicId identifier, FilePlaybackSource source,
        AudioStreamInfo? info,
        CancellationToken cancellationToken = default)
    {
        if (info is null || source.Quality is not { } previous) return;
        var quality = AudioQualities.Resolve(info, previous);
        var bitrate = info.BitrateKbps is > 0 ? info.BitrateKbps : source.BitrateKbps;
        if (quality == previous && bitrate == source.BitrateKbps) return;
        identifier = MusicCacheIdentity.Normalize(identifier);
        var directory = DirectoryPath;
        var previousPath = GetIndexPath(directory, identifier, previous);
        string? temporaryPath = null;
        try
        {
            // Discovery already checked the audio digest; only read the small index again.
            var entry = JsonSerializer.Deserialize<CacheEntry>(
                await File.ReadAllTextAsync(previousPath, cancellationToken));
            if (entry is null || entry.FormatVersion != FormatVersion || entry.Identifier != identifier ||
                entry.Quality != previous || Path.Combine(directory, entry.FileName) != source.Path) return;
            if (quality is { } actual)
            {
                temporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json.part");
                await File.WriteAllTextAsync(temporaryPath,
                    JsonSerializer.Serialize(entry with { Quality = actual, BitrateKbps = bitrate }),
                    cancellationToken);
                File.Move(temporaryPath, GetIndexPath(directory, identifier, actual), true);
            }

            if (quality != previous) RemoveIndex(previousPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(exception, "Could not update cached audio measurements.");
        }
        finally
        {
            if (temporaryPath is not null) RemoveIndex(temporaryPath);
        }
    }

    public void Invalidate(ExternalMusicId identifier, AudioQuality quality)
    {
        var directory = DirectoryPath;
        RemoveIndex(GetIndexPath(directory, identifier, quality));
    }

    private static string GetIndexPath(string directory, ExternalMusicId identifier, AudioQuality quality)
    {
        var identity = JsonSerializer.Serialize(new
            { MusicId = MusicCacheIdentity.GetKey(identifier), Quality = quality });
        return Path.Combine(directory,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    private void RemoveIndex(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove an invalid audio cache index {Path}.", path);
        }
    }

    private static bool IsDigest(string? value)
    {
        return value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string AudioExtension(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".mp3" or ".mp2" or ".mp1" or ".flac" or ".wav" or ".ogg" or ".aiff" or
                ".aif" or ".m4a" or ".mp4" or ".aac" or ".wma" or ".opus" => extension.ToLowerInvariant(),
            _ => ".audio"
        };
    }

    private sealed record CacheEntry(
        int FormatVersion,
        ExternalMusicId Identifier,
        AudioQuality Quality,
        int? BitrateKbps,
        string FileName,
        long Length,
        string Sha256);

    private sealed class DownloadWriter : IPlaybackDownloadSink
    {
        private readonly string _directory;
        private readonly string _extension;
        private readonly object _gate = new();
        private readonly Func<AudioQuality, string> _getIndexPath;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly ExternalMusicId _identifier;
        private readonly ILogger _logger;
        private readonly string _temporaryIndexPath;
        private readonly string _temporaryPath;
        private int? _bitrateKbps;
        private bool _closed;
        private bool _ended;
        private long? _expectedLength;
        private FileStream? _file;
        private bool _metadataReady;
        private AudioQuality? _quality;
        private long _written;

        public DownloadWriter(string directory, ExternalMusicId identifier, AudioQuality? quality, int? bitrateKbps,
            Func<AudioQuality, string> getIndexPath, string extension, ILogger logger, bool awaitDecoderInfo)
        {
            _directory = directory;
            _identifier = identifier;
            _quality = quality;
            _bitrateKbps = bitrateKbps;
            _getIndexPath = getIndexPath;
            _metadataReady = !awaitDecoderInfo;
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
                if (_ended)
                {
                    Close();
                    return;
                }

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
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                      or ObjectDisposedException)
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

        public void Abort()
        {
            Dispose();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                Close();
            }
        }

        public void SetAudioInfo(AudioStreamInfo? info)
        {
            lock (_gate)
            {
                if (_closed || _metadataReady) return;
                _quality = AudioQualities.Resolve(info, _quality);
                if (info?.BitrateKbps is > 0) _bitrateKbps = info.BitrateKbps;
                if (_quality is null)
                {
                    Close();
                    return;
                }

                _metadataReady = true;
                if (_ended && _expectedLength is not null) Publish();
            }
        }

        private void Publish()
        {
            // Fast downloads can finish while the decoder is still opening the stream.
            if (!_metadataReady || _quality is not { } quality) return;
            if (_written != _expectedLength)
            {
                Close();
                return;
            }

            try
            {
                var digest = Convert.ToHexStringLower(_hash.GetHashAndReset());
                var fileName = digest + _extension;
                _file!.Flush(true);
                _file.Dispose();
                _file = null;
                File.Move(_temporaryPath, Path.Combine(_directory, fileName), true);
                var entry = JsonSerializer.Serialize(new CacheEntry(FormatVersion, _identifier, quality, _bitrateKbps,
                    fileName, _written, digest));
                File.WriteAllText(_temporaryIndexPath, entry);
                File.Move(_temporaryIndexPath, _getIndexPath(quality), true);
                _logger.LogInformation("Cached downloaded audio at {Path}.", Path.Combine(_directory, fileName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not finalize the audio cache. Streaming playback continues.");
            }
            finally
            {
                Close();
            }
        }

        private void Close()
        {
            if (_closed) return;
            _closed = true;
            try
            {
                _file?.Dispose();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            _file = null;
            _hash.Dispose();
            DeleteTemporary(_temporaryPath);
            DeleteTemporary(_temporaryIndexPath);
        }

        private static void DeleteTemporary(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}