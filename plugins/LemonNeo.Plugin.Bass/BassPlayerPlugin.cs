using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using Un4seen.Bass;
using BassApi = Un4seen.Bass.Bass;

namespace LemonNeo.Plugin.Bass;

public sealed class BassPlayerPlugin : ILemonPlugin, IPlayerProvider
{
    private BassPlayer? _player;

    public PluginDescriptor Descriptor { get; } = new(
        "org.lemonneo.bass",
        "BASS Player",
        "1.0.0");

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

internal sealed class BassPlayer : IPlayer, IAsyncDisposable
{
    private const int InvalidStream = 0;
    private readonly object _gate = new();
    private readonly ILogger<BassPlayer> _logger;
    private int _stream;
    private int _flacPlugin;
    private bool _disposed;

    public BassPlayer(ILogger<BassPlayer> logger)
    {
        _logger = logger;
        BassNet.Registration("lemon.app@qq.com", "2X52325160022");
        if (!BassApi.BASS_Init(-1, 44100, BASSInit.BASS_DEVICE_CPSPEAKERS, IntPtr.Zero))
        {
            throw BassFailure("initialize the default audio device");
        }

        var pluginDirectory = Path.GetDirectoryName(typeof(BassPlayerPlugin).Assembly.Location)!;
        var architecture = Environment.Is64BitProcess ? "win-x64" : "win-x86";
        var flacPath = Path.Combine(pluginDirectory, "runtimes", architecture, "native", "bassflac.dll");
        _flacPlugin = BassApi.BASS_PluginLoad(flacPath);
        if (_flacPlugin == 0)
        {
            BassApi.BASS_Free();
            throw BassFailure($"load the FLAC decoder '{flacPath}'");
        }

        _logger.LogInformation("BASS audio player initialized ({Architecture}).", architecture);
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ThrowIfDisposed();
            FreeCurrentStream();
            var sourceDescription = source switch
            {
                FilePlaybackSource fileSource => CreateFileStream(fileSource),
                HttpPlaybackSource { IsDirectAudio: true } httpSource => CreateHttpStream(httpSource),
                _ => throw new NotSupportedException($"BASS Player does not support '{source.GetType().Name}'.")
            };
            if (_stream == InvalidStream)
            {
                throw BassFailure($"open '{sourceDescription}'");
            }

            if (!BassApi.BASS_ChannelPlay(_stream, false))
            {
                FreeCurrentStream();
                throw BassFailure($"play '{sourceDescription}'");
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

    private string CreateHttpStream(HttpPlaybackSource source)
    {
        var headers = source.Headers.Select(header =>
        {
            if (header.Key.IndexOfAny(['\r', '\n']) >= 0 || header.Value.IndexOfAny(['\r', '\n']) >= 0)
            {
                throw new ArgumentException("HTTP playback headers cannot contain line breaks.");
            }

            return $"{header.Key}: {header.Value}";
        });
        var sourceWithHeaders = string.Join("\r\n", new[] { source.Uri.ToString() }.Concat(headers));
        _stream = BassApi.BASS_StreamCreateURL(
            sourceWithHeaders,
            0,
            BASSFlag.BASS_SAMPLE_FLOAT,
            null,
            IntPtr.Zero);
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
        if (_stream == InvalidStream)
        {
            return;
        }

        BassApi.BASS_ChannelStop(_stream);
        BassApi.BASS_StreamFree(_stream);
        _stream = InvalidStream;
    }

    private static InvalidOperationException BassFailure(string operation) =>
        new($"BASS could not {operation}. Error: {BassApi.BASS_ErrorGetCode()}.");

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
}
