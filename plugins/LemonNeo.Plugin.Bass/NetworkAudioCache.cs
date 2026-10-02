using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LemonNeo.Core;
using Microsoft.Extensions.Logging;
using Un4seen.Bass;
using BassApi = Un4seen.Bass.Bass;

namespace LemonNeo.Plugin.Bass;

internal sealed class NetworkAudioCache(string directory, ILogger logger)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(directory), "Music");

    public string GetPath(Track track, HttpPlaybackSource source)
    {
        // Without an explicit provider identity, preserve the entire URL and
        // headers: different encodings, qualities, or requests must not collide.
        var identity = string.IsNullOrWhiteSpace(source.CacheKey)
            ? JsonSerializer.Serialize(new
            {
                track.Id,
                Url = source.Uri.AbsoluteUri,
                Headers = source.Headers.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(header => new[] { header.Key.ToUpperInvariant(), header.Value }).ToArray()
            })
            : JsonSerializer.Serialize(new { track.Id, source.CacheKey });
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        // Retain known extensions for decoders that use OS codec discovery.
        var extension = Path.GetExtension(source.Uri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".mp3" or ".mp2" or ".mp1" or ".flac" or ".wav" or ".ogg" or ".aiff" or
            ".aif" or ".m4a" or ".mp4" or ".aac" or ".wma" or ".opus")) extension = ".audio";
        return Path.Combine(_directory, key + extension);
    }

    public BassDownloadCache? Start(string path)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            return new BassDownloadCache(path, logger);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write the audio cache. Playing the network stream without disk caching.");
            return null;
        }
    }

    public void RemoveInvalid(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove an invalid audio cache file {Path}.", path);
        }
    }
}

/// <summary>
/// Owns the rooted native callback and its unique temporary file. The callback
/// never touches the player's lock or frees a BASS stream, avoiding deadlocks
/// when StreamFree waits for the native download thread to exit.
/// </summary>
internal sealed class BassDownloadCache : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly ILogger _logger;
    private FileStream? _file;
    private byte[] _buffer = [];
    private int _stream;
    private long _written;
    private bool _ended;
    private bool _disposed;

    public BassDownloadCache(string path, ILogger logger)
    {
        _path = path;
        _temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".part";
        _logger = logger;
        _file = new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            65536, FileOptions.SequentialScan);
        DownloadCallback = Receive;
    }

    public DOWNLOADPROC DownloadCallback { get; }

    public void SetStream(int stream)
    {
        lock (_gate)
        {
            _stream = stream;
            if (BassApi.BASS_StreamGetFilePosition(stream, BASSStreamFilePosition.BASS_FILEPOS_SIZE) <= 0)
            {
                // Do not let an unbounded/live stream grow a temporary file.
                AbandonSafely();
                return;
            }
            // BASS can finish downloading before StreamCreateURL returns.
            if (_ended) CompleteSafely();
        }
    }

    private void Receive(nint buffer, int length, nint user)
    {
        // Exceptions must never unwind through the unmanaged callback boundary.
        try { ReceiveCore(buffer, length); }
        catch (Exception) { } // Includes failures from a host logging sink.
    }

    private void ReceiveCore(nint buffer, int length)
    {
        lock (_gate)
        {
            if (_disposed || _file is null) return;
            try
            {
                if (buffer == nint.Zero)
                {
                    _ended = true;
                    CompleteSafely();
                }
                else if (length > 0)
                {
                    if (_buffer.Length < length) _buffer = new byte[length];
                    Marshal.Copy(buffer, _buffer, 0, length);
                    _file.Write(_buffer, 0, length);
                    _written += length;
                }
                // length == 0 with a non-null pointer is status/metadata, not audio.
            }
            catch (Exception exception)
            {
                AbandonSafely();
                _logger.LogWarning(exception, "Could not save downloaded audio to the cache.");
            }
        }
    }

    private void CompleteSafely()
    {
        if (_stream == 0 || _file is null || _disposed) return;
        try
        {
            var expectedSize = BassApi.BASS_StreamGetFilePosition(_stream, BASSStreamFilePosition.BASS_FILEPOS_SIZE);
            // A disconnect also ends a download. Only publish a verified,
            // complete finite file; unknown-length/live streams are not cached.
            if (expectedSize <= 0 || _written != expectedSize)
            {
                AbandonSafely();
                return;
            }
            _file.Flush();
            _file.Dispose();
            _file = null;
            if (File.Exists(_path)) File.Delete(_temporaryPath);
            else File.Move(_temporaryPath, _path);
            _logger.LogInformation("Cached downloaded audio at {Path}.", _path);
        }
        catch (Exception exception)
        {
            AbandonSafely();
            _logger.LogWarning(exception, "Could not finalize the audio cache file.");
        }
    }

    private void AbandonSafely()
    {
        try { _file?.Dispose(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        _file = null;
        try { File.Delete(_temporaryPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            AbandonSafely();
        }
    }
}
