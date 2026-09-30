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
        "Native BASS decoder and audio output for local files.");
    public int Priority => 100;
    public IPlayer Player => _player ?? throw new InvalidOperationException("The BASS player is not initialized.");
    public bool CanPlay(PlaybackSource source) => source is FilePlaybackSource;

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
    public TrackReference? CurrentTrack { get; private set; }

    public Task PlayAsync(
        TrackReference track,
        PlaybackSource source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is not FilePlaybackSource fileSource)
        {
            throw new NotSupportedException($"BASS Player does not support '{source.GetType().Name}'.");
        }

        var path = Path.GetFullPath(fileSource.Path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The local audio file no longer exists.", path);
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            FreeCurrentStream();
            _stream = BassApi.BASS_StreamCreateFile(path, 0, 0, BASSFlag.BASS_SAMPLE_FLOAT);
            if (_stream == InvalidStream)
            {
                throw BassFailure($"decode '{path}'");
            }

            if (!BassApi.BASS_ChannelPlay(_stream, false))
            {
                FreeCurrentStream();
                throw BassFailure($"play '{path}'");
            }

            CurrentTrack = track;
            State = PlayerState.Playing;
            _logger.LogInformation("Now playing {Title} from {Path}", track.Title ?? track.Id.RemoteId, path);
        }

        return Task.CompletedTask;
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
