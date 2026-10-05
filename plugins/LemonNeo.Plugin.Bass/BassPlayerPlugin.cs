using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Un4seen.Bass;
using BassApi = Un4seen.Bass.Bass;

namespace LemonNeo.Plugin.Bass;

public sealed class BassPlayerPlugin : ILemonPlugin, IPlayerProvider
{
    private BassPlayer? _player;

    public PluginDescriptor Descriptor { get; } = new(
        "LemonNeo.Plugin.Bass",
        "BASS Player",
        "1.1.0");

    public string Id => "bass";
    PlayerDescriptor IPlayerProvider.Descriptor { get; } = new(
        "BASS Player",
        "Native BASS decoder and audio output for local files and direct HTTP streams.");
    public int Priority => 100;
    public IPlayer Player => _player ?? throw new InvalidOperationException("The BASS player is not initialized.");
    public bool CanPlay(PlaybackSource source) =>
        source is FilePlaybackSource or HttpPlaybackSource { IsDirectAudio: true };

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _player = new BassPlayer(context.LoggerFactory.CreateLogger<BassPlayer>());
        context.RegisterPlayer(this);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_player is not null)
        {
            await _player.DisposeAsync();
            _player = null;
        }
    }
}

internal sealed class BassPlayer : ISeekablePlayer, IStreamingPlayer, IVolumePlayer, IAsyncDisposable
{
    private const int InvalidStream = 0;
    private readonly object _gate = new();
    private readonly ILogger<BassPlayer> _logger;
    private int _stream;
    private int _flacPlugin;
    private float _volume = 1;
    private bool _disposed;
    private bool _isHttpStream;
    private BassDownloadForwarder? _downloadForwarder;

    public BassPlayer(ILogger<BassPlayer> logger)
    {
        _logger = logger;
        var nativeAsset = GetNativeAsset();
        BassNet.Registration("lemon.app@qq.com", "2X52325160022");
        var initFlags = OperatingSystem.IsWindows()
            ? BASSInit.BASS_DEVICE_CPSPEAKERS
            : BASSInit.BASS_DEVICE_DEFAULT;
        if (!BassApi.BASS_Init(-1, 44100, initFlags, IntPtr.Zero))
        {
            throw BassFailure("initialize the default audio device");
        }

        var pluginDirectory = Path.GetDirectoryName(typeof(BassPlayerPlugin).Assembly.Location)!;
        var flacPath = Path.Combine(
            pluginDirectory,
            "runtimes",
            nativeAsset.RuntimeIdentifier,
            "native",
            nativeAsset.FlacFileName);
        _flacPlugin = BassApi.BASS_PluginLoad(flacPath);
        if (_flacPlugin == 0)
        {
            BassApi.BASS_Free();
            throw BassFailure($"load the FLAC decoder '{flacPath}'");
        }

        _logger.LogInformation("BASS audio player initialized ({RuntimeIdentifier}).", nativeAsset.RuntimeIdentifier);
    }

    public PlayerState State { get; private set; } = PlayerState.Stopped;
    public Track? CurrentTrack { get; private set; }
    public PlayerTimeline Timeline
    {
        get
        {
            lock (_gate)
            {
                if (_stream == InvalidStream)
                {
                    return new PlayerTimeline(TimeSpan.Zero, CurrentTrack?.Duration);
                }

                var position = ChannelTime(BassApi.BASS_ChannelGetPosition(_stream)) ?? TimeSpan.Zero;
                var duration = ChannelTime(BassApi.BASS_ChannelGetLength(_stream)) ?? CurrentTrack?.Duration;
                var hasEnded = State == PlayerState.Playing &&
                               BassApi.BASS_ChannelIsActive(_stream) == BASSActive.BASS_ACTIVE_STOPPED;
                return new PlayerTimeline(position, duration, hasEnded);
            }
        }
    }

    public Task PlayAsync(
        Track track,
        PlaybackSource source,
        CancellationToken cancellationToken = default) =>
        PlayCoreAsync(track, source, null, cancellationToken);

    public Task PlayAsync(
        Track track,
        HttpPlaybackSource source,
        IPlaybackDownloadSink downloadSink,
        CancellationToken cancellationToken = default) =>
        PlayCoreAsync(track, source, downloadSink, cancellationToken);

    private Task PlayCoreAsync(
        Track track,
        PlaybackSource source,
        IPlaybackDownloadSink? downloadSink,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            FreeCurrentStream();
            var sourceDescription = source switch
            {
                FilePlaybackSource fileSource => CreateFileStream(fileSource),
                HttpPlaybackSource { IsDirectAudio: true } httpSource => CreateHttpStream(httpSource, downloadSink),
                _ => throw new NotSupportedException($"BASS Player does not support '{source.GetType().Name}'.")
            };
            if (_stream == InvalidStream)
            {
                throw BassFailure($"open '{sourceDescription}'");
            }

            // Apply before playback starts so new streams never briefly play at full volume.
            if (!BassApi.BASS_ChannelSetAttribute(_stream, BASSAttribute.BASS_ATTRIB_VOL, _volume))
            {
                var error = BassApi.BASS_ErrorGetCode();
                FreeCurrentStream();
                throw BassFailure("set the stream volume", error);
            }

            if (!BassApi.BASS_ChannelPlay(_stream, false))
            {
                var error = BassApi.BASS_ErrorGetCode();
                FreeCurrentStream();
                throw BassFailure($"play '{sourceDescription}'", error);
            }

            CurrentTrack = track;
            State = PlayerState.Playing;
            _logger.LogInformation(
                "Now playing {Title} from {Source}",
                track.Title ?? track.Id.RemoteId,
                sourceDescription);
        }

        return Task.CompletedTask;
    }

    private string CreateFileStream(FilePlaybackSource source)
    {
        var path = Path.GetFullPath(source.Path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local audio file no longer exists.", path);
        }

        _stream = BassApi.BASS_StreamCreateFile(path, 0, 0, BASSFlag.BASS_SAMPLE_FLOAT);
        return path;
    }

    private string CreateHttpStream(HttpPlaybackSource source, IPlaybackDownloadSink? downloadSink)
    {
        var headers = source.Headers.Select(header =>
        {
            if (header.Key.IndexOfAny(['\r', '\n']) >= 0 || header.Value.IndexOfAny(['\r', '\n']) >= 0)
            {
                throw new ArgumentException("HTTP playback headers cannot contain line breaks.");
            }

            return $"{header.Key}: {header.Value}";
        }).ToArray();
        _downloadForwarder = downloadSink is null ? null : new BassDownloadForwarder(downloadSink);
        // Keep the callback rooted before BASS begins receiving bytes, which
        // may happen before StreamCreateURL returns. Terminate every header.
        var downloadHeaders = source.Headers.Keys.Contains("Accept-Encoding", StringComparer.OrdinalIgnoreCase)
            ? headers
            : [.. headers, "Accept-Encoding: identity"];
        var sourceWithHeaders = string.Join("\r\n", new[] { source.Uri.ToString() }.Concat(downloadHeaders)) + "\r\n";
        _stream = BassApi.BASS_StreamCreateURL(
            sourceWithHeaders,
            0,
            BASSFlag.BASS_SAMPLE_FLOAT,
            _downloadForwarder?.DownloadCallback,
            IntPtr.Zero);
        if (_stream == InvalidStream)
        {
            var error = BassApi.BASS_ErrorGetCode();
            _downloadForwarder?.Dispose();
            _downloadForwarder = null;
            throw BassFailure("open the network audio stream", error);
        }
        _isHttpStream = true;
        _downloadForwarder?.SetStream(_stream);
        return source.Uri.ToString();
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stream != InvalidStream && !BassApi.BASS_ChannelPause(_stream))
            {
                throw BassFailure("pause playback");
            }

            if (_stream != InvalidStream)
            {
                State = PlayerState.Paused;
            }
        }

        return Task.CompletedTask;
    }

    public Task<double> GetVolumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            var volume = _volume;
            if (_stream != InvalidStream &&
                !BassApi.BASS_ChannelGetAttribute(_stream, BASSAttribute.BASS_ATTRIB_VOL, ref volume))
                throw BassFailure("read the stream volume");
            return Task.FromResult((double)volume);
        }
    }

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(volume) || volume is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 1.");
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stream != InvalidStream &&
                !BassApi.BASS_ChannelSetAttribute(_stream, BASSAttribute.BASS_ATTRIB_VOL, (float)volume))
                throw BassFailure("set the stream volume");
            _volume = (float)volume;
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stream != InvalidStream && !BassApi.BASS_ChannelPlay(_stream, false))
            {
                throw BassFailure("resume playback");
            }

            if (_stream != InvalidStream)
            {
                State = PlayerState.Playing;
            }
        }

        return Task.CompletedTask;
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));
        int stream;
        long bytes;
        bool resumeAfterSeeking;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stream == InvalidStream) throw new InvalidOperationException("No audio stream is loaded.");
            stream = _stream;
            var duration = ChannelTime(BassApi.BASS_ChannelGetLength(_stream));
            if (duration is { } length && position > length) position = length;
            bytes = BassApi.BASS_ChannelSeconds2Bytes(stream, position.TotalSeconds);
            if (bytes < 0)
            {
                throw BassFailure("convert the playback position", BassApi.BASS_ErrorGetCode());
            }

            resumeAfterSeeking = State == PlayerState.Playing;
            if (resumeAfterSeeking)
            {
                if (!BassApi.BASS_ChannelPause(stream))
                    throw BassFailure("pause while restoring the playback position", BassApi.BASS_ErrorGetCode());
                State = PlayerState.Paused;
            }
        }

        var wait = Stopwatch.StartNew();
        var waitingForDownload = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_stream != stream)
                    throw new InvalidOperationException("The audio stream changed while restoring the playback position.");

                if (BassApi.BASS_ChannelSetPosition(stream, bytes, BASSMode.BASS_POS_BYTE))
                {
                    if (resumeAfterSeeking)
                    {
                        if (!BassApi.BASS_ChannelPlay(stream, false))
                            throw BassFailure("resume after restoring the playback position", BassApi.BASS_ErrorGetCode());
                        State = PlayerState.Playing;
                    }
                    return;
                }

                // Capture the failing call's error before querying download state:
                // subsequent BASS calls overwrite the thread-local error code.
                var error = BassApi.BASS_ErrorGetCode();
                var connected = _isHttpStream &&
                    BassApi.BASS_StreamGetFilePosition(stream, BASSStreamFilePosition.BASS_FILEPOS_CONNECTED) > 0;
                var blocked = _isHttpStream &&
                    (BassApi.BASS_ChannelGetInfo(stream)?.flags & BASSFlag.BASS_STREAM_BLOCK) != 0;
                // A reported BASS_OK alongside a failed network seek is not
                // grounds to lose the restore attempt while data is arriving.
                if (!connected || blocked || error is not (BASSError.BASS_ERROR_POSITION or BASSError.BASS_OK))
                    throw BassFailure("restore the playback position", error);

                if (wait.Elapsed >= TimeSpan.FromSeconds(30))
                    throw new InvalidOperationException(
                        $"Timed out waiting for audio to buffer at {position:c}. The saved playback position is retained; try resume again.");

                if (!waitingForDownload)
                {
                    waitingForDownload = true;
                    _logger.LogInformation("Buffering audio to restore playback at {Position}.", position);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            FreeCurrentStream();
            CurrentTrack = null;
            State = PlayerState.Stopped;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            FreeCurrentStream();
            if (_flacPlugin != 0)
            {
                BassApi.BASS_PluginFree(_flacPlugin);
                _flacPlugin = 0;
            }

            BassApi.BASS_Free();
            _disposed = true;
            State = PlayerState.Stopped;
            CurrentTrack = null;
        }

        return ValueTask.CompletedTask;
    }

    private void FreeCurrentStream()
    {
        if (_stream != InvalidStream)
        {
            BassApi.BASS_ChannelStop(_stream);
            BassApi.BASS_StreamFree(_stream);
        }
        // StreamFree waits for native callbacks. Only release the delegate
        // and host sink after those callbacks have stopped.
        GC.KeepAlive(_downloadForwarder);
        _downloadForwarder?.Dispose();
        _downloadForwarder = null;
        _stream = InvalidStream;
        _isHttpStream = false;
    }

    private static InvalidOperationException BassFailure(string operation) =>
        new($"BASS could not {operation}. Error: {BassApi.BASS_ErrorGetCode()}.");

    private static InvalidOperationException BassFailure(string operation, BASSError error) =>
        error == BASSError.BASS_OK
            ? new($"BASS could not {operation}. The native call failed without an error code.")
            : new($"BASS could not {operation}. Error: {error}.");

    private static NativeAsset GetNativeAsset()
    {
        var operatingSystem = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "osx"
                    : null;
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm when OperatingSystem.IsLinux() => "arm",
            Architecture.Arm64 => "arm64",
            _ => null
        };
        if (operatingSystem is null || architecture is null)
        {
            throw new PlatformNotSupportedException(
                $"BASS is not bundled for {RuntimeInformation.OSDescription} " +
                $"({RuntimeInformation.ProcessArchitecture}).");
        }

        var flacFileName = operatingSystem switch
        {
            "win" => "bassflac.dll",
            "linux" => "libbassflac.so",
            "osx" => "libbassflac.dylib",
            _ => throw new PlatformNotSupportedException()
        };
        return new NativeAsset($"{operatingSystem}-{architecture}", flacFileName);
    }

    private TimeSpan? ChannelTime(long bytePosition)
    {
        if (bytePosition < 0)
        {
            return null;
        }

        var seconds = BassApi.BASS_ChannelBytes2Seconds(_stream, bytePosition);
        return double.IsFinite(seconds) && seconds >= 0 && seconds <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct NativeAsset(string RuntimeIdentifier, string FlacFileName);
}
