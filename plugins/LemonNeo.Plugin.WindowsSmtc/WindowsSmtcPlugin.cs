using LemonNeo.Plugin.Abstractions;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LemonNeo.Plugin.WindowsSmtc;

public sealed class WindowsSmtcPlugin : InteractivePluginBase
{
    private MediaPlayer? _owner;
    private SystemMediaTransportControls? _controls;
    private InteractivePlaybackSnapshot? _last;

    public override PluginDescriptor Descriptor { get; } = new("windows-smtc", "Windows System Media Transport Controls", "0.1.0");

    protected override ValueTask InitializeCoreAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A MediaPlayer owns a desktop SMTC session without relying on a console
        // HWND (which belongs to the terminal). Audio remains owned by the host.
        _owner = new MediaPlayer();
        _owner.CommandManager.IsEnabled = false;
        _controls = _owner.SystemMediaTransportControls;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.PlaybackPositionChangeRequested += OnPositionRequested;
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask UpdateAsync(InteractivePlaybackSnapshot snapshot, CancellationToken cancellationToken)
    {
        var controls = _controls!;
        controls.IsEnabled = snapshot.Track is not null;
        controls.IsPlayEnabled = snapshot.Controls.HasFlag(PlaybackControls.Play);
        controls.IsPauseEnabled = snapshot.Controls.HasFlag(PlaybackControls.Pause);
        controls.IsStopEnabled = snapshot.Controls.HasFlag(PlaybackControls.Stop);
        controls.IsNextEnabled = snapshot.Controls.HasFlag(PlaybackControls.Next);
        controls.IsPreviousEnabled = snapshot.Controls.HasFlag(PlaybackControls.Previous);
        controls.PlaybackStatus = snapshot.State switch
        {
            PlayerState.Playing => MediaPlaybackStatus.Playing,
            PlayerState.Paused => MediaPlaybackStatus.Paused,
            _ => snapshot.Track is null ? MediaPlaybackStatus.Closed : MediaPlaybackStatus.Stopped
        };
        controls.PlaybackRate = snapshot.State == PlayerState.Playing ? 1 : 0;
        if (_last is null || _last.Track != snapshot.Track)
        {
            var display = controls.DisplayUpdater;
            display.ClearAll();
            if (snapshot.Track is { } track)
            {
                display.Type = MediaPlaybackType.Music;
                display.MusicProperties.Title = track.Title;
                display.MusicProperties.Artist = string.Join(", ", track.Artists.Select(artist => artist.Name));
                display.MusicProperties.AlbumTitle = track.Album?.Title ?? "";
                if (track.ArtworkUri is { IsAbsoluteUri: true } artwork)
                {
                    try
                    {
                        if (artwork.IsFile)
                        {
                            var file = await StorageFile.GetFileFromPathAsync(artwork.LocalPath).AsTask(cancellationToken);
                            display.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                        }
                        else if (artwork.Scheme is "http" or "https")
                            display.Thumbnail = RandomAccessStreamReference.CreateFromUri(artwork);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) { Logger.LogWarning(exception, "Could not publish media artwork."); }
                }
            }
            display.Update();
        }
        var duration = snapshot.Duration ?? snapshot.Position;
        var seek = snapshot.Controls.HasFlag(PlaybackControls.Seek) && snapshot.Duration is not null;
        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = duration,
            Position = snapshot.Position,
            MinSeekTime = seek ? TimeSpan.Zero : snapshot.Position,
            MaxSeekTime = seek ? duration : snapshot.Position
        });
        _last = snapshot;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        var command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => PlaybackCommand.Play,
            SystemMediaTransportControlsButton.Pause => PlaybackCommand.Pause,
            SystemMediaTransportControlsButton.Stop => PlaybackCommand.Stop,
            SystemMediaTransportControlsButton.Next => PlaybackCommand.Next,
            SystemMediaTransportControlsButton.Previous => PlaybackCommand.Previous,
            _ => (PlaybackCommand?)null
        };
        if (command is { } value) RequestCommand(value);
    }

    private void OnPositionRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        if (Playback.Snapshot.Controls.HasFlag(PlaybackControls.Seek))
            RequestCommand(PlaybackCommand.Seek, args.RequestedPlaybackPosition);
    }

    protected override ValueTask ShutdownCoreAsync()
    {
        try
        {
            if (_controls is { } controls)
            {
                controls.ButtonPressed -= OnButtonPressed;
                controls.PlaybackPositionChangeRequested -= OnPositionRequested;
                controls.PlaybackStatus = MediaPlaybackStatus.Closed;
                controls.DisplayUpdater.ClearAll();
                controls.DisplayUpdater.Update();
                controls.IsEnabled = false;
            }
        }
        finally
        {
            _controls = null;
            _owner?.Dispose();
            _owner = null;
        }
        return ValueTask.CompletedTask;
    }
}
