using System.Diagnostics;
using System.Text.Json;
using LemonNeo.Abstractions.Playback;
using LemonNeo.Abstractions.Plugins;
using Microsoft.Extensions.Logging;

namespace LemonNeo.Plugin.MacNowPlaying;

public sealed class MacNowPlayingPlugin : InteractivePluginBase
{
    private readonly CancellationTokenSource _readerCancellation = new();
    private Task? _errorTask;
    private Process? _helper;
    private Task? _readTask;

    public override PluginDescriptor Descriptor { get; } =
        new("LemonNeo.Plugin.MacNowPlaying", "macOS Now Playing", "0.1.0");

    protected override async ValueTask InitializeCoreAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS Now Playing requires macOS.");
        var directory = Path.GetDirectoryName(GetType().Assembly.Location)!;
        var executable = Path.Combine(directory, "LemonNeo.MediaControls.app", "Contents", "MacOS",
            "LemonNeo.MediaControls");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Build this plugin on macOS with Xcode Command Line Tools installed.",
                executable);
        // The helper owns AppKit's main run loop, leaving the console's main
        // thread and the player's audio output available to the host.
        var helper = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        try
        {
            helper.Start();
        }
        catch
        {
            helper.Dispose();
            throw;
        }

        _helper = helper;
        _errorTask = ReadErrorsAsync(_readerCancellation.Token);
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var ready = await _helper.StandardOutput.ReadLineAsync(startupCancellation.Token);
        using var handshake = ready is null ? null : JsonDocument.Parse(ready);
        if (handshake is null || !handshake.RootElement.TryGetProperty("ready", out var isReady) ||
            !isReady.GetBoolean())
            throw new InvalidOperationException("macOS media control helper did not start.");
        _readTask = ReadCommandsAsync(_readerCancellation.Token);
    }

    protected override async ValueTask UpdateAsync(InteractivePlaybackSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Serialize(new
        {
            title = snapshot.Track?.Title,
            artist =
                snapshot.Track is { } track ? string.Join(", ", track.Artists.Select(artist => artist.Name)) : null,
            album = snapshot.Track?.Album?.Title,
            trackId = snapshot.Track?.Id.ToString(),
            artwork = snapshot.Track?.ArtworkUri is { IsAbsoluteUri: true } artwork ? artwork.AbsoluteUri : null,
            state = (int)snapshot.State,
            position = snapshot.Position.TotalSeconds,
            duration = snapshot.Duration?.TotalSeconds,
            controls = (int)snapshot.Controls
        });
        await _helper!.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
        await _helper.StandardInput.FlushAsync(cancellationToken);
    }

    private async Task ReadCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _helper!.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var command = (PlaybackCommand)document.RootElement.GetProperty("command").GetInt32();
                    TimeSpan? position = document.RootElement.TryGetProperty("position", out var value)
                        ? TimeSpan.FromSeconds(value.GetDouble())
                        : null;
                    if (Enum.IsDefined(command)) RequestCommand(command, position);
                }
                catch (Exception exception)
                {
                    Logger.LogWarning(exception, "Invalid macOS media command.");
                }

            if (!cancellationToken.IsCancellationRequested) Logger.LogWarning("macOS media control helper exited.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception)
        {
            Logger.LogWarning(exception, "macOS media command channel closed.");
        }
    }

    private async Task ReadErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _helper!.StandardError.ReadLineAsync(cancellationToken) is { } line)
                Logger.LogDebug("macOS media controls: {Message}", line);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    protected override async ValueTask ShutdownCoreAsync()
    {
        try
        {
            if (_helper is { HasExited: false })
            {
                // EOF makes the helper clear Now Playing and unregister all targets.
                _helper.StandardInput.Close();
                try
                {
                    await _helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (TimeoutException)
                {
                    _helper.Kill(true);
                    await _helper.WaitForExitAsync();
                }
            }
        }
        finally
        {
            await _readerCancellation.CancelAsync();
            if (_readTask is not null) await _readTask;
            if (_errorTask is not null) await _errorTask;
            _helper?.Dispose();
            _helper = null;
            _readerCancellation.Dispose();
        }
    }
}