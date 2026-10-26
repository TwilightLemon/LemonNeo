using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Plugins;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using Microsoft.Extensions.Logging;
using Un4seen.Bass;
using BassApi = Un4seen.Bass.Bass;

namespace LemonNeo.Plugin.Bass;

public sealed class BassPlayerPlugin : ILemonPlugin, IPlayerProvider
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aiff", ".flac", ".mp3", ".ogg", ".wav"
    };

    private BassPlayer? _player;

    public PluginDescriptor Descriptor { get; } = new(
        "LemonNeo.Plugin.Bass",
        "BASS Player",
        "1.1.0");

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _player = new BassPlayer(context.LoggerFactory.CreateLogger<BassPlayer>(), context.NetworkProxy);
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

    public string Id => "bass";

    PlayerDescriptor IPlayerProvider.Descriptor { get; } = new(
        "BASS Player",
        "Native BASS decoder and audio output for local files and direct HTTP streams.");

    public IReadOnlyCollection<string> SupportedExtensions => Extensions;
    public IPlayer Player => _player ?? throw new InvalidOperationException("The BASS player is not initialized.");

    public bool CanPlay(PlaybackSource source)
    {
        return source switch
        {
            FilePlaybackSource file => SupportsExtension(file.Path),
            // Remote URLs frequently omit or disguise the filename; BASS probes the stream itself.
            HttpPlaybackSource { IsDirectAudio: true } => true,
            _ => false
        };
    }

    private static bool SupportsExtension(string path)
    {
        return Extensions.Contains(Path.GetExtension(path));
    }
}

internal sealed class BassPlayer : IPlayer, IStreamingPlayer, IAudioInfoPlayer, IFftPlayer, IAsyncDisposable
{
    private const int FftBinCountValue = 512;
    private const int InvalidStream = 0;
    private readonly float[] _fftData = new float[FftBinCountValue];
    private readonly object _gate = new();
    private readonly ILogger<BassPlayer> _logger;
    private readonly IWebProxy? _networkProxy;
    private readonly int _device;
    private bool _disposed;
    private readonly Dictionary<int, PreparedStream> _streams = [];
    private readonly List<Task<IPreparedPlayback>> _preparations = [];
    private int _flacPlugin;
    private bool _isHttpStream;
    private int _stream;
    private float _volume = 1;

    public BassPlayer(ILogger<BassPlayer> logger, IWebProxy? networkProxy, int device = -1)
    {
        _logger = logger;
        _networkProxy = networkProxy;
        var nativeAsset = GetNativeAsset();
        BassNet.Registration("lemon.app@qq.com", "2X52325160022");
        var initFlags = OperatingSystem.IsWindows()
            ? BASSInit.BASS_DEVICE_CPSPEAKERS
            : BASSInit.BASS_DEVICE_DEFAULT;
        if (!BassApi.BASS_Init(device, 44100, initFlags, IntPtr.Zero))
            throw BassFailure("initialize the default audio device");
        _device = BassApi.BASS_GetDevice();

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
            BassApi.BASS_SetDevice(_device);
            BassApi.BASS_Free();
            throw BassFailure($"load the FLAC decoder '{flacPath}'");
        }

        _logger.LogInformation("BASS audio player initialized ({RuntimeIdentifier}).", nativeAsset.RuntimeIdentifier);
    }

    public async ValueTask DisposeAsync()
    {
        Task[] preparations;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            preparations = _preparations.ToArray();
        }
        try { await Task.WhenAll(preparations); }
        catch (Exception) { /* Each preparation owns and releases its failed resources. */ }
        int[] streams;
        lock (_gate) streams = _streams.Keys.ToArray();
        foreach (var stream in streams) ReleaseStream(stream);
        lock (_gate)
        {
            if (_flacPlugin != 0)
            {
                BassApi.BASS_PluginFree(_flacPlugin);
                _flacPlugin = 0;
            }

            BassApi.BASS_SetDevice(_device);
            BassApi.BASS_Free();
            State = PlayerState.Stopped;
            CurrentTrack = null;
        }
    }

    public AudioStreamInfo? AudioInfo
    {
        get
        {
            lock (_gate)
            {
                if (_stream == InvalidStream) return null;
                var channel = BassApi.BASS_ChannelGetInfo(_stream);
                float bitrate = 0;
                var hasBitrate =
                    BassApi.BASS_ChannelGetAttribute(_stream, BASSAttribute.BASS_ATTRIB_BITRATE, ref bitrate);
                return BassAudioInfo.FromChannel(channel, hasBitrate ? bitrate : null);
            }
        }
    }

    public PlayerState State { get; private set; } = PlayerState.Stopped;
    public Track? CurrentTrack { get; private set; }
    public int FftBinCount => FftBinCountValue;

    public PlayerTimeline Timeline
    {
        get
        {
            lock (_gate)
            {
                if (_stream == InvalidStream) return new PlayerTimeline(TimeSpan.Zero, CurrentTrack?.Duration);

                var position = ChannelTime(BassApi.BASS_ChannelGetPosition(_stream)) ?? TimeSpan.Zero;
                var duration = ChannelTime(BassApi.BASS_ChannelGetLength(_stream)) ?? CurrentTrack?.Duration;
                var hasEnded = State == PlayerState.Playing &&
                               BassApi.BASS_ChannelIsActive(_stream) == BASSActive.BASS_ACTIVE_STOPPED;
                return new PlayerTimeline(position, duration, hasEnded);
            }
        }
    }

    public bool TryGetFftData(Span<float> destination)
    {
        if (destination.Length < FftBinCountValue) return false;
        lock (_gate)
        {
            if (_disposed || _stream == InvalidStream || State != PlayerState.Playing) return false;
            var read = BassApi.BASS_ChannelGetData(
                _stream,
                _fftData,
                (int)(BASSData.BASS_DATA_FFT1024 | BASSData.BASS_DATA_FFT_REMOVEDC));
            if (read < 0) return false;
            _fftData.AsSpan().CopyTo(destination);
            return true;
        }
    }

    public Task PlayAsync(
        Track track,
        PlaybackSource source,
        CancellationToken cancellationToken = default)
    {
        return PlayCoreAsync(track, source, null, cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_stream != InvalidStream && !BassApi.BASS_ChannelPause(_stream)) throw BassFailure("pause playback");

            if (_stream != InvalidStream) State = PlayerState.Paused;
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
                throw BassFailure("resume playback");

            if (_stream != InvalidStream) State = PlayerState.Playing;
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
            if (bytes < 0) throw BassFailure("convert the playback position", BassApi.BASS_ErrorGetCode());

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
                    throw new InvalidOperationException(
                        "The audio stream changed while restoring the playback position.");

                if (BassApi.BASS_ChannelSetPosition(stream, bytes, BASSMode.BASS_POS_BYTE))
                {
                    if (resumeAfterSeeking)
                    {
                        if (!BassApi.BASS_ChannelPlay(stream, false))
                            throw BassFailure("resume after restoring the playback position",
                                BassApi.BASS_ErrorGetCode());
                        State = PlayerState.Playing;
                    }

                    return;
                }

                // Capture the failing call's error before querying download state:
                // subsequent BASS calls overwrite the thread-local error code.
                var error = BassApi.BASS_ErrorGetCode();
                var connected = _isHttpStream &&
                                BassApi.BASS_StreamGetFilePosition(stream,
                                    BASSStreamFilePosition.BASS_FILEPOS_CONNECTED) > 0;
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
        PreparedStream? current;
        lock (_gate)
        {
            ThrowIfDisposed();
            current = _streams.GetValueOrDefault(_stream);
        }
        current?.Dispose();
        return Task.CompletedTask;
    }

    public Task PlayAsync(
        Track track,
        HttpPlaybackSource source,
        IPlaybackDownloadSink downloadSink,
        CancellationToken cancellationToken = default)
    {
        return PlayCoreAsync(track, source, downloadSink, cancellationToken);
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

    private async Task PlayCoreAsync(
        Track track,
        PlaybackSource source,
        IPlaybackDownloadSink? downloadSink,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareAsync(track, source, downloadSink, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreparedStream? previous;
            lock (_gate) previous = _streams.GetValueOrDefault(_stream);
            prepared.Activate(_volume);
            previous?.Dispose();
        }
        catch { prepared.Dispose(); throw; }
    }

    public Task<IPreparedPlayback> PrepareAsync(Track track, PlaybackSource source,
        IPlaybackDownloadSink? downloadSink = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            _preparations.RemoveAll(task => task.IsCompleted);
            var task = Task.Run(() => PrepareCore(track, source, downloadSink, cancellationToken), CancellationToken.None);
            _preparations.Add(task);
            return task;
        }
    }

    private IPreparedPlayback PrepareCore(Track track, PlaybackSource source, IPlaybackDownloadSink? sink,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!BassApi.BASS_SetDevice(_device)) throw BassFailure("select the audio device");
        var forwarder = sink is null ? null : new BassDownloadForwarder(sink);
        var stream = InvalidStream;
        try
        {
            // BASS_StreamCreateURL may block in native I/O. No active-player lock is held,
            // and the returned handle belongs exclusively to this request.
            stream = source switch
            {
                FilePlaybackSource file => CreateFileStream(file),
                HttpPlaybackSource { IsDirectAudio: true } http => CreateHttpStream(http, forwarder),
                _ => throw new NotSupportedException($"BASS Player does not support '{source.GetType().Name}'.")
            };
            if (stream == InvalidStream) throw BassFailure("open the audio stream");
            token.ThrowIfCancellationRequested();
            forwarder?.SetStream(stream);
            lock (_gate)
            {
                ThrowIfDisposed();
                var prepared = new PreparedStream(this, track, stream, source is HttpPlaybackSource, forwarder);
                _streams.Add(stream, prepared);
                return prepared;
            }
        }
        catch
        {
            if (stream != InvalidStream) BassApi.BASS_StreamFree(stream);
            GC.KeepAlive(forwarder);
            forwarder?.Dispose();
            throw;
        }
    }

    private static int CreateFileStream(FilePlaybackSource source)
    {
        var path = Path.GetFullPath(source.Path);
        if (!File.Exists(path)) throw new FileNotFoundException("The local audio file no longer exists.", path);

        return BassApi.BASS_StreamCreateFile(path, 0, 0, BASSFlag.BASS_SAMPLE_FLOAT);
    }

    private int CreateHttpStream(HttpPlaybackSource source, BassDownloadForwarder? forwarder)
    {
        var headers = source.Headers.Select(header =>
        {
            if (header.Key.IndexOfAny(['\r', '\n']) >= 0 || header.Value.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("HTTP playback headers cannot contain line breaks.");

            return $"{header.Key}: {header.Value}";
        }).ToArray();
        // Keep the callback rooted before BASS begins receiving bytes, which
        // may happen before StreamCreateURL returns. Terminate every header.
        var downloadHeaders = source.Headers.Keys.Contains("Accept-Encoding", StringComparer.OrdinalIgnoreCase)
            ? headers
            : [.. headers, "Accept-Encoding: identity"];
        var sourceWithHeaders = string.Join("\r\n", new[] { source.Uri.ToString() }.Concat(downloadHeaders)) + "\r\n";
        ApplyNetworkProxy(source.Uri);
        var stream = BassApi.BASS_StreamCreateURL(
            sourceWithHeaders,
            0,
            BASSFlag.BASS_SAMPLE_FLOAT,
            forwarder?.DownloadCallback,
            IntPtr.Zero);
        if (stream == InvalidStream)
        {
            var error = BassApi.BASS_ErrorGetCode();
            throw BassFailure("open the network audio stream", error);
        }

        return stream;
    }

    /// <summary>BASS performs its own downloads, so mirror the host proxy into its native configuration.</summary>
    private void ApplyNetworkProxy(Uri destination)
    {
        var value = "";
        var proxy = _networkProxy;
        if (proxy is not null && !proxy.IsBypassed(destination))
        {
            var address = proxy.GetProxy(destination);
            if (address is not null && !address.Equals(destination))
            {
                var builder = new UriBuilder(address);
                if (builder.UserName.Length == 0 &&
                    proxy.Credentials?.GetCredential(address, "Basic") is { } credentials)
                {
                    builder.UserName = credentials.UserName;
                    builder.Password = credentials.Password;
                }

                value = builder.Uri.GetLeftPart(UriPartial.Authority);
            }
        }

        if (!BassApi.BASS_SetConfigString(BASSConfig.BASS_CONFIG_NET_PROXY, value))
            _logger.LogWarning("Could not apply the network proxy to the BASS stream.");
    }

    private void ReleaseStream(int stream, PreparedStream? expected = null)
    {
        BassDownloadForwarder? forwarder;
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var owned) || expected is not null && !ReferenceEquals(owned, expected)) return;
            _streams.Remove(stream);
            forwarder = owned.Forwarder;
            if (_stream == stream)
            {
                _stream = InvalidStream;
                _isHttpStream = false;
                State = PlayerState.Stopped;
                CurrentTrack = null;
            }
        }
        BassApi.BASS_ChannelStop(stream);
        BassApi.BASS_StreamFree(stream);
        GC.KeepAlive(forwarder);
        forwarder?.Dispose();
    }

    private sealed class PreparedStream(BassPlayer owner, Track track, int stream, bool isHttp,
        BassDownloadForwarder? forwarder) : IPreparedPlayback
    {
        private int _disposed;
        public BassDownloadForwarder? Forwarder { get; } = forwarder;
        public AudioStreamInfo? AudioInfo
        {
            get
            {
                lock (owner._gate)
                {
                    if (!owner._streams.TryGetValue(stream, out var owned) || !ReferenceEquals(owned, this)) return null;
                    float bitrate = 0;
                    var available = BassApi.BASS_ChannelGetAttribute(stream, BASSAttribute.BASS_ATTRIB_BITRATE, ref bitrate);
                    return BassAudioInfo.FromChannel(BassApi.BASS_ChannelGetInfo(stream), available ? bitrate : null);
                }
            }
        }

        public void Activate(double volume)
        {
            lock (owner._gate)
            {
                owner.ThrowIfDisposed();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (!owner._streams.TryGetValue(stream, out var owned) || !ReferenceEquals(owned, this))
                    throw new ObjectDisposedException(nameof(PreparedStream));
                if (!BassApi.BASS_ChannelSetAttribute(stream, BASSAttribute.BASS_ATTRIB_VOL, (float)volume))
                    throw BassFailure("set the stream volume");
                if (owner._stream != InvalidStream && owner._stream != stream)
                    BassApi.BASS_ChannelStop(owner._stream);
                if (!BassApi.BASS_ChannelPlay(stream, false)) throw BassFailure("start playback");
                owner._stream = stream;
                owner._volume = (float)volume;
                owner._isHttpStream = isHttp;
                owner.CurrentTrack = track;
                owner.State = PlayerState.Playing;
                owner._logger.LogInformation("Now playing {Title}.", track.Title);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.ReleaseStream(stream, this);
        }
    }

    private static InvalidOperationException BassFailure(string operation)
    {
        return new InvalidOperationException($"BASS could not {operation}. Error: {BassApi.BASS_ErrorGetCode()}.");
    }

    private static InvalidOperationException BassFailure(string operation, BASSError error)
    {
        return error == BASSError.BASS_OK
            ? new InvalidOperationException(
                $"BASS could not {operation}. The native call failed without an error code.")
            : new InvalidOperationException($"BASS could not {operation}. Error: {error}.");
    }

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
            throw new PlatformNotSupportedException(
                $"BASS is not bundled for {RuntimeInformation.OSDescription} " +
                $"({RuntimeInformation.ProcessArchitecture}).");

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
        if (bytePosition < 0) return null;

        var seconds = BassApi.BASS_ChannelBytes2Seconds(_stream, bytePosition);
        return double.IsFinite(seconds) && seconds >= 0 && seconds <= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct NativeAsset(string RuntimeIdentifier, string FlacFileName);
}
