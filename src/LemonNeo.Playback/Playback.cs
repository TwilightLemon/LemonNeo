using LemonNeo.Core;
using LemonNeo.Plugin.Abstractions;

namespace LemonNeo.Playback;

public interface IPlaybackQueue
{
    IReadOnlyList<TrackReference> Items { get; }
    void Enqueue(TrackReference track);
    TrackReference? Dequeue();
    void Clear();
}

public sealed class PlaybackQueue : IPlaybackQueue
{
    private readonly List<TrackReference> _items = [];

    public IReadOnlyList<TrackReference> Items => _items;

    public void Enqueue(TrackReference track) => _items.Add(track);

    public TrackReference? Dequeue()
    {
        if (_items.Count == 0)
        {
            return null;
        }

        var item = _items[0];
        _items.RemoveAt(0);
        return item;
    }

    public void Clear() => _items.Clear();
}

public sealed class ConsolePlayer(TextWriter output) : IPlayer
{
    public PlayerState State { get; private set; }
    public TrackReference? CurrentTrack { get; private set; }

    public Task PlayAsync(
        TrackReference track,
        PlaybackSource source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentTrack = track;
        State = PlayerState.Playing;

        output.WriteLine("Now Playing");
        output.WriteLine(track.Title ?? track.Id.RemoteId);
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

        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State == PlayerState.Playing)
        {
            State = PlayerState.Paused;
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (State == PlayerState.Paused)
        {
            State = PlayerState.Playing;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        State = PlayerState.Stopped;
        CurrentTrack = null;
        return Task.CompletedTask;
    }
}

public sealed class ConsolePlayerProvider(TextWriter output) : IPlayerProvider
{
    public string Id => "console";
    public PlayerDescriptor Descriptor { get; } = new(
        "Console Player",
        "Fallback player that prints the resolved source without decoding audio.");
    public int Priority => int.MinValue;
    public IPlayer Player { get; } = new ConsolePlayer(output);
    public bool CanPlay(PlaybackSource source) => true;
}
