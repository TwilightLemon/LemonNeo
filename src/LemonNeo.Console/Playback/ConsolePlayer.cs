using LemonNeo.Abstractions.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;
using System.Diagnostics;

namespace LemonNeo.ConsoleApp;

internal sealed class ConsolePlayer(TextWriter output) : ISeekablePlayer
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = new();
    private TimeSpan _elapsed;
    private PlayerState _state;
    private Track? _currentTrack;

    public PlayerState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public Track? CurrentTrack
    {
        get
        {
            lock (_gate)
            {
                return _currentTrack;
            }
        }
    }

    public PlayerTimeline Timeline
    {
        get
        {
            lock (_gate)
            {
                var position = _elapsed + (_state == PlayerState.Playing ? _clock.Elapsed : TimeSpan.Zero);
                var duration = _currentTrack?.Duration;
                if (duration is { } knownDuration && position > knownDuration)
                {
                    position = knownDuration;
                }

                return new PlayerTimeline(
                    position,
                    duration,
                    _state == PlayerState.Playing && duration is not null && position >= duration);
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
            _currentTrack = track;
            _elapsed = TimeSpan.Zero;
            _clock.Restart();
            _state = PlayerState.Playing;

            output.WriteLine("Now Playing");
            output.WriteLine(track.Title);
            output.WriteLine(track.Artists.Count == 0
                ? $"Provider: {track.Id.ProviderId}"
                : string.Join(", ", track.Artists.Select(artist => artist.Name)));
            output.WriteLine();
            output.WriteLine("Source:");
            output.WriteLine(source switch
            {
                HttpPlaybackSource http => $"HTTPS{Environment.NewLine}{http.Uri}",
                FilePlaybackSource file => $"File{Environment.NewLine}{file.Path}",
                _ => source.GetType().Name
            });
        }

        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state == PlayerState.Playing)
            {
                _elapsed += _clock.Elapsed;
                _clock.Reset();
                _state = PlayerState.Paused;
            }
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state == PlayerState.Paused)
            {
                _clock.Restart();
                _state = PlayerState.Playing;
            }
        }

        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));
        lock (_gate)
        {
            _elapsed = _currentTrack?.Duration is { } duration && position > duration ? duration : position;
            if (_state == PlayerState.Playing) _clock.Restart();
            else _clock.Reset();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _clock.Reset();
            _elapsed = TimeSpan.Zero;
            _state = PlayerState.Stopped;
            _currentTrack = null;
        }

        return Task.CompletedTask;
    }
}

internal sealed class ConsolePlayerProvider(TextWriter output) : IPlayerProvider
{
    public string Id => "console";
    public PlayerDescriptor Descriptor { get; } = new(
        "Console Player",
        "Fallback player that prints the resolved source without decoding audio.");
    public int Priority => int.MinValue;
    public IPlayer Player { get; } = new ConsolePlayer(output);
    public bool CanPlay(PlaybackSource source) => true;
}
