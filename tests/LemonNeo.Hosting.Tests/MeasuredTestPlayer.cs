using LemonNeo.Abstractions.Playback;
using LemonNeo.Core.Music;
using LemonNeo.Core.Playback;

namespace LemonNeo.Hosting.Tests;

internal sealed class MeasuredTestPlayer : IPlayerProvider, IStreamingPlayer, IAudioInfoPlayer
{
    private readonly TestPlayer _player = new();
    public string Id => "measured-player";
    public PlayerDescriptor Descriptor { get; } = new("Measured player", "Supplies independent decoder measurements.");
    public int Priority => 100;
    public IPlayer Player => this;
    public PlayerState State => _player.State;
    public Track? CurrentTrack => _player.CurrentTrack;
    public PlayerTimeline Timeline => _player.Timeline;
    public AudioStreamInfo? Measurements { get; set; }
    public AudioStreamInfo? AudioInfo => State == PlayerState.Stopped ? null : Measurements;
    public bool CanPlay(PlaybackSource source) => true;
    public Task PlayAsync(Track track, PlaybackSource source, CancellationToken cancellationToken = default) =>
        _player.PlayAsync(track, source, cancellationToken);
    public async Task PlayAsync(Track track, HttpPlaybackSource source, IPlaybackDownloadSink downloadSink, CancellationToken cancellationToken = default)
    {
        // A small stream can download completely before PlayAsync returns or AudioInfo is available.
        downloadSink.SetExpectedLength(4);
        downloadSink.Write([1, 2, 3, 4]);
        downloadSink.Complete();
        await _player.PlayAsync(track, source, cancellationToken);
    }
    public Task PauseAsync(CancellationToken cancellationToken = default) => _player.PauseAsync(cancellationToken);
    public Task ResumeAsync(CancellationToken cancellationToken = default) => _player.ResumeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken = default) => _player.StopAsync(cancellationToken);
}
